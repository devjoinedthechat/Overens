// Starts the whole system on one machine: `dotnet run --project src/AppHost`.
//
// Postgres, the Durable Task Scheduler's emulator and Keycloak run in containers. The schema
// is applied, then the worker and the API start. With ANTHROPIC_API_KEY set the worker
// extracts with Claude; without it, it replays the answers recorded for the corpus.
var builder = DistributedApplication.CreateBuilder(args);

var apiPassword = builder.AddParameter("api-db-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true);
var workerPassword = builder.AddParameter("worker-db-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("overens");
var endpoint = postgres.Resource.PrimaryEndpoint;
ReferenceExpression As(string role, IResourceBuilder<ParameterResource> password) => ReferenceExpression.Create(
    $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=overens;Username={role};Password={password}");

var migrate = builder.AddProject<Projects.Overens_Migrate>("migrate")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("API_DB_PASSWORD", apiPassword)
    .WithEnvironment("WORKER_DB_PASSWORD", workerPassword);

// Pinned by digest: the image's version tags are not in order, and "latest" moves.
var scheduler = builder.AddContainer("scheduler", "mcr.microsoft.com/dts/dts-emulator@sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719")
    .WithHttpEndpoint(port: 8090, targetPort: 8080, name: "grpc")
    .WithHttpEndpoint(port: 8092, targetPort: 8082, name: "dashboard");
const string Scheduler = "Endpoint=http://localhost:8090;TaskHub=default;Authentication=None";

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.8.2")
    .WithArgs("start-dev", "--import-realm")
    .WithBindMount("../../infra/keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management");
const string Authority = "http://localhost:8081/realms/overens";

builder.AddProject<Projects.Overens_Worker>("worker")
    .WithEnvironment("ConnectionStrings__worker", As("overens_worker", workerPassword))
    .WithEnvironment("ConnectionStrings__scheduler", Scheduler)
    .WithEnvironment("ANTHROPIC_API_KEY", builder.Configuration["ANTHROPIC_API_KEY"] ?? "")
    .WithEnvironment("RECORDINGS_DIR", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "evals", "recordings")))
    .WaitForCompletion(migrate)
    .WaitFor(scheduler);

builder.AddProject<Projects.Overens_Api>("api")
    .WithEnvironment("ConnectionStrings__api", As("overens_api", apiPassword))
    .WithEnvironment("ConnectionStrings__scheduler", Scheduler)
    .WithEnvironment("Auth__Authority", Authority)
    .WithEnvironment("Auth__Audience", "overens-api")
    .WaitForCompletion(migrate)
    .WaitFor(scheduler)
    .WaitFor(keycloak);

builder.Build().Run();

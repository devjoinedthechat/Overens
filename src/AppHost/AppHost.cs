// Starts the whole system on one machine: `dotnet run --project src/AppHost`.
var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres").AddDatabase("overens");

builder.AddProject<Projects.Overens_Api>("api")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();

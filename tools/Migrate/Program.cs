using Overens.Data;

// Run as the database's owner, before any service starts:
//
//   ConnectionStrings__overens   the owner's connection string
//   API_DB_PASSWORD              the password overens_api signs in with
//   WORKER_DB_PASSWORD           the password overens_worker signs in with
static string Setting(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"The setting {name} is missing.");

var owner = Setting("ConnectionStrings__overens");
await Migrator.MigrateAsync(owner);
await Migrator.SetRolePasswordsAsync(owner, Setting("API_DB_PASSWORD"), Setting("WORKER_DB_PASSWORD"));
Console.WriteLine("The schema is up to date.");

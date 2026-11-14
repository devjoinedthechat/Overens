using Overens.Data;

// Run as the database's owner.
//
//   (no arguments)        apply the schema and set the run-time roles' passwords; done before any service starts
//   erase-tenant <id>     remove everything a company has stored; the audit log keeps that it happened
//   verify-audit          check the audit log's chain of hashes
//
//   ConnectionStrings__overens   the owner's connection string
//   API_DB_PASSWORD              the password overens_api signs in with       (no arguments only)
//   WORKER_DB_PASSWORD           the password overens_worker signs in with    (no arguments only)
static string Setting(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"The setting {name} is missing.");

var owner = Setting("ConnectionStrings__overens");
switch (args)
{
    case []:
        await Migrator.MigrateAsync(owner);
        await Migrator.SetRolePasswordsAsync(owner, Setting("API_DB_PASSWORD"), Setting("WORKER_DB_PASSWORD"));
        Console.WriteLine("The schema is up to date.");
        return 0;
    case ["erase-tenant", var tenant]:
        Console.WriteLine($"Erased {tenant}: {await Migrator.EraseTenantAsync(owner, tenant)}");
        return 0;
    case ["verify-audit"]:
        var broken = await Migrator.VerifyAuditAsync(owner);
        Console.WriteLine(broken is null ? "The audit log's chain holds." : $"The audit log's chain is broken at entry {broken}.");
        return broken is null ? 0 : 1;
    default:
        Console.Error.WriteLine("Usage: [erase-tenant <id> | verify-audit]");
        return 2;
}

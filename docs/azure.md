# Running on Azure

How Overens is deployed to Azure, what is created, and what has been checked.

**The deployment has not been run.** Everything on this page is defined in the repository and checked as far as it can be without an Azure account. The table under "What is checked" says how far that goes.

## What is created

One resource group, from [`infra/azure/main.bicep`](../infra/azure/main.bicep):

| Resource | What it is for |
| --- | --- |
| Container Apps | The API, public over HTTPS, and the worker, with no way in from outside |
| Container Apps job | Applies the database schema as the database's owner, then exits |
| PostgreSQL flexible server | The database. It has no public address and is reached only from inside the virtual network |
| Durable Task Scheduler | Carries each run from upload to publication |
| Key Vault | Every secret: three connection strings, two role passwords, the model key |
| Container Registry | The three images |
| Application Insights | Traces and logs, sent by the environment's own collector |

## How it is kept apart

- **No password in a setting.** The apps read their secrets from the vault through managed identities. Each identity may read its own secrets and no others: the role is granted on the secret, not on the vault.
- **Three database roles, as on one machine.** The API's identity cannot read the worker's connection string, and neither can read the owner's. Only the schema job can.
- **The scheduler takes identities, not keys.** Only the API's and the worker's identities hold a role on the task hub.
- **The registry has no admin account.** Images are pulled by identity.
- **A ceiling on logs.** The log workspace stops taking data after one gigabyte in a day.

## Sign-in with Microsoft Entra ID

Each customer company signs in through its own directory. The directory's id, the `tid` claim, is the company.

| Setting | Value |
| --- | --- |
| `Auth:Authority` | `https://login.microsoftonline.com/organizations/v2.0` |
| `Auth:TenantClaim` | `tid` |
| `Auth:IssuerPerTenant` | `https://login.microsoftonline.com/{tenant}/v2.0` |
| `Auth:AllowedTenants` | The directories that may sign in. Empty lets in any |

A token is taken only if its issuer is its own directory's. A token issued by one company's directory that names another company is refused. `With_a_directory_per_company_a_token_is_taken_only_from_its_own_companys_issuer` tries exactly that.

Two app registrations are needed, made once by hand:

1. **The API.** Sign-in audience: accounts in any organisational directory. Expose a scope named `access`. Add two app roles, `specialist` and `employer`. Set the accepted token version to 2.
2. **The console.** A single-page application with the redirect address `https://<the API's address>/app/`, and permission to the API's `access` scope.

People are given the `specialist` or `employer` role under Enterprise applications in their own directory.

## Deploy, check, remove

```sh
scripts/azure.sh check       # compile and lint the infrastructure file; needs no account
scripts/azure.sh deploy      # create everything, push the images, apply the schema
scripts/azure.sh smoke       # ask the deployed API whether it is alive and ready
scripts/azure.sh teardown    # delete the resource group and everything in it
```

The settings each command needs are listed at the top of [`scripts/azure.sh`](../scripts/azure.sh). The three database passwords are made by the script, handed to the deployment and kept only in the vault.

The deployment runs in two passes, because the apps cannot be created before their images are in the registry.

## What is checked

| Part | Checked how | Run against Azure |
| --- | --- | --- |
| The infrastructure file | Compiled and linted in CI with no warnings | No |
| The three images | Built in CI | No |
| The system from its images | The whole smoke test, browser included, against the images with local stand-ins | No |
| Sign-in with a directory per company | An API test with tokens from two issuers | No |
| Managed identities, vault references, the private database, the scheduler's roles | Read against the reference, not run | No |
| The deploy script past `check` | Not run | No |

The stand-ins are Postgres for the managed database, the Durable Task Scheduler's emulator for the scheduler, and Keycloak for the identity provider. They are in [`infra/compose/compose.yml`](../infra/compose/compose.yml).

## What is likely to need fixing on the first real run

- **Names and versions.** A resource API version or a property the compiler accepts can still be refused by a region.
- **The database's certificate.** The connection strings ask for the server's certificate to be verified in full. If the image's certificate store lacks the issuer, this is where it shows.
- **PostgreSQL 17 against 18.** The tests run on 18.3 and the file asks Azure for 17.
- **The order of role assignments.** An app can start before its role on a secret has taken effect, and fail to read it once.
- **Telemetry.** That spans reach Application Insights through the environment's collector is configured and not seen.

// Overens on Azure, in one resource group.
//
//   Container Apps            the API (public, HTTPS) and the worker (no way in from outside)
//   Container Apps job        applies the database schema; run once per deployment
//   PostgreSQL flexible server, reachable only from inside the virtual network
//   Durable Task Scheduler    carries each run from upload to publication
//   Key Vault                 every secret; the apps read them through managed identities
//   Container Registry        the three images
//   Application Insights      traces and logs, sent by the environment's own collector
//
// It is deployed in two passes, because the apps cannot be created before their images
// exist: first with deployApps false, then, when the images are pushed, with it true.
// scripts/azure.sh does both.

targetScope = 'resourceGroup'

@description('Where everything is created.')
param location string = resourceGroup().location

@description('A short name every resource name is built from.')
@minLength(3)
@maxLength(12)
param name string = 'overens'

@description('False creates everything but the apps and the job. True adds them; their images must be in the registry.')
param deployApps bool = false

@description('The tag of the three images.')
param imageTag string = 'latest'

@secure()
@description('The password of the database owner. Used by the schema job and by nothing that serves people.')
param ownerPassword string

@secure()
@description('The password the API signs in to the database with.')
param apiPassword string

@secure()
@description('The password the worker signs in to the database with.')
param workerPassword string

@secure()
@description('The model provider key. Empty deploys without a model: findings get fixed wording and no agreement can be read.')
param modelKey string = ''

@description('The identity provider. With Microsoft Entra ID and a directory per company: https://login.microsoftonline.com/organizations/v2.0')
param authAuthority string

@description('The audience the API accepts tokens for: its application ID URI or client id.')
param authAudience string

@description('The client id of the console at the identity provider.')
param authWebClient string

@description('What the console asks for at sign-in, for example api://overens-api/access.')
param authScope string

@description('The claim that names a person\'s company. Microsoft Entra ID: tid.')
param authTenantClaim string = 'tid'

@description('The issuer each company\'s tokens must have, with {tenant} where the company goes.')
param authIssuerPerTenant string = '${environment().authentication.loginEndpoint}{tenant}/v2.0'

@description('The companies that may sign in, by the value of the tenant claim. Empty lets in every company the identity provider does.')
param authAllowedTenants array = []

var suffix = uniqueString(resourceGroup().id)
var database = 'overens'
var taskHub = 'overens'
var hasModel = !empty(modelKey)

// Built-in roles, by their ids.
var roles = {
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  durableTaskDataContributor: '0ad04412-c4d5-4796-b79c-f76d14c8d402'
}

// ---------------------------------------------------------------- network

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${name}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.40.0.0/16'] }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: '10.40.0.0/23'
          delegations: [{ name: 'apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'postgres'
        properties: {
          addressPrefix: '10.40.2.0/28'
          delegations: [{ name: 'postgres', properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' } }]
        }
      }
    ]
  }

  resource apps 'subnets' existing = {
    name: 'apps'
  }

  resource postgres 'subnets' existing = {
    name: 'postgres'
  }
}

// The database has a name only inside the network.
resource databaseZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: '${name}-${suffix}.private.postgres.database.azure.com'
  location: 'global'

  resource link 'virtualNetworkLinks' = {
    name: 'apps'
    location: 'global'
    properties: {
      registrationEnabled: false
      virtualNetwork: { id: network.id }
    }
  }
}

// ---------------------------------------------------------------- database

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${name}-pg-${suffix}'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '17'
    administratorLogin: 'overens_owner'
    administratorLoginPassword: ownerPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    // No public address. Only what runs inside the network can reach it.
    network: {
      delegatedSubnetResourceId: network::postgres.id
      privateDnsZoneArmResourceId: databaseZone.id
      publicNetworkAccess: 'Disabled'
    }
  }
  dependsOn: [databaseZone::link]

  resource overens 'databases' = {
    name: database
    properties: { charset: 'UTF8', collation: 'en_US.utf8' }
  }
}

var databaseHost = postgres.properties.fullyQualifiedDomainName
func connection(host string, database string, user string, password string) string =>
  'Host=${host};Database=${database};Username=${user};Password=${password};SSL Mode=VerifyFull'

// ---------------------------------------------------------------- identities

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-api'
  location: location
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-worker'
  location: location
}

resource migrateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-migrate'
  location: location
}

// Who each identity is to the directory, by the name used for it in the lists below.
var principals = {
  api: apiIdentity.properties.principalId
  worker: workerIdentity.properties.principalId
  migrate: migrateIdentity.properties.principalId
}

// ---------------------------------------------------------------- secrets

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${name}-kv-${take(suffix, 10)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }

  // Each connection string holds one role's password and no other.
  resource ownerConnection 'secrets' = {
    name: 'owner-connection'
    properties: { value: connection(databaseHost, database, 'overens_owner', ownerPassword) }
  }

  resource apiConnection 'secrets' = {
    name: 'api-connection'
    properties: { value: connection(databaseHost, database, 'overens_api', apiPassword) }
  }

  resource workerConnection 'secrets' = {
    name: 'worker-connection'
    properties: { value: connection(databaseHost, database, 'overens_worker', workerPassword) }
  }

  resource apiPasswordSecret 'secrets' = {
    name: 'api-db-password'
    properties: { value: apiPassword }
  }

  resource workerPasswordSecret 'secrets' = {
    name: 'worker-db-password'
    properties: { value: workerPassword }
  }

  resource model 'secrets' = if (hasModel) {
    name: 'model-key'
    properties: { value: modelKey }
  }
}

// An identity may read the secrets it is given below and no others: each role is granted
// on one secret, not on the vault.
var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)

resource apiReadsItsConnection 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault::apiConnection.id, 'api')
  scope: vault::apiConnection
  properties: { principalId: principals.api, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource workerReadsItsConnection 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault::workerConnection.id, 'worker')
  scope: vault::workerConnection
  properties: { principalId: principals.worker, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource migrateReadsTheOwnerConnection 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault::ownerConnection.id, 'migrate')
  scope: vault::ownerConnection
  properties: { principalId: principals.migrate, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource migrateReadsTheApiPassword 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault::apiPasswordSecret.id, 'migrate')
  scope: vault::apiPasswordSecret
  properties: { principalId: principals.migrate, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource migrateReadsTheWorkerPassword 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault::workerPasswordSecret.id, 'migrate')
  scope: vault::workerPasswordSecret
  properties: { principalId: principals.migrate, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource apiReadsTheModelKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasModel) {
  name: guid(vault.id, 'model-key', 'api')
  scope: vault::model
  properties: { principalId: principals.api, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

resource workerReadsTheModelKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasModel) {
  name: guid(vault.id, 'model-key', 'worker')
  scope: vault::model
  properties: { principalId: principals.worker, principalType: 'ServicePrincipal', roleDefinitionId: secretsUser }
}

// ---------------------------------------------------------------- registry

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${name}${suffix}'
  location: location
  sku: { name: 'Basic' }
  // No shared admin account. Images are pulled by the apps' identities.
  properties: { adminUserEnabled: false }
}

resource pullAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for identity in ['api', 'worker', 'migrate']: {
    name: guid(registry.id, identity, 'pull')
    scope: registry
    properties: {
      principalId: principals[identity]
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    }
  }
]

// ---------------------------------------------------------------- scheduler

resource scheduler 'Microsoft.DurableTask/schedulers@2025-11-01' = {
  name: '${name}-dts-${suffix}'
  location: location
  properties: {
    // It is reached over the internet and guarded by identity: only the two apps' identities hold a role on the task hub.
    ipAllowlist: ['0.0.0.0/0']
    sku: { name: 'Consumption' }
  }

  resource hub 'taskHubs' = {
    name: taskHub
  }
}

resource schedulerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for identity in ['api', 'worker']: {
    name: guid(scheduler.id, identity, 'tasks')
    scope: scheduler::hub
    properties: {
      principalId: principals[identity]
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.durableTaskDataContributor)
    }
  }
]

func schedulerConnection(endpoint string, hub string, clientId string) string =>
  'Endpoint=${endpoint};TaskHub=${hub};Authentication=ManagedIdentity;ClientID=${clientId}'

// ---------------------------------------------------------------- telemetry

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // A ceiling on what a day of logs may cost.
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------- apps

resource environment_ 'Microsoft.App/managedEnvironments@2026-07-01' = {
  name: '${name}-apps'
  location: location
  properties: {
    vnetConfiguration: { infrastructureSubnetId: network::apps.id }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    // The services send their spans and logs to the environment's own collector, which passes
    // them on. Nothing in the services knows where telemetry ends up.
    appInsightsConfiguration: { connectionString: insights.properties.ConnectionString }
    openTelemetryConfiguration: {
      tracesConfiguration: { destinations: ['appInsights'] }
      logsConfiguration: { destinations: ['appInsights'] }
    }
  }
}

var images = {
  api: '${registry.properties.loginServer}/overens-api:${imageTag}'
  worker: '${registry.properties.loginServer}/overens-worker:${imageTag}'
  migrate: '${registry.properties.loginServer}/overens-migrate:${imageTag}'
}

func vaultSecret(name string, vaultUri string, identity string) object => {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${name}'
  identity: identity
}

var probes = [
  { type: 'Liveness', httpGet: { path: '/alive', port: 8080 }, periodSeconds: 20 }
  { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10 }
  // Time to start before the other two count.
  { type: 'Startup', httpGet: { path: '/alive', port: 8080 }, periodSeconds: 5, failureThreshold: 24 }
]

resource api 'Microsoft.App/containerApps@2025-07-01' = if (deployApps) {
  name: '${name}-api'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity.id}': {} } }
  properties: {
    environmentId: environment_.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: registry.properties.loginServer, identity: apiIdentity.id }]
      secrets: concat(
        [vaultSecret('api-connection', vault.properties.vaultUri, apiIdentity.id)],
        hasModel ? [vaultSecret('model-key', vault.properties.vaultUri, apiIdentity.id)] : []
      )
    }
    template: {
      containers: [
        {
          name: 'api'
          image: images.api
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: probes
          env: concat(
            [
              { name: 'ConnectionStrings__api', secretRef: 'api-connection' }
              {
                name: 'ConnectionStrings__scheduler'
                value: schedulerConnection(scheduler.properties.endpoint, taskHub, apiIdentity.properties.clientId)
              }
              { name: 'Auth__Authority', value: authAuthority }
              { name: 'Auth__Audience', value: authAudience }
              { name: 'Auth__WebClient', value: authWebClient }
              { name: 'Auth__Scope', value: authScope }
              { name: 'Auth__TenantClaim', value: authTenantClaim }
              { name: 'Auth__IssuerPerTenant', value: authIssuerPerTenant }
              // The app sits behind the environment's proxy, which is where HTTPS ends.
              { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
            ],
            map(range(0, length(authAllowedTenants)), index => {
              name: 'Auth__AllowedTenants__${index}'
              value: string(authAllowedTenants[index])
            }),
            hasModel ? [{ name: 'ANTHROPIC_API_KEY', secretRef: 'model-key' }] : []
          )
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [{ name: 'requests', http: { metadata: { concurrentRequests: '40' } } }]
      }
    }
  }
  dependsOn: [apiReadsItsConnection, apiReadsTheModelKey, pullAccess, schedulerAccess]
}

resource worker 'Microsoft.App/containerApps@2025-07-01' = if (deployApps) {
  name: '${name}-worker'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: environment_.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: nothing outside the environment can call the worker.
      registries: [{ server: registry.properties.loginServer, identity: workerIdentity.id }]
      secrets: concat(
        [vaultSecret('worker-connection', vault.properties.vaultUri, workerIdentity.id)],
        hasModel ? [vaultSecret('model-key', vault.properties.vaultUri, workerIdentity.id)] : []
      )
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: images.worker
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: probes
          env: concat(
            [
              { name: 'ConnectionStrings__worker', secretRef: 'worker-connection' }
              {
                name: 'ConnectionStrings__scheduler'
                value: schedulerConnection(scheduler.properties.endpoint, taskHub, workerIdentity.properties.clientId)
              }
            ],
            hasModel ? [{ name: 'ANTHROPIC_API_KEY', secretRef: 'model-key' }] : []
          )
        }
      ]
      // One worker. It pulls its work from the scheduler; a second would be safe and is not needed.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [workerReadsItsConnection, workerReadsTheModelKey, pullAccess, schedulerAccess]
}

// Applies the schema and sets the two run-time roles' passwords, as the database's owner,
// then exits. Started by hand or by scripts/azure.sh, once per deployment.
resource migrate 'Microsoft.App/jobs@2025-07-01' = if (deployApps) {
  name: '${name}-migrate'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${migrateIdentity.id}': {} } }
  properties: {
    environmentId: environment_.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 300
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registry.properties.loginServer, identity: migrateIdentity.id }]
      secrets: [
        vaultSecret('owner-connection', vault.properties.vaultUri, migrateIdentity.id)
        vaultSecret('api-db-password', vault.properties.vaultUri, migrateIdentity.id)
        vaultSecret('worker-db-password', vault.properties.vaultUri, migrateIdentity.id)
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: images.migrate
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ConnectionStrings__overens', secretRef: 'owner-connection' }
            { name: 'API_DB_PASSWORD', secretRef: 'api-db-password' }
            { name: 'WORKER_DB_PASSWORD', secretRef: 'worker-db-password' }
          ]
        }
      ]
    }
  }
  dependsOn: [migrateReadsTheOwnerConnection, migrateReadsTheApiPassword, migrateReadsTheWorkerPassword, pullAccess]
}

@description('Where the images are pushed.')
output registry string = registry.properties.loginServer

@description('The address of the API and the console, once the apps are deployed.')
output address string = deployApps ? 'https://${api!.properties.configuration.ingress.fqdn}' : ''

@description('The job that applies the schema.')
output migrateJob string = deployApps ? migrate!.name : ''

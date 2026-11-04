// One Linux App Service with three containers (sidecars share localhost, like a compose network):
//   web    (main, port 8080): nginx serving Angular, proxying /api to the API
//   api    (port 5080): ASP.NET Core, Hangfire jobs
//   clamav (port 3310): virus scanner for uploads (optional)
// plus a "staging" slot with the same shape for blue-green releases.
param baseName string
param location string
param tags object
param planSku string
param apiImage string
param webImage string
param registryServer string
param registryUsername string
@secure()
param registryPassword string
param keyVaultName string
param keyVaultUri string
param appInsightsConnectionString string
param logAnalyticsWorkspaceId string
param seedDemoData bool
param whatsAppProvider string
param whatsAppPhoneNumberId string

@description('Run ClamAV as a sidecar and scan every upload with it (recommended; about 1 GB of memory).')
param enableClamAv bool = true

var kv = 'VaultName=${keyVaultName}'
var useCredentials = !empty(registryUsername)

// Shared by the slots. Secrets are Key Vault references resolved by App Service with the managed identity.
var commonSettings = {
  ASPNETCORE_HTTP_PORTS: '5080'
  API_UPSTREAM: 'http://localhost:5080'
  WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'false'
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
  ConnectionStrings__Default: '@Microsoft.KeyVault(${kv};SecretName=sql-connection)'
  Jwt__SigningKey: '@Microsoft.KeyVault(${kv};SecretName=jwt-signing-key)'
  Auth__SecureCookies: 'true'
  Database__MigrateOnStartup: 'true' // EF Core takes a lock, so two slots starting together are safe
  ReverseProxy__Enabled: 'true'
  // App Service's front end, then nginx. The container can only be reached through the front end.
  ReverseProxy__ForwardLimit: '2'
  ReverseProxy__KnownNetworks__0: '0.0.0.0/0'
  ReverseProxy__KnownNetworks__1: '::/0'
  Storage__Provider: 'AzureBlob'
  Storage__Container: 'documents'
  Storage__AzureConnectionString: '@Microsoft.KeyVault(${kv};SecretName=storage-connection)'
  Scanning__Provider: enableClamAv ? 'ClamAv' : 'Eicar'
  Scanning__ClamAvHost: 'localhost'
  Seed__DemoData: string(seedDemoData)
  Seed__Password: '@Microsoft.KeyVault(${kv};SecretName=seed-password)'
  WhatsApp__Provider: whatsAppProvider
  WhatsApp__AppSecret: '@Microsoft.KeyVault(${kv};SecretName=whatsapp-app-secret)'
  WhatsApp__VerifyToken: '@Microsoft.KeyVault(${kv};SecretName=whatsapp-verify-token)'
  WhatsApp__AccessToken: '@Microsoft.KeyVault(${kv};SecretName=whatsapp-access-token)'
  WhatsApp__PhoneNumberId: whatsAppPhoneNumberId
  // Feature flags (ADR-0017): switch a risky feature off here, without a deploy.
  Features__WhatsAppSending: 'true'
  Features__Reminders: 'true'
}

// Only production runs the background jobs; the staging slot must never send reminders.
var productionSettings = union(commonSettings, { Jobs__Enabled: 'true' })
var stagingSettings = union(commonSettings, { Jobs__Enabled: 'false', Seed__DemoData: 'false' })

// Every app setting is passed into the API container; the web container needs only its upstream.
var apiEnvironment = [for name in objectKeys(productionSettings): { name: name, value: name }]

var siteConfig = {
  linuxFxVersion: 'SITECONTAINERS'
  alwaysOn: true
  http20Enabled: true
  minTlsVersion: '1.2'
  scmMinTlsVersion: '1.2'
  ftpsState: 'Disabled'
  healthCheckPath: '/health/ready'
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${baseName}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: planSku }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: '${baseName}-app-${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

resource siteSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'appsettings'
  properties: productionSettings
}

// Settings that stay with their slot when slots are swapped.
resource sticky 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'slotConfigNames'
  properties: {
    appSettingNames: ['Jobs__Enabled', 'Seed__DemoData']
  }
}

resource staging 'Microsoft.Web/sites/slots@2024-04-01' = {
  parent: site
  name: 'staging'
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

resource stagingSettingsConfig 'Microsoft.Web/sites/slots/config@2024-04-01' = {
  parent: staging
  name: 'appsettings'
  properties: stagingSettings
}

var registryAuth = useCredentials
  ? { authType: 'UserCredentials', userName: registryUsername, passwordSecret: registryPassword }
  : { authType: 'Anonymous' }

resource webContainer 'Microsoft.Web/sites/sitecontainers@2024-04-01' = {
  parent: site
  name: 'web'
  properties: union(registryAuth, {
    image: webImage
    isMain: true
    targetPort: '8080'
    environmentVariables: [{ name: 'API_UPSTREAM', value: 'API_UPSTREAM' }]
  })
}

resource apiContainer 'Microsoft.Web/sites/sitecontainers@2024-04-01' = {
  parent: site
  name: 'api'
  properties: union(registryAuth, {
    image: apiImage
    isMain: false
    targetPort: '5080'
    environmentVariables: apiEnvironment
  })
}

resource clamContainer 'Microsoft.Web/sites/sitecontainers@2024-04-01' = if (enableClamAv) {
  parent: site
  name: 'clamav'
  properties: {
    image: 'clamav/clamav:stable'
    isMain: false
    targetPort: '3310'
    authType: 'Anonymous'
  }
}

// The staging slot starts with the same images; deploy.yml points it at each new release.
resource stagingWeb 'Microsoft.Web/sites/slots/sitecontainers@2024-04-01' = {
  parent: staging
  name: 'web'
  properties: webContainer.properties
}

resource stagingApi 'Microsoft.Web/sites/slots/sitecontainers@2024-04-01' = {
  parent: staging
  name: 'api'
  properties: apiContainer.properties
}

resource stagingClam 'Microsoft.Web/sites/slots/sitecontainers@2024-04-01' = if (enableClamAv) {
  parent: staging
  name: 'clamav'
  properties: {
    image: 'clamav/clamav:stable'
    isMain: false
    targetPort: '3310'
    authType: 'Anonymous'
  }
}

// Both slots' identities may read secrets, and nothing else.
var keyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource siteCanReadSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, site.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource stagingCanReadSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, staging.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: staging.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Container stdout (Serilog's JSON), HTTP and platform logs go to Log Analytics.
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: site
  name: 'to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output siteName string = site.name
output hostName string = site.properties.defaultHostName
output stagingHostName string = staging.properties.defaultHostName
output keyVaultUri string = keyVaultUri

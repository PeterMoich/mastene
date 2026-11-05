// Tenant Manager on Azure (ADR-0017). Everything the app needs, nothing set up by hand:
//   az deployment group create -g <resource-group> -f infra/main.bicep -p infra/main.parameters.json \
//     -p sqlAdminPassword=<secret> jwtSigningKey=<secret>
// One App Service (Linux) runs the web container (nginx + Angular) with the API as a sidecar, so the
// browser talks to one origin, exactly as in docker compose. With costProfile=production a "staging" slot
// takes new releases first and swapping it in is the blue-green release step; with costProfile=demo the
// release replaces the running containers (see .github/workflows/deploy.yml and ADR-0022).
targetScope = 'resourceGroup'

@description('Short name used in every resource name, e.g. "mastene". Lower case letters and digits.')
@minLength(3)
@maxLength(12)
param appName string

@description('dev, test or prod. Part of the resource names.')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'prod'

param location string = resourceGroup().location

@description('Container images, e.g. ghcr.io/petermoich/mastene-api:<git sha>.')
param apiImage string
param webImage string

@description('Registry credentials, only if the GHCR packages are private.')
param registryServer string = 'ghcr.io'
param registryUsername string = ''
@secure()
param registryPassword string = ''

@description('Azure SQL administrator login.')
param sqlAdminLogin string = 'tmadmin'
@secure()
param sqlAdminPassword string

@description('JWT signing key: at least 32 random characters.')
@secure()
param jwtSigningKey string

@description('Who gets the SLO and availability alerts.')
param alertEmail string = ''

@description('''How much to spend (ADR-0022).
demo: Basic B1 plan, Basic database, no staging slot or ClamAV sidecar: a portfolio or trial site, roughly
R350 a month. Releases replace the running containers (a minute or two of restart, rolled back if unhealthy).
production: Premium P0v3 plan with a staging slot for blue-green releases, S0 database and the ClamAV sidecar.''')
@allowed(['demo', 'production'])
param costProfile string = 'demo'

@description('Optional override of the App Service plan size. Slots need Standard (S1) or Premium (P0v3 and up).')
param planSku string = ''

@description('Monthly budget for this resource group, in the billing currency. Emails at 50%, 80% and 100% (actual) and 100% (forecast). 0 turns it off.')
param monthlyBudget int = 500

@description('First day of the budget, yyyy-MM-01. Defaults to this month.')
param budgetStartDate string = '${utcNow('yyyy-MM')}-01'

@description('Seed the synthetic demo data (never for a real landlord).')
param seedDemoData bool = false

@secure()
param seedPassword string = ''

@description('List the demo logins (landlord and tenants, not the admin) on the sign-in page. Demo sites only: anyone can then sign in and change the demo data.')
param showDemoLogins bool = false

@description('WhatsApp: "Fake" until a Meta business account is set up.')
@allowed(['Fake', 'Meta'])
param whatsAppProvider string = 'Fake'
@secure()
param whatsAppAppSecret string = ''
@secure()
param whatsAppVerifyToken string = ''
@secure()
param whatsAppAccessToken string = ''
param whatsAppPhoneNumberId string = ''

var isProduction = costProfile == 'production'
var profile = {
  planSku: empty(planSku) ? (isProduction ? 'P0v3' : 'B1') : planSku
  useSlots: isProduction
  enableClamAv: isProduction
  databaseSku: isProduction ? { name: 'S0', tier: 'Standard' } : { name: 'Basic', tier: 'Basic' }
  // Standard availability tests are billed per run: every 5 minutes from 3 places, or every 15 from 2.
  availabilityFrequency: isProduction ? 300 : 900
  availabilityLocations: isProduction ? 3 : 2
}

var suffix = uniqueString(resourceGroup().id)
var baseName = '${appName}-${environmentName}'
var storageName = take(toLower('${appName}${environmentName}${suffix}'), 24)
var sqlServerName = '${baseName}-sql-${suffix}'
var databaseName = 'TenantManager'
var tags = {
  app: 'tenant-manager'
  environment: environmentName
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    baseName: baseName
    location: location
    tags: tags
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  params: {
    baseName: baseName
    storageName: storageName
    sqlServerName: sqlServerName
    databaseName: databaseName
    location: location
    tags: tags
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    databaseSku: profile.databaseSku
  }
}

// Built here, not output by the data module, so no secret ever appears in deployment outputs.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
  dependsOn: [data]
}
var sqlConnection = 'Server=tcp:${data.outputs.sqlFqdn},1433;Database=${databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var storageConnection = 'DefaultEndpointsProtocol=https;AccountName=${storageName};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

module vault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: take('${appName}${environmentName}kv${suffix}', 24)
    location: location
    tags: tags
    secrets: {
      'sql-connection': sqlConnection
      'storage-connection': storageConnection
      'jwt-signing-key': jwtSigningKey
      'seed-password': empty(seedPassword) ? 'not-used' : seedPassword
      'whatsapp-app-secret': empty(whatsAppAppSecret) ? 'not-used' : whatsAppAppSecret
      'whatsapp-verify-token': empty(whatsAppVerifyToken) ? 'not-used' : whatsAppVerifyToken
      'whatsapp-access-token': empty(whatsAppAccessToken) ? 'not-used' : whatsAppAccessToken
    }
  }
}

module app 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    baseName: baseName
    location: location
    tags: tags
    planSku: profile.planSku
    useSlots: profile.useSlots
    enableClamAv: profile.enableClamAv
    apiImage: apiImage
    webImage: webImage
    registryServer: registryServer
    registryUsername: registryUsername
    registryPassword: registryPassword
    keyVaultName: vault.outputs.name
    keyVaultUri: vault.outputs.uri
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    seedDemoData: seedDemoData
    showDemoLogins: showDemoLogins && seedDemoData
    whatsAppProvider: whatsAppProvider
    whatsAppPhoneNumberId: whatsAppPhoneNumberId
  }
}

module alerts 'modules/alerts.bicep' = {
  name: 'alerts'
  params: {
    baseName: baseName
    location: location
    tags: tags
    appInsightsId: monitoring.outputs.appInsightsId
    appUrl: 'https://${app.outputs.hostName}'
    alertEmail: alertEmail
    availabilityFrequency: profile.availabilityFrequency
    availabilityLocations: profile.availabilityLocations
  }
}

// Cost alerts by email; nothing is switched off automatically (an Azure free account is disabled, not
// billed, when its credit runs out).
resource budget 'Microsoft.Consumption/budgets@2023-05-01' = if (monthlyBudget > 0 && !empty(alertEmail)) {
  name: '${baseName}-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      actual50: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 50, thresholdType: 'Actual', contactEmails: [alertEmail] }
      actual80: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 80, thresholdType: 'Actual', contactEmails: [alertEmail] }
      actual100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Actual', contactEmails: [alertEmail] }
      forecast100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Forecasted', contactEmails: [alertEmail] }
    }
  }
}

output appUrl string = 'https://${app.outputs.hostName}'
output stagingUrl string = profile.useSlots ? 'https://${app.outputs.stagingHostName}' : ''
output costProfile string = costProfile
output siteName string = app.outputs.siteName
output keyVaultName string = vault.outputs.name

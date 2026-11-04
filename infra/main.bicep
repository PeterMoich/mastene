// Tenant Manager on Azure (ADR-0017). Everything the app needs, nothing set up by hand:
//   az deployment group create -g <resource-group> -f infra/main.bicep -p infra/main.parameters.json \
//     -p sqlAdminPassword=<secret> jwtSigningKey=<secret>
// One App Service (Linux) runs the web container (nginx + Angular) with the API as a sidecar, so the
// browser talks to one origin, exactly as in docker compose. A "staging" slot takes new releases first;
// swapping it into production is the blue-green release step (see .github/workflows/deploy.yml).
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

@description('App Service plan size. Deployment slots need Standard (S1) or Premium (P0v3 and up).')
param planSku string = 'P0v3'

@description('Seed the synthetic demo data (never for a real landlord).')
param seedDemoData bool = false

@secure()
param seedPassword string = ''

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
    planSku: planSku
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
  }
}

output appUrl string = 'https://${app.outputs.hostName}'
output stagingUrl string = 'https://${app.outputs.stagingHostName}'
output siteName string = app.outputs.siteName
output keyVaultName string = vault.outputs.name

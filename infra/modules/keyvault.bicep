// Key Vault for every secret (RBAC, purge protection). The app reads them through Key Vault
// references in its settings, using its managed identity; no secret is stored in app settings or git.
param name string
param location string
param tags object

@secure()
param secrets object

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource secret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for item in items(secrets): {
  parent: vault
  name: item.key
  properties: {
    value: item.value
  }
}]

output name string = vault.name
output uri string = vault.properties.vaultUri

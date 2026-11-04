// Log Analytics + workspace-based Application Insights (ADR-0018): traces, metrics and the container logs.
param baseName string
param location string
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: false
  }
}

// The SLO dashboard: request rate, latency percentiles, error rate, the 30-day latency SLO and app metrics.
resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, baseName, 'slo-workbook')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Tenant Manager - service levels'
    category: 'workbook'
    sourceId: appInsights.id
    serializedData: string(loadJsonContent('slo-workbook.json'))
  }
}

output workspaceId string = workspace.id
output appInsightsId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString

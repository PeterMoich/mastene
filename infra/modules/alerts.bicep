// SLO alerts (ADR-0018). SLOs over 30 days: 99% of API requests under 500 ms, 99.5% succeed.
// The alerts fire on a fast burn of that budget, so someone hears about it within the hour.
param baseName string
param location string
param tags object
param appInsightsId string
param appUrl string
param alertEmail string

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: '${baseName}-oncall'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'tm-oncall'
    enabled: true
    emailReceivers: [
      { name: 'owner', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

var actions = empty(alertEmail) ? [] : [actionGroup.id]

// Availability: the readiness endpoint (API + database) checked from three regions every 5 minutes.
resource availability 'Microsoft.Insights/webtests@2022-06-15' = {
  name: '${baseName}-ready'
  location: location
  tags: union(tags, { 'hidden-link:${appInsightsId}': 'Resource' })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: '${baseName}-ready'
    Name: 'Readiness (/health/ready)'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      { Id: 'emea-za-jnb-azr' } // South Africa North
      { Id: 'emea-nl-ams-azr' }
      { Id: 'us-va-ash-azr' }
    ]
    Request: {
      RequestUrl: '${appUrl}/health/ready'
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 14
    }
  }
}

resource availabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${baseName}-unavailable'
  location: 'global'
  tags: tags
  properties: {
    description: 'The app failed its readiness check from 2 or more locations.'
    severity: 1
    enabled: true
    scopes: [availability.id, appInsightsId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: availability.id
      componentId: appInsightsId
      failedLocationCount: 2
    }
    actions: [for id in actions: { actionGroupId: id }]
  }
}

// Fast burn of the latency SLO: more than 5% of requests slower than 500 ms over an hour
// (the 30-day budget is 1%).
resource latencyAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${baseName}-latency-slo'
  location: location
  tags: tags
  properties: {
    displayName: 'Latency SLO burning: >5% of requests over 500 ms in the last hour'
    severity: 2
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    criteria: {
      allOf: [
        {
          query: 'requests | where name !startswith "GET /health" | summarize total = count(), slow = countif(duration >= 500) | where total >= 50 | extend slowPct = 100.0 * slow / total | where slowPct > 5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: actions }
  }
}

// Fast burn of the availability SLO: more than 2% server errors over 15 minutes (the 30-day budget is 0.5%).
resource errorAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${baseName}-error-slo'
  location: location
  tags: tags
  properties: {
    displayName: 'Availability SLO burning: >2% server errors in 15 minutes'
    severity: 1
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'requests | where name !startswith "GET /health" | summarize total = count(), errors = countif(toint(resultCode) >= 500) | where total >= 20 | extend errorPct = 100.0 * errors / total | where errorPct > 2'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: actions }
  }
}

// WhatsApp messages failing to send, or forged webhook calls, are worth a look.
resource messagingAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${baseName}-messaging'
  location: location
  tags: tags
  properties: {
    displayName: 'WhatsApp: failed sends or rejected webhooks in the last hour'
    severity: 3
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name in ("tm.messages.failed", "tm.webhooks.rejected") | summarize n = sum(valueSum) | where n >= 5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: actions }
  }
}

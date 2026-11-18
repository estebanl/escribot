@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = {}

@description('Optional custom domain to bind a free managed certificate to.')
param customDomain string = ''

@description('Set to true after DNS records (CNAME and asuid TXT) point to the Container App.')
param enableCustomDomainCert bool = false

resource aca_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('acalaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    retentionInDays: 30
    // About 4.5 GB a month at most, inside the 5 GB free allowance even if a flood of
    // requests floods the logs too. Ingestion stops for the rest of the UTC day at the cap.
    workspaceCapping: {
      dailyQuotaGb: json('0.15')
    }
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource aca 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: take('aca${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: aca_law.properties.customerId
        sharedKey: aca_law.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: tags
}

// Aspire Dashboard on Container Apps requires Microsoft Entra ID authentication (Azure RBAC)
// and is not reachable anonymously.
resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: aca
}

resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2025-07-01' = if (enableCustomDomainCert && !empty(customDomain)) {
  name: take('cert-${replace(customDomain, '.', '-')}', 64)
  location: location
  parent: aca
  properties: {
    subjectName: customDomain
    domainControlValidation: 'CNAME'
  }
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = aca_law.name
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = aca_law.id
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = aca.name
output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = aca.id
output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = aca.properties.defaultDomain
output customDomainVerificationId string = aca.properties.customDomainConfiguration.customDomainVerificationId
output managedCertificateId string = (enableCustomDomainCert && !empty(customDomain)) ? managedCertificate.id : ''

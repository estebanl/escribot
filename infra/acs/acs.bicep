@description('Data location for Azure Communication Services.')
param dataLocation string = 'United States'

@description('Custom domain for transactional email (SPF and DKIM records are shown in the Azure Portal for this domain).')
param customDomain string

@description('Name of the Key Vault where the ACS connection string is stored.')
param vaultName string

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: take('email-${uniqueString(resourceGroup().id)}', 63)
  location: 'global'
  properties: {
    dataLocation: dataLocation
  }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  name: customDomain
  location: 'global'
  parent: emailService
  properties: {
    domainManagement: 'CustomerManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource senderUsername 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = {
  name: 'DoNotReply'
  parent: emailDomain
  properties: {
    username: 'DoNotReply'
    displayName: 'FluentEscritor'
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: take('acs-${uniqueString(resourceGroup().id)}', 63)
  location: 'global'
  properties: {
    dataLocation: dataLocation
    linkedDomains: [
      emailDomain.id
    ]
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vaultName
}

// Store the connection string directly in Key Vault so it never appears in deployment outputs or logs.
resource acsConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'acs-connection-string'
  parent: vault
  properties: {
    value: communicationService.listKeys().primaryConnectionString
  }
}

output communicationServiceName string = communicationService.name
output emailDomainName string = emailDomain.name
output defaultSenderAddress string = 'DoNotReply@${customDomain}'

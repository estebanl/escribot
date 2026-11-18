@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@secure()
param encryption_secret_value string

@secure()
param stripe_secret_key_value string = ''

@secure()
param stripe_webhook_secret_value string = ''

@secure()
param google_client_secret_value string = ''

@secure()
param openrouter_api_key_value string = ''

@secure()
param deepseek_api_key_value string = ''

@secure()
param gemini_api_key_value string = ''

@secure()
param openai_api_key_value string = ''

@secure()
param anthropic_api_key_value string = ''

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('vault-${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
  tags: {
    'aspire-resource-name': 'vault'
  }
}

resource secret_encryption_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'encryption-secret'
  properties: {
    value: encryption_secret_value
  }
  parent: vault
}

resource secret_stripe_secret_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(stripe_secret_key_value)) {
  name: 'stripe-secret-key'
  properties: {
    value: stripe_secret_key_value
  }
  parent: vault
}

resource secret_stripe_webhook_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(stripe_webhook_secret_value)) {
  name: 'stripe-webhook-secret'
  properties: {
    value: stripe_webhook_secret_value
  }
  parent: vault
}

resource secret_google_client_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(google_client_secret_value)) {
  name: 'google-client-secret'
  properties: {
    value: google_client_secret_value
  }
  parent: vault
}

resource secret_openrouter_api_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(openrouter_api_key_value)) {
  name: 'openrouter-api-key'
  properties: {
    value: openrouter_api_key_value
  }
  parent: vault
}

resource secret_deepseek_api_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(deepseek_api_key_value)) {
  name: 'deepseek-api-key'
  properties: {
    value: deepseek_api_key_value
  }
  parent: vault
}

resource secret_gemini_api_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(gemini_api_key_value)) {
  name: 'gemini-api-key'
  properties: {
    value: gemini_api_key_value
  }
  parent: vault
}

resource secret_openai_api_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(openai_api_key_value)) {
  name: 'openai-api-key'
  properties: {
    value: openai_api_key_value
  }
  parent: vault
}

resource secret_anthropic_api_key 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(anthropic_api_key_value)) {
  name: 'anthropic-api-key'
  properties: {
    value: anthropic_api_key_value
  }
  parent: vault
}

output vaultUri string = vault.properties.vaultUri
output name string = vault.name
output id string = vault.id

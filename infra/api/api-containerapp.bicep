@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aca_outputs_azure_container_apps_environment_default_domain string
param aca_outputs_azure_container_apps_environment_id string
param api_containerimage string
param api_identity_outputs_id string
param api_identity_outputs_clientid string
param cosmos_outputs_connectionstring string
param storage_outputs_blobendpoint string
param vault_outputs_vaultUri string

param customDomain string = ''
param enableCustomDomainCert bool = false
param managedCertificateId string = ''
param publicUrl string = ''

param hasStripeSecretKey bool = false
param hasStripeWebhookSecret bool = false
param hasGoogleClientSecret bool = false
param hasOpenRouterApiKey bool = false
param hasDeepSeekApiKey bool = false
param hasGeminiApiKey bool = false
param hasOpenAiApiKey bool = false
param hasAnthropicApiKey bool = false
param hasAcsConnectionString bool = false

param googleClientId string = ''
param stripePriceProMonthly string = ''
param stripePriceProYearly string = ''
param ownerEmail string = ''
param allowedSignupEmails string = ''
param adminEmails string = ''
param emailFrom string = ''
param freePlanDailyAiCap string = ''
param siteOperator string = ''
param supportEmail string = ''
param siteJurisdiction string = ''

var effectivePublicUrl = !empty(publicUrl)
  ? publicUrl
  : (!empty(customDomain)
      ? 'https://${customDomain}'
      : 'https://api.${aca_outputs_azure_container_apps_environment_default_domain}')

var containerAppSecrets = concat(
  [
    {
      name: 'app--encryptionsecret'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/encryption-secret'
    }
  ],
  hasStripeSecretKey ? [
    {
      name: 'stripe-secret-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/stripe-secret-key'
    }
  ] : [],
  hasStripeWebhookSecret ? [
    {
      name: 'stripe-webhook-secret'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/stripe-webhook-secret'
    }
  ] : [],
  hasGoogleClientSecret ? [
    {
      name: 'google-client-secret'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/google-client-secret'
    }
  ] : [],
  hasOpenRouterApiKey ? [
    {
      name: 'openrouter-api-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/openrouter-api-key'
    }
  ] : [],
  hasDeepSeekApiKey ? [
    {
      name: 'deepseek-api-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/deepseek-api-key'
    }
  ] : [],
  hasGeminiApiKey ? [
    {
      name: 'gemini-api-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/gemini-api-key'
    }
  ] : [],
  hasOpenAiApiKey ? [
    {
      name: 'openai-api-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/openai-api-key'
    }
  ] : [],
  hasAnthropicApiKey ? [
    {
      name: 'anthropic-api-key'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/anthropic-api-key'
    }
  ] : [],
  hasAcsConnectionString ? [
    {
      name: 'acs-connection-string'
      identity: api_identity_outputs_id
      keyVaultUrl: '${vault_outputs_vaultUri}secrets/acs-connection-string'
    }
  ] : []
)

var containerEnv = concat(
  [
    {
      name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
      value: 'in_memory'
    }
    {
      name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
      value: 'true'
    }
    {
      name: 'App__TrustForwardedHeaders'
      value: 'true'
    }
    {
      name: 'App__PublicUrl'
      value: effectivePublicUrl
    }
    {
      name: 'HTTP_PORTS'
      value: '8080'
    }
    {
      name: 'ConnectionStrings__cosmos'
      value: cosmos_outputs_connectionstring
    }
    {
      name: 'ConnectionStrings__blobs'
      value: storage_outputs_blobendpoint
    }
    {
      name: 'App__EncryptionSecret'
      secretRef: 'app--encryptionsecret'
    }
    {
      name: 'AZURE_CLIENT_ID'
      value: api_identity_outputs_clientid
    }
    {
      name: 'AZURE_TOKEN_CREDENTIALS'
      value: 'ManagedIdentityCredential'
    }
  ],
  hasStripeSecretKey ? [{ name: 'STRIPE_SECRET_KEY', secretRef: 'stripe-secret-key' }] : [],
  hasStripeWebhookSecret ? [{ name: 'STRIPE_WEBHOOK_SECRET', secretRef: 'stripe-webhook-secret' }] : [],
  hasGoogleClientSecret ? [{ name: 'GOOGLE_CLIENT_SECRET', secretRef: 'google-client-secret' }] : [],
  hasOpenRouterApiKey ? [{ name: 'OPENROUTER_API_KEY', secretRef: 'openrouter-api-key' }] : [],
  hasDeepSeekApiKey ? [{ name: 'DEEPSEEK_API_KEY', secretRef: 'deepseek-api-key' }] : [],
  hasGeminiApiKey ? [{ name: 'GEMINI_API_KEY', secretRef: 'gemini-api-key' }] : [],
  hasOpenAiApiKey ? [{ name: 'OPENAI_API_KEY', secretRef: 'openai-api-key' }] : [],
  hasAnthropicApiKey ? [{ name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }] : [],
  hasAcsConnectionString ? [{ name: 'AZURE_COMMUNICATION_CONNECTION_STRING', secretRef: 'acs-connection-string' }] : [],
  !empty(googleClientId) ? [{ name: 'GOOGLE_CLIENT_ID', value: googleClientId }] : [],
  !empty(stripePriceProMonthly) ? [{ name: 'STRIPE_PRICE_PRO_MONTHLY', value: stripePriceProMonthly }] : [],
  !empty(stripePriceProYearly) ? [{ name: 'STRIPE_PRICE_PRO_YEARLY', value: stripePriceProYearly }] : [],
  !empty(ownerEmail) ? [{ name: 'OWNER_EMAIL', value: ownerEmail }] : [],
  !empty(allowedSignupEmails) ? [{ name: 'ALLOWED_SIGNUP_EMAILS', value: allowedSignupEmails }] : [],
  !empty(adminEmails) ? [{ name: 'ADMIN_EMAILS', value: adminEmails }] : [],
  !empty(emailFrom) ? [{ name: 'EMAIL_FROM', value: emailFrom }] : [],
  !empty(freePlanDailyAiCap) ? [{ name: 'FREE_PLAN_DAILY_AI_CAP', value: freePlanDailyAiCap }] : [],
  !empty(siteOperator) ? [{ name: 'SITE_OPERATOR', value: siteOperator }] : [],
  !empty(supportEmail) ? [{ name: 'SUPPORT_EMAIL', value: supportEmail }] : [],
  !empty(siteJurisdiction) ? [{ name: 'SITE_JURISDICTION', value: siteJurisdiction }] : []
)

resource api 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: containerAppSecrets
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        customDomains: (enableCustomDomainCert && !empty(customDomain) && !empty(managedCertificateId))
          ? [
              {
                name: customDomain
                bindingType: 'SniEnabled'
                certificateId: managedCertificateId
              }
            ]
          : []
      }
      // Public GHCR image: no pull credentials or Azure Container Registry needed.
      registries: []
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: aca_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: api_containerimage
          name: 'api'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: containerEnv
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 2
              periodSeconds: 5
              failureThreshold: 12
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
        cooldownPeriod: 300
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${api_identity_outputs_id}': {}
    }
  }
}

output fqdn string = api.properties.configuration.ingress.fqdn
output name string = api.name

targetScope = 'subscription'

param resourceGroupName string
param location string
param apiContainerImage string

param customDomain string = ''
param enableCustomDomainCert bool = false
@description('Domain Azure Communication Services sends email from. Defaults to customDomain; use a subdomain such as mail.<customDomain> when customDomain is a CNAME.')
param emailDomain string = ''
param publicUrl string = ''

@description('Set to true once customDomain is proxied through Cloudflare. The Container App then accepts requests only from Cloudflare\'s ranges in cloudflare-ips.json, and the API reads the visitor\'s address from CF-Connecting-IP.')
param behindCloudflare bool = false

@secure()
@description('Base64 PFX of the Cloudflare Origin certificate for customDomain.')
param origin_certificate_pfx string = ''

@secure()
param origin_certificate_password string = ''

// Cloudflare's published edge ranges (https://www.cloudflare.com/ips/), kept in one file.
var cloudflareIpRanges = loadJsonContent('cloudflare-ips.json')
param acsDataLocation string = 'United States'

var effectiveEmailDomain = !empty(emailDomain) ? emailDomain : customDomain

param budgetAlertEmail string = ''
@description('Monthly budget in the billing currency. Kept out of the repository: the deploy workflow passes it from the BUDGET_AMOUNT secret.')
@minValue(1)
param budgetAmount int
param budgetStartDate string = '2026-11-01'
@description('Stop the Container App when actual spend in the resource group reaches the budget.')
param enableBudgetKillSwitch bool = true

@secure()
param encryption_secret string

@secure()
param stripe_secret_key string = ''

@secure()
param stripe_webhook_secret string = ''

@secure()
param google_client_secret string = ''

@secure()
param openrouter_api_key string = ''

@secure()
param deepseek_api_key string = ''

@secure()
param gemini_api_key string = ''

@secure()
param openai_api_key string = ''

@secure()
param anthropic_api_key string = ''

param googleClientId string = ''
param stripePriceProMonthly string = ''
param stripePriceProYearly string = ''
param ownerEmail string = ''
param allowedSignupEmails string = ''
param adminEmails string = ''
param emailFrom string = ''
param freePlanDailyAiCap string = ''
param freePlanEvaluationsPerMonth string = ''
param freePlanAssistsPerMonth string = ''
param proPlanEvaluationsPerMonth string = ''
param proPlanAssistsPerMonth string = ''
param siteOperator string = ''
param supportEmail string = ''
param siteJurisdiction string = ''

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module cosmos 'cosmos/cosmos.bicep' = {
  name: 'cosmos'
  scope: rg
  params: {
    location: location
  }
}

module storage 'storage/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
  }
}

module vault 'vault/vault.bicep' = {
  name: 'vault'
  scope: rg
  params: {
    location: location
    encryption_secret_value: encryption_secret
    stripe_secret_key_value: stripe_secret_key
    stripe_webhook_secret_value: stripe_webhook_secret
    google_client_secret_value: google_client_secret
    openrouter_api_key_value: openrouter_api_key
    deepseek_api_key_value: deepseek_api_key
    gemini_api_key_value: gemini_api_key
    openai_api_key_value: openai_api_key
    anthropic_api_key_value: anthropic_api_key
  }
}

module acs 'acs/acs.bicep' = if (!empty(effectiveEmailDomain)) {
  name: 'acs'
  scope: rg
  params: {
    dataLocation: acsDataLocation
    customDomain: effectiveEmailDomain
    vaultName: vault.outputs.name
  }
}

module aca 'aca/aca.bicep' = {
  name: 'aca'
  scope: rg
  params: {
    location: location
    customDomain: customDomain
    enableCustomDomainCert: enableCustomDomainCert
    originCertificatePfx: origin_certificate_pfx
    originCertificatePassword: origin_certificate_password
  }
}

module api_identity 'api-identity/api-identity.bicep' = {
  name: 'api-identity'
  scope: rg
  params: {
    location: location
  }
}

module api_roles_vault 'api-roles-vault/api-roles-vault.bicep' = {
  name: 'api-roles-vault'
  scope: rg
  params: {
    vault_outputs_name: vault.outputs.name
    principalId: api_identity.outputs.principalId
  }
}

module api_roles_cosmos 'api-roles-cosmos/api-roles-cosmos.bicep' = {
  name: 'api-roles-cosmos'
  scope: rg
  params: {
    cosmos_outputs_name: cosmos.outputs.name
    principalId: api_identity.outputs.principalId
  }
}

module api_roles_storage 'api-roles-storage/api-roles-storage.bicep' = {
  name: 'api-roles-storage'
  scope: rg
  params: {
    storage_outputs_name: storage.outputs.name
    principalId: api_identity.outputs.principalId
  }
}

module api 'api/api-containerapp.bicep' = {
  name: 'api'
  scope: rg
  dependsOn: [
    api_roles_vault
    api_roles_cosmos
    api_roles_storage
    acs
  ]
  params: {
    location: location
    aca_outputs_azure_container_apps_environment_default_domain: aca.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
    aca_outputs_azure_container_apps_environment_id: aca.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID
    api_containerimage: apiContainerImage
    api_identity_outputs_id: api_identity.outputs.id
    api_identity_outputs_clientid: api_identity.outputs.clientId
    cosmos_outputs_connectionstring: cosmos.outputs.connectionString
    storage_outputs_blobendpoint: storage.outputs.blobEndpoint
    vault_outputs_vaultUri: vault.outputs.vaultUri
    customDomain: customDomain
    customDomainCertificateId: aca.outputs.customDomainCertificateId
    // The Container Apps ingress is IPv4 only, so only the IPv4 ranges become ingress rules.
    allowedIngressIpRanges: behindCloudflare ? filter(cloudflareIpRanges, range => !contains(range, ':')) : []
    cloudflareIpRanges: behindCloudflare ? join(cloudflareIpRanges, ',') : ''
    publicUrl: publicUrl
    hasStripeSecretKey: !empty(stripe_secret_key)
    hasStripeWebhookSecret: !empty(stripe_webhook_secret)
    hasGoogleClientSecret: !empty(google_client_secret)
    hasOpenRouterApiKey: !empty(openrouter_api_key)
    hasDeepSeekApiKey: !empty(deepseek_api_key)
    hasGeminiApiKey: !empty(gemini_api_key)
    hasOpenAiApiKey: !empty(openai_api_key)
    hasAnthropicApiKey: !empty(anthropic_api_key)
    hasAcsConnectionString: !empty(effectiveEmailDomain)
    googleClientId: googleClientId
    stripePriceProMonthly: stripePriceProMonthly
    stripePriceProYearly: stripePriceProYearly
    ownerEmail: ownerEmail
    allowedSignupEmails: allowedSignupEmails
    adminEmails: adminEmails
    emailFrom: !empty(emailFrom) ? emailFrom : (!empty(effectiveEmailDomain) ? 'DoNotReply@${effectiveEmailDomain}' : '')
    freePlanDailyAiCap: freePlanDailyAiCap
    freePlanEvaluationsPerMonth: freePlanEvaluationsPerMonth
    freePlanAssistsPerMonth: freePlanAssistsPerMonth
    proPlanEvaluationsPerMonth: proPlanEvaluationsPerMonth
    proPlanAssistsPerMonth: proPlanAssistsPerMonth
    siteOperator: siteOperator
    supportEmail: supportEmail
    siteJurisdiction: siteJurisdiction
  }
}

module budget_killswitch 'budget-killswitch/budget-killswitch.bicep' = if (enableBudgetKillSwitch) {
  name: 'budget-killswitch'
  scope: rg
  params: {
    location: location
    containerAppName: api.outputs.name
  }
}

var budgetEmails = empty(budgetAlertEmail) ? [] : [budgetAlertEmail]
var budgetKillSwitchGroups = enableBudgetKillSwitch ? [budget_killswitch!.outputs.actionGroupId] : []

var budgetEmailNotifications = empty(budgetAlertEmail) ? {} : {
  Actual_50_Percent: {
    enabled: true
    operator: 'GreaterThanOrEqualTo'
    threshold: 50
    thresholdType: 'Actual'
    contactEmails: budgetEmails
  }
  Forecasted_100_Percent: {
    enabled: true
    operator: 'GreaterThanOrEqualTo'
    threshold: 100
    thresholdType: 'Forecasted'
    contactEmails: budgetEmails
  }
}

// Counts only this app's resource group. At 100% actual spend it emails the owner and,
// with the kill switch on, stops the Container App.
resource monthlyBudget 'Microsoft.Consumption/budgets@2023-05-01' = if (!empty(budgetAlertEmail) || enableBudgetKillSwitch) {
  name: 'escribot-monthly-budget'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    filter: {
      dimensions: {
        name: 'ResourceGroupName'
        operator: 'In'
        values: [
          resourceGroupName
        ]
      }
    }
    notifications: union(budgetEmailNotifications, {
      Actual_100_Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: budgetEmails
        contactGroups: budgetKillSwitchGroups
      }
    })
  }
}

output apiFqdn string = api.outputs.fqdn
output customDomainVerificationId string = aca.outputs.customDomainVerificationId
output cosmosEndpoint string = cosmos.outputs.connectionString
output storageBlobEndpoint string = storage.outputs.blobEndpoint
output vaultName string = vault.outputs.name

// Budget kill switch: the monthly budget's 100% notification calls this action group,
// which runs a Logic App that stops the Container App through the ARM API.
// Stopping is reversible: `az containerapp start -g <rg> -n api` brings the app back.

param location string
param containerAppName string

resource api 'Microsoft.App/containerApps@2025-10-02-preview' existing = {
  name: containerAppName
}

resource stopApi 'Microsoft.Logic/workflows@2019-05-01' = {
  name: 'budget-killswitch'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      triggers: {
        manual: {
          type: 'Request'
          kind: 'Http'
          inputs: {
            schema: {}
          }
        }
      }
      actions: {
        Stop_container_app: {
          type: 'Http'
          runAfter: {}
          inputs: {
            method: 'POST'
            uri: '${environment().resourceManager}${substring(api.id, 1)}/stop?api-version=2024-03-01'
            authentication: {
              type: 'ManagedServiceIdentity'
              audience: environment().resourceManager
            }
          }
        }
      }
    }
  }
}

// Contributor on the one Container App only, so the Logic App can call /stop.
var contributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')

resource stopApi_Contributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(api.id, stopApi.id, contributorRoleId)
  scope: api
  properties: {
    principalId: stopApi.identity.principalId
    roleDefinitionId: contributorRoleId
    principalType: 'ServicePrincipal'
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'budget-killswitch'
  location: 'global'
  properties: {
    groupShortName: 'killswitch'
    enabled: true
    logicAppReceivers: [
      {
        name: 'stop-container-app'
        resourceId: stopApi.id
        callbackUrl: listCallbackUrl('${stopApi.id}/triggers/manual', '2019-05-01').value
        useCommonAlertSchema: true
      }
    ]
  }
}

output actionGroupId string = actionGroup.id

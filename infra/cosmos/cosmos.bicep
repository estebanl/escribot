@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' = {
  name: take('cosmos-${uniqueString(resourceGroup().id)}', 44)
  location: location
  properties: {
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
  }
  kind: 'GlobalDocumentDB'
  tags: {
    'aspire-resource-name': 'cosmos'
  }
}

resource fluentescritor 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-08-15' = {
  name: 'fluentescritor'
  location: location
  properties: {
    resource: {
      id: 'fluentescritor'
    }
  }
  parent: cosmos
}

resource users 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'users'
  location: location
  properties: {
    resource: {
      id: 'users'
      partitionKey: {
        paths: [
          '/userId'
        ]
        kind: 'Hash'
      }
    }
  }
  parent: fluentescritor
}

resource shared 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'shared'
  location: location
  properties: {
    resource: {
      id: 'shared'
      partitionKey: {
        paths: [
          '/pk'
        ]
        kind: 'Hash'
      }
    }
  }
  parent: fluentescritor
}

resource lookups 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'lookups'
  location: location
  properties: {
    resource: {
      id: 'lookups'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
      defaultTtl: -1
    }
  }
  parent: fluentescritor
}

resource counters 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'counters'
  location: location
  properties: {
    resource: {
      id: 'counters'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
      defaultTtl: -1
    }
  }
  parent: fluentescritor
}

resource errors 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'errors'
  location: location
  properties: {
    resource: {
      id: 'errors'
      partitionKey: {
        paths: [
          '/day'
        ]
        kind: 'Hash'
      }
      defaultTtl: 2592000
    }
  }
  parent: fluentescritor
}

output connectionString string = cosmos.properties.documentEndpoint
output name string = cosmos.name
output id string = cosmos.id

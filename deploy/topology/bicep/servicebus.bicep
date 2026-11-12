// SourceFlow topology on Azure Service Bus (doc 07 §5, ADR-017).
//
// Creates what `ProvisioningMode.CreateIfMissing` would create for one service, from the JSON written by
// `dotnet sourceflow topology export` with the Azure transport, in an existing namespace: the endpoint queue, the
// topics the service publishes, and on each subscribed topic a subscription that forwards to the queue, with the
// filter's rules. A subscription's first rule is named $Default in the export, so it replaces the match-everything
// rule ARM creates with every subscription. Names and filters come from the export.
//
// Usage:
//   module accounts 'servicebus.bicep' = {
//     name: 'accounts-topology'
//     params: {
//       namespaceName: 'my-namespace'
//       topology: loadJsonContent('accounts.topology.json')
//     }
//   }

@description('The name of an existing Service Bus namespace (Standard or Premium tier) in the deployment\'s resource group.')
param namespaceName string

@description('The JSON written by `dotnet sourceflow topology export` with the Azure transport (loadJsonContent).')
param topology object

@description('Also create the topics the service subscribes to but does not publish (a single deployment holding every service).')
param createSubscribedTopics bool = false

var resources = topology.resources
var queues = filter(resources, r => r.kind == 'queue' && r.owned && r.role == 'endpoint')
var topics = filter(resources, r => r.kind == 'topic' && (r.owned || createSubscribedTopics))
var subscriptions = filter(resources, r => r.kind == 'subscription')
var rules = filter(resources, r => r.kind == 'rule')

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: namespaceName
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [for q in queues: {
  parent: namespace
  name: q.name
  properties: {
    requiresSession: q.properties.requiresSession
    maxDeliveryCount: q.properties.maxDeliveryCount
    lockDuration: q.properties.lockDuration
    defaultMessageTimeToLive: q.properties.defaultMessageTimeToLive
    deadLetteringOnMessageExpiration: q.properties.deadLetteringOnMessageExpiration
    requiresDuplicateDetection: q.properties.requiresDuplicateDetection
    duplicateDetectionHistoryTimeWindow: q.properties.duplicateDetectionHistoryTimeWindow ?? 'PT10M'
  }
}]

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = [for t in topics: {
  parent: namespace
  name: t.name
  properties: {
    defaultMessageTimeToLive: t.properties.defaultMessageTimeToLive
  }
}]

#disable-next-line use-parent-property
resource subscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [for s in subscriptions: {
  name: '${namespaceName}/${s.properties.topic}/${s.properties.subscription}'
  properties: {
    forwardTo: s.properties.forwardTo
    defaultMessageTimeToLive: s.properties.defaultMessageTimeToLive
    maxDeliveryCount: 10
  }
  dependsOn: [
    queue
    topic
  ]
}]

#disable-next-line use-parent-property
resource rule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = [for r in rules: {
  name: '${namespaceName}/${r.properties.topic}/${r.properties.subscription}/${r.properties.rule}'
  properties: r.properties.filterType == 'CorrelationFilter'
    ? {
        filterType: 'CorrelationFilter'
        correlationFilter: {
          properties: r.properties.correlationProperties
        }
      }
    : {
        filterType: 'SqlFilter'
        sqlFilter: {
          sqlExpression: r.properties.sqlExpression
        }
      }
  dependsOn: [
    subscription
  ]
}]

@description('The names of the queues created.')
output queueNames array = [for q in queues: q.name]

@description('The names of the topics created.')
output topicNames array = [for t in topics: t.name]

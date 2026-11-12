# Plans and applies the module against mock providers for the committed fixtures (deploy/topology/fixtures/azure):
# every expression is evaluated and every resource count checked, without an Azure subscription.

mock_provider "azurerm" {
  mock_resource "azurerm_servicebus_queue" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/sourceflow/providers/Microsoft.ServiceBus/namespaces/sourceflow/queues/mock" }
  }
  mock_resource "azurerm_servicebus_topic" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/sourceflow/providers/Microsoft.ServiceBus/namespaces/sourceflow/topics/mock" }
  }
  mock_data "azurerm_servicebus_topic" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/sourceflow/providers/Microsoft.ServiceBus/namespaces/sourceflow/topics/mock" }
  }
  mock_resource "azurerm_servicebus_subscription" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/sourceflow/providers/Microsoft.ServiceBus/namespaces/sourceflow/topics/mock/subscriptions/mock" }
  }
}
mock_provider "azapi" {}

variables {
  namespace_id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/sourceflow/providers/Microsoft.ServiceBus/namespaces/sourceflow"
  create_subscribed_topics = true
}

run "minimal" {
  variables {
    topology = file("../../fixtures/azure/minimal.json")
  }

  assert {
    condition     = length(azurerm_servicebus_queue.endpoint) == 1 && length(azurerm_servicebus_topic.topic) == 0 && length(azurerm_servicebus_subscription.subscription) == 0
    error_message = "minimal: one queue, nothing else"
  }

  assert {
    condition     = azurerm_servicebus_queue.endpoint["dev-minimal"].requires_session == false && azurerm_servicebus_queue.endpoint["dev-minimal"].max_delivery_count == 10
    error_message = "minimal: an unordered queue with the default delivery limit"
  }
}

run "full" {
  variables {
    topology = file("../../fixtures/azure/full.json")
  }

  assert {
    condition     = azurerm_servicebus_queue.endpoint["dev-full"].max_delivery_count == 7 && azurerm_servicebus_queue.endpoint["dev-full"].lock_duration == "PT30S"
    error_message = "full: the topology's delivery limit and the lock"
  }

  assert {
    condition     = azurerm_servicebus_subscription.subscription["dev-full-in/dev-full"].forward_to == "dev-full"
    error_message = "full: the subscription forwards to the endpoint queue"
  }

  assert {
    condition     = azapi_update_resource.default_rule["dev-full-in/dev-full/$Default"].body.properties.sqlFilter.sqlExpression == "[cloudEvents:type] LIKE 'tck.%'"
    error_message = "full: the prefix filter replaces the $Default rule"
  }
}

run "ordered" {
  variables {
    topology = file("../../fixtures/azure/ordered.json")
  }

  assert {
    condition     = azurerm_servicebus_queue.endpoint["dev-ordered"].requires_session && azurerm_servicebus_queue.endpoint["dev-ordered"].max_delivery_count == 5
    error_message = "ordered: sessions on"
  }

  assert {
    condition     = azapi_update_resource.default_rule["dev-ordered-in/dev-ordered/$Default"].body.properties.correlationFilter.properties["cloudEvents:type"] == "tck.ordered.v1"
    error_message = "ordered: an exact-type correlation rule"
  }
}

run "filtered" {
  variables {
    topology = file("../../fixtures/azure/filtered.json")
  }

  assert {
    condition     = length(azurerm_servicebus_subscription.subscription) == 4 && length(azapi_update_resource.default_rule) == 4 && length(azurerm_servicebus_subscription_rule.rule) == 2
    error_message = "filtered: four subscriptions, four $Default rules and two more rules"
  }

  assert {
    condition     = azapi_update_resource.default_rule["dev-tenant/dev-filtered/$Default"].body.properties.sqlFilter.sqlExpression == "1=1"
    error_message = "filtered: a filter Service Bus cannot express matches everything (the receiver evaluates it)"
  }

  assert {
    condition     = azurerm_servicebus_subscription_rule.rule["dev-typed/dev-filtered/sf-type-1"].filter_type == "CorrelationFilter"
    error_message = "filtered: the second exact type is a correlation rule"
  }
}

run "subscriber_uses_the_publishers_topic" {
  variables {
    topology                 = file("../../fixtures/azure/subscriber.json")
    create_subscribed_topics = false
  }

  assert {
    condition     = length(azurerm_servicebus_topic.topic) == 0 && length(data.azurerm_servicebus_topic.existing) == 1
    error_message = "subscriber: the shared topic is looked up, not created"
  }
}

run "wrong_transport" {
  command = plan

  variables {
    topology = file("../../fixtures/aws/minimal.json")
  }

  expect_failures = [terraform_data.transport]
}

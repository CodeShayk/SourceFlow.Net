# SourceFlow topology on Azure Service Bus (doc 07 §5, ADR-017).
#
# Creates what `ProvisioningMode.CreateIfMissing` would create for one service, from the JSON written by
# `dotnet sourceflow topology export` with the Azure transport, in an existing namespace: the endpoint queue (sessions
# when ordered, the delivery limit, lock, time to live, dead-lettering on expiry, duplicate detection when configured),
# the topics the service publishes, and on each subscribed topic a subscription that forwards to the queue, with the
# filter's rules.
#
# ARM creates a match-everything rule called $Default with every subscription. The export names each subscription's
# first rule $Default, and this module overwrites that rule (azapi_update_resource) instead of adding one beside it;
# the other rules are ordinary rule resources. Names and filters come from the export.

terraform {
  required_version = ">= 1.5"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = ">= 3.90"
    }
    azapi = {
      source  = "Azure/azapi"
      version = ">= 2.0"
    }
  }
}

locals {
  topology = jsondecode(var.topology)
  # Another transport's export has other properties: nothing is read from it, and the precondition below reports it.
  resources = [for r in local.topology.resources : r if local.topology.transport == "servicebus"]

  queues = {
    for r in local.resources : r.name => {
      requires_session                        = r.properties.requiresSession
      max_delivery_count                      = r.properties.maxDeliveryCount
      lock_duration                           = r.properties.lockDuration
      default_message_ttl                     = r.properties.defaultMessageTimeToLive
      dead_lettering_on_message_expiration    = r.properties.deadLetteringOnMessageExpiration
      requires_duplicate_detection            = r.properties.requiresDuplicateDetection
      duplicate_detection_history_time_window = r.properties.duplicateDetectionHistoryTimeWindow
    } if r.kind == "queue" && r.owned && r.role == "endpoint"
  }

  created_topics = {
    for r in local.resources : r.name => { default_message_ttl = r.properties.defaultMessageTimeToLive }
    if r.kind == "topic" && (r.owned || var.create_subscribed_topics)
  }

  existing_topics = toset([for r in local.resources : r.name if r.kind == "topic" && !(r.owned || var.create_subscribed_topics)])

  topic_ids = merge(
    { for name, topic in azurerm_servicebus_topic.topic : name => topic.id },
    { for name, topic in data.azurerm_servicebus_topic.existing : name => topic.id },
  )

  subscriptions = {
    for r in local.resources : r.name => {
      topic               = r.properties.topic
      subscription        = r.properties.subscription
      forward_to          = r.properties.forwardTo
      default_message_ttl = r.properties.defaultMessageTimeToLive
    } if r.kind == "subscription"
  }

  rules = {
    for r in local.resources : r.name => {
      subscription           = "${r.properties.topic}/${r.properties.subscription}"
      rule                   = r.properties.rule
      filter_type            = r.properties.filterType
      sql_expression         = r.properties.sqlExpression
      correlation_properties = r.properties.correlationProperties == null ? null : tomap(r.properties.correlationProperties)
    } if r.kind == "rule"
  }
}

resource "terraform_data" "transport" {
  input = local.topology.transport

  lifecycle {
    precondition {
      condition     = local.topology.transport == "servicebus" && local.topology.schemaVersion == 1
      error_message = "The topology export is for '${coalesce(local.topology.transport, "no transport")}' (schema ${local.topology.schemaVersion}); this module needs a servicebus export with schemaVersion 1."
    }
  }
}

resource "azurerm_servicebus_queue" "endpoint" {
  for_each = local.queues

  name                                    = each.key
  namespace_id                            = var.namespace_id
  requires_session                        = each.value.requires_session
  max_delivery_count                      = each.value.max_delivery_count
  lock_duration                           = each.value.lock_duration
  default_message_ttl                     = each.value.default_message_ttl
  dead_lettering_on_message_expiration    = each.value.dead_lettering_on_message_expiration
  requires_duplicate_detection            = each.value.requires_duplicate_detection
  duplicate_detection_history_time_window = each.value.duplicate_detection_history_time_window
}

resource "azurerm_servicebus_topic" "topic" {
  for_each = local.created_topics

  name                = each.key
  namespace_id        = var.namespace_id
  default_message_ttl = each.value.default_message_ttl
}

# Topics the service subscribes to but does not publish belong to their publishers' stacks.
data "azurerm_servicebus_topic" "existing" {
  for_each = local.existing_topics

  name         = each.value
  namespace_id = var.namespace_id
}

resource "azurerm_servicebus_subscription" "subscription" {
  for_each = local.subscriptions

  name                = each.value.subscription
  topic_id            = local.topic_ids[each.value.topic]
  forward_to          = each.value.forward_to
  default_message_ttl = each.value.default_message_ttl
  max_delivery_count  = 10

  depends_on = [azurerm_servicebus_queue.endpoint]
}

# The first rule of each subscription replaces the $Default rule ARM created with it.
resource "azapi_update_resource" "default_rule" {
  for_each = { for name, rule in local.rules : name => rule if rule.rule == "$Default" }

  type      = "Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2021-11-01"
  name      = "$Default"
  parent_id = azurerm_servicebus_subscription.subscription[each.value.subscription].id
  body = {
    properties = merge(
      { filterType = each.value.filter_type },
      each.value.filter_type == "SqlFilter" ? { sqlFilter = { sqlExpression = each.value.sql_expression } } : {},
      each.value.filter_type == "CorrelationFilter" ? { correlationFilter = { properties = each.value.correlation_properties } } : {},
    )
  }
}

resource "azurerm_servicebus_subscription_rule" "rule" {
  for_each = { for name, rule in local.rules : name => rule if rule.rule != "$Default" }

  name            = each.value.rule
  subscription_id = azurerm_servicebus_subscription.subscription[each.value.subscription].id
  filter_type     = each.value.filter_type
  sql_filter      = each.value.filter_type == "SqlFilter" ? each.value.sql_expression : null

  dynamic "correlation_filter" {
    for_each = each.value.filter_type == "CorrelationFilter" ? [each.value.correlation_properties] : []
    content {
      properties = correlation_filter.value
    }
  }
}

output "queue_ids" {
  description = "The resource ids of the service's queues, by name."
  value       = { for name, queue in azurerm_servicebus_queue.endpoint : name => queue.id }
}

output "topic_ids" {
  description = "The resource ids of the topics the service publishes or subscribes to, by name."
  value       = local.topic_ids
}

output "subscription_ids" {
  description = "The resource ids of the service's subscriptions, by topic/subscription."
  value       = { for name, subscription in azurerm_servicebus_subscription.subscription : name => subscription.id }
}

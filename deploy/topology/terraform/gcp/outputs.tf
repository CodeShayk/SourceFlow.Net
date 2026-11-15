output "topic_ids" {
  description = "The full names of the topics this module created, by topic id."
  value       = { for name, topic in google_pubsub_topic.topic : name => topic.id }
}

output "subscription_ids" {
  description = "The full names of the service's subscriptions, by subscription id."
  value       = { for name, subscription in google_pubsub_subscription.subscription : name => subscription.id }
}

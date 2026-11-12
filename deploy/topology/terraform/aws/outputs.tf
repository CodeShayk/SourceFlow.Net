output "queue_urls" {
  description = "The URLs of the service's queues (endpoint and dead letters), by name."
  value = merge(
    { for name, queue in aws_sqs_queue.endpoint : name => queue.url },
    { for name, queue in aws_sqs_queue.dead_letter : name => queue.url },
  )
}

output "queue_arns" {
  description = "The ARNs of the service's queues, by name."
  value = merge(
    { for name, queue in aws_sqs_queue.endpoint : name => queue.arn },
    { for name, queue in aws_sqs_queue.dead_letter : name => queue.arn },
  )
}

output "topic_arns" {
  description = "The ARNs of the topics the service publishes or subscribes to, by name."
  value       = local.topic_arns
}

output "runtime_policy_json" {
  description = "An IAM policy for the service at runtime with ProvisioningMode.Validate: receive and settle, publish, send to remote endpoints, and describe. Attach it to the service's role."
  value = jsonencode({
    Version   = "2012-10-17"
    Statement = local.runtime_statements
  })
}

# SourceFlow topology on Amazon SQS and SNS (doc 07 §5, ADR-017).
#
# Creates what `ProvisioningMode.CreateIfMissing` would create for one service, from the JSON written by
# `dotnet sourceflow topology export` with the AWS transport: the endpoint queue with a redrive policy to its
# dead-letter queue (FIFO when ordered), the topics the service publishes, raw-delivery subscriptions with the
# filter policies, and the queue policy that lets the subscribed topics send. The service can then run with
# `ProvisioningMode.Validate` and describe-only permissions (`runtime_policy_json`).
#
# Names, ordering and filters come from the export; this module computes none of them.

terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 6.0"
    }
  }
}

locals {
  topology = jsondecode(var.topology)
  # Another transport's export has other properties: nothing is read from it, and the precondition below reports it.
  resources = [for r in local.topology.resources : r if local.topology.transport == "aws_sqs"]

  dead_letter_queues = {
    for r in local.resources : r.name => {
      fifo      = r.properties.fifo
      retention = r.properties.messageRetentionSeconds
    } if r.kind == "queue" && r.owned && r.role == "dead-letter"
  }

  endpoint_queues = {
    for r in local.resources : r.name => {
      fifo              = r.properties.fifo
      retention         = r.properties.messageRetentionSeconds
      dead_letter_queue = r.properties.deadLetterQueue
      max_receive_count = r.properties.maxReceiveCount
      allow_topics      = tolist(r.properties.allowTopics)
    } if r.kind == "queue" && r.owned && r.role == "endpoint"
  }

  remote_queues = flatten([
    for r in local.resources : tolist(r.properties.candidates) if r.kind == "queue" && !r.owned && r.role == "remote-endpoint"
  ])

  created_topics = {
    for r in local.resources : r.name => { fifo = r.properties.fifo }
    if r.kind == "topic" && (r.owned || var.create_subscribed_topics)
  }

  existing_topics = toset([
    for r in local.resources : r.name if r.kind == "topic" && !(r.owned || var.create_subscribed_topics)
  ])

  published_topics = toset([for r in local.resources : r.name if r.kind == "topic" && r.owned])

  subscriptions = {
    for r in local.resources : r.name => {
      topic                = r.properties.topic
      queue                = r.properties.queue
      raw_message_delivery = r.properties.rawMessageDelivery
      filter_policy        = r.properties.filterPolicy
      filter_policy_scope  = r.properties.filterPolicyScope
    } if r.kind == "subscription"
  }

  topic_arns = merge(
    { for name, topic in aws_sns_topic.topic : name => topic.arn },
    { for name, topic in data.aws_sns_topic.existing : name => topic.arn },
  )
}

resource "terraform_data" "transport" {
  input = local.topology.transport

  lifecycle {
    precondition {
      condition     = local.topology.transport == "aws_sqs" && local.topology.schemaVersion == 1
      error_message = "The topology export is for '${coalesce(local.topology.transport, "no transport")}' (schema ${local.topology.schemaVersion}); this module needs an aws_sqs export with schemaVersion 1."
    }
  }
}

resource "aws_sqs_queue" "dead_letter" {
  for_each = local.dead_letter_queues

  name                      = each.key
  fifo_queue                = each.value.fifo ? true : null
  message_retention_seconds = each.value.retention
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "endpoint" {
  for_each = local.endpoint_queues

  name                      = each.key
  fifo_queue                = each.value.fifo ? true : null
  message_retention_seconds = each.value.retention
  sqs_managed_sse_enabled   = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dead_letter[each.value.dead_letter_queue].arn
    maxReceiveCount     = each.value.max_receive_count
  })
  tags = var.tags
}

resource "aws_sns_topic" "topic" {
  for_each = local.created_topics

  name       = each.key
  fifo_topic = each.value.fifo ? true : null
  tags       = var.tags
}

# Topics the service subscribes to but does not publish belong to their publishers' stacks.
data "aws_sns_topic" "existing" {
  for_each = local.existing_topics

  name = each.value
}

resource "aws_sqs_queue_policy" "endpoint" {
  for_each = { for name, queue in local.endpoint_queues : name => queue if length(queue.allow_topics) > 0 }

  queue_url = aws_sqs_queue.endpoint[each.key].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "sourceflow-sns-to-sqs"
      Effect    = "Allow"
      Principal = { Service = "sns.amazonaws.com" }
      Action    = "sqs:SendMessage"
      Resource  = aws_sqs_queue.endpoint[each.key].arn
      Condition = { ArnEquals = { "aws:SourceArn" = [for topic in each.value.allow_topics : local.topic_arns[topic]] } }
    }]
  })
}

resource "aws_sns_topic_subscription" "subscription" {
  for_each = local.subscriptions

  topic_arn            = local.topic_arns[each.value.topic]
  protocol             = "sqs"
  endpoint             = aws_sqs_queue.endpoint[each.value.queue].arn
  raw_message_delivery = each.value.raw_message_delivery
  filter_policy        = each.value.filter_policy
  filter_policy_scope  = each.value.filter_policy_scope

  depends_on = [aws_sqs_queue_policy.endpoint]
}

locals {
  # arn:{partition}:sqs:{region}:{account}, from the endpoint queue (no STS call).
  queue_arn_prefix = try(join(":", slice(split(":", one([for name, queue in aws_sqs_queue.endpoint : queue.arn])), 0, 5)), null)

  # The service at runtime: receive and settle on its queues, publish to its topics, send to remote endpoints, and
  # describe everything for ProvisioningMode.Validate. No create, delete or set permission.
  runtime_statements = concat(
    [{
      Sid    = "Receive"
      Effect = "Allow"
      Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes", "sqs:GetQueueUrl", "sqs:SendMessage"]
      Resource = concat(
        [for name, queue in aws_sqs_queue.endpoint : queue.arn],
        [for name, queue in aws_sqs_queue.dead_letter : queue.arn],
      )
    }],
    length(local.published_topics) == 0 ? [] : [{
      Sid      = "Publish"
      Effect   = "Allow"
      Action   = ["sns:Publish", "sns:GetTopicAttributes"]
      Resource = [for name in local.published_topics : local.topic_arns[name]]
    }],
    length(local.remote_queues) == 0 ? [] : [{
      Sid      = "Send"
      Effect   = "Allow"
      Action   = ["sqs:SendMessage", "sqs:GetQueueUrl", "sqs:GetQueueAttributes"]
      Resource = [for name in local.remote_queues : "${local.queue_arn_prefix}:${name}"]
    }],
    [{
      Sid      = "Describe"
      Effect   = "Allow"
      Action   = ["sqs:GetQueueUrl", "sqs:GetQueueAttributes", "sqs:ListQueues", "sns:ListTopics", "sns:GetTopicAttributes", "sns:ListSubscriptionsByTopic", "sns:GetSubscriptionAttributes"]
      Resource = ["*"]
    }],
  )
}

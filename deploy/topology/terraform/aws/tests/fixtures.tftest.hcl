# Plans and applies the module against a mock provider for the committed fixtures (deploy/topology/fixtures/aws).
# The emulator test (tests/Cloud/SourceFlow.Cloud.Tests/Topology) applies it to LocalStack and validates the result
# with the transport.

mock_provider "aws" {
  mock_data "aws_sns_topic" {
    defaults = { arn = "arn:aws:sns:eu-west-1:123456789012:mock" }
  }
  mock_resource "aws_sns_topic" {
    defaults = { arn = "arn:aws:sns:eu-west-1:123456789012:mock" }
  }
  mock_resource "aws_sqs_queue" {
    defaults = {
      arn = "arn:aws:sqs:eu-west-1:123456789012:mock"
      id  = "https://sqs.eu-west-1.amazonaws.com/123456789012/mock"
      url = "https://sqs.eu-west-1.amazonaws.com/123456789012/mock"
    }
  }
}

variables {
  create_subscribed_topics = true
}

run "full" {
  variables {
    topology = file("../../fixtures/aws/full.json")
  }

  assert {
    condition     = length(aws_sqs_queue.endpoint) == 1 && length(aws_sqs_queue.dead_letter) == 1 && length(aws_sns_topic.topic) == 2 && length(aws_sns_topic_subscription.subscription) == 1
    error_message = "full: endpoint, dead letters, two topics, one subscription"
  }

  assert {
    condition     = jsondecode(aws_sqs_queue.endpoint["dev-full"].redrive_policy).maxReceiveCount == 7
    error_message = "full: the redrive policy has the topology's delivery limit"
  }

  assert {
    condition     = aws_sns_topic_subscription.subscription["dev-full-in/dev-full"].raw_message_delivery && aws_sns_topic_subscription.subscription["dev-full-in/dev-full"].filter_policy_scope == "MessageAttributes"
    error_message = "full: raw delivery with a message-attribute filter policy"
  }

  assert {
    condition     = length(aws_sqs_queue_policy.endpoint) == 1
    error_message = "full: the queue policy lets the subscribed topic send"
  }
}

run "ordered" {
  variables {
    topology = file("../../fixtures/aws/ordered.json")
  }

  assert {
    condition     = aws_sqs_queue.endpoint["dev-ordered.fifo"].fifo_queue && aws_sqs_queue.dead_letter["dev-ordered-dlq.fifo"].fifo_queue && aws_sns_topic.topic["dev-ordered-out.fifo"].fifo_topic
    error_message = "ordered: FIFO queues and topics"
  }

  assert {
    condition     = strcontains(output.runtime_policy_json, "arn:aws:sqs:eu-west-1:123456789012:dev-minimal.fifo")
    error_message = "ordered: the runtime policy may send to both candidates of the remote endpoint"
  }
}

run "filtered" {
  variables {
    topology = file("../../fixtures/aws/filtered.json")
  }

  assert {
    condition     = length(aws_sns_topic_subscription.subscription) == 4
    error_message = "filtered: four subscriptions"
  }

  assert {
    condition     = aws_sns_topic_subscription.subscription["dev-tenant/dev-filtered"].filter_policy == null && aws_sns_topic_subscription.subscription["dev-everything/dev-filtered"].filter_policy == null
    error_message = "filtered: no policy where SNS cannot express the filter (the receiver evaluates it)"
  }
}

run "subscriber_uses_the_publishers_topic" {
  variables {
    topology                 = file("../../fixtures/aws/subscriber.json")
    create_subscribed_topics = false
  }

  assert {
    condition     = length(aws_sns_topic.topic) == 0 && length(data.aws_sns_topic.existing) == 1
    error_message = "subscriber: the shared topic is looked up, not created"
  }
}

run "wrong_transport" {
  command = plan

  variables {
    topology = file("../../fixtures/gcp/minimal.json")
  }

  expect_failures = [terraform_data.transport]
}

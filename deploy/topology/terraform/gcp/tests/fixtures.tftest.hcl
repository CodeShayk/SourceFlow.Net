# Plans and applies the module against a mock provider for the committed fixtures (deploy/topology/fixtures/gcp).
# The emulator test (tests/Cloud/SourceFlow.Cloud.Tests/Topology) applies it to the Pub/Sub emulator and validates the
# result with the transport.

mock_provider "google" {
  mock_data "google_project" {
    defaults = { number = "123456789" }
  }
}

variables {
  project                  = "sourceflow"
  create_subscribed_topics = true
}

run "full" {
  variables {
    topology = file("../../fixtures/gcp/full.json")
  }

  assert {
    condition     = length(google_pubsub_topic.topic) == 4 && length(google_pubsub_subscription.subscription) == 3
    error_message = "full: dead-letter, command, published and subscribed topics; dead-letter, endpoint and topic subscriptions"
  }

  assert {
    condition     = google_pubsub_subscription.subscription["dev-full"].dead_letter_policy[0].max_delivery_attempts == 7 && google_pubsub_subscription.subscription["dev-full"].labels["sf-max-deliveries"] == "7"
    error_message = "full: the dead-letter policy and the delivery-limit label"
  }

  assert {
    condition     = length(google_pubsub_subscription.subscription["dev-full+dlq"].dead_letter_policy) == 0
    error_message = "full: the dead-letter subscription has no dead-letter policy"
  }

  assert {
    condition     = length(google_pubsub_topic_iam_member.service_agent_publisher) == 1 && length(google_pubsub_subscription_iam_member.service_agent_subscriber) == 2
    error_message = "full: the service agent may publish dead letters and read the subscriptions with a policy"
  }

  assert {
    condition     = google_pubsub_topic_iam_member.service_agent_publisher["dev-full+dlq"].member == "serviceAccount:service-123456789@gcp-sa-pubsub.iam.gserviceaccount.com"
    error_message = "full: the grant goes to the project's Pub/Sub service agent"
  }
}

run "ordered" {
  variables {
    topology       = file("../../fixtures/gcp/ordered.json")
    runtime_member = "serviceAccount:ordered@sourceflow.iam.gserviceaccount.com"
  }

  assert {
    condition     = google_pubsub_subscription.subscription["dev-ordered"].enable_message_ordering && google_pubsub_subscription.subscription["dev-ordered"].dead_letter_policy[0].max_delivery_attempts == 5
    error_message = "ordered: ordering on, delivery limit 5"
  }

  assert {
    condition     = contains(keys(google_pubsub_topic_iam_member.runtime_publisher), "dev-minimal+in") && contains(keys(google_pubsub_topic_iam_member.runtime_publisher), "dev-ordered-out")
    error_message = "ordered: the service may publish its topic and send to the remote endpoint"
  }
}

run "filtered" {
  variables {
    topology = file("../../fixtures/gcp/filtered.json")
  }

  assert {
    condition     = google_pubsub_subscription.subscription["dev-filtered~tenant"].filter == null && google_pubsub_subscription.subscription["dev-filtered~typed"].filter != null
    error_message = "filtered: a native filter where Pub/Sub can express it"
  }
}

run "subscriber_uses_the_publishers_topic" {
  variables {
    topology                 = file("../../fixtures/gcp/subscriber.json")
    create_subscribed_topics = false
  }

  assert {
    condition     = length(google_pubsub_topic.topic) == 2
    error_message = "subscriber: only its own command and dead-letter topics"
  }
}

run "wrong_transport" {
  command = plan

  variables {
    topology = file("../../fixtures/azure/minimal.json")
  }

  expect_failures = [terraform_data.transport]
}

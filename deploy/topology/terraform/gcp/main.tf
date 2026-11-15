# SourceFlow topology on Google Cloud Pub/Sub (doc 07 §5, ADR-017).
#
# Creates what `ProvisioningMode.CreateIfMissing` would create for one service, from the JSON written by
# `dotnet sourceflow topology export` with the Google Cloud transport: the endpoint's command topic and subscription,
# its dead-letter topic and subscription, the topics the service publishes, and one subscription per subscribed topic
# (ordering, dead-letter policy, delivery-limit label, filter, no expiry).
#
# It also grants the Pub/Sub service agent what dead-lettering needs (publisher on the dead-letter topic, subscriber on
# each subscription with a dead-letter policy), and optionally the service's own identity what it needs at runtime with
# `ProvisioningMode.Validate` (`runtime_member`). Names, ordering and filters come from the export.

terraform {
  required_version = ">= 1.5"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = ">= 5.0"
    }
  }
}

locals {
  topology = jsondecode(var.topology)
  # Another transport's export has other properties: nothing is read from it, and the precondition below reports it.
  resources = [for r in local.topology.resources : r if local.topology.transport == "gcp_pubsub"]

  created_topics = toset([
    for r in local.resources : r.name if r.kind == "topic" && r.role != "remote-endpoint" && (r.owned || var.create_subscribed_topics)
  ])

  subscriptions = {
    for r in local.resources : r.name => {
      role                       = r.role
      topic                      = r.properties.topic
      ack_deadline_seconds       = r.properties.ackDeadlineSeconds
      enable_message_ordering    = r.properties.enableMessageOrdering
      message_retention_duration = r.properties.messageRetentionDuration
      filter                     = r.properties.filter
      labels                     = tomap(r.properties.labels)
      dead_letter_topic          = r.properties.deadLetterTopic
      max_delivery_attempts      = r.properties.maxDeliveryAttempts
    } if r.kind == "subscription"
  }

  dead_letter_topics = toset([for r in local.resources : r.name if r.kind == "topic" && r.role == "dead-letter"])
  published_topics   = toset([for r in local.resources : r.name if r.kind == "topic" && r.role == "topic" && r.owned])
  remote_topics      = toset([for r in local.resources : r.name if r.kind == "topic" && r.role == "remote-endpoint"])
  all_topics         = toset([for r in local.resources : r.name if r.kind == "topic" && r.role != "remote-endpoint"])

  service_agent = var.grant_service_agent ? "serviceAccount:service-${data.google_project.project[0].number}@gcp-sa-pubsub.iam.gserviceaccount.com" : null
}

resource "terraform_data" "transport" {
  input = local.topology.transport

  lifecycle {
    precondition {
      condition     = local.topology.transport == "gcp_pubsub" && local.topology.schemaVersion == 1
      error_message = "The topology export is for '${coalesce(local.topology.transport, "no transport")}' (schema ${local.topology.schemaVersion}); this module needs a gcp_pubsub export with schemaVersion 1."
    }
  }
}

resource "google_pubsub_topic" "topic" {
  for_each = local.created_topics

  project = var.project
  name    = each.value
  labels  = var.labels
}

resource "google_pubsub_subscription" "subscription" {
  for_each = local.subscriptions

  project                    = var.project
  name                       = each.key
  topic                      = "projects/${var.project}/topics/${each.value.topic}"
  ack_deadline_seconds       = each.value.ack_deadline_seconds
  enable_message_ordering    = each.value.enable_message_ordering
  message_retention_duration = each.value.message_retention_duration
  filter                     = each.value.filter == "" ? null : each.value.filter
  labels                     = merge(var.labels, each.value.labels)

  expiration_policy {
    ttl = "" # never expires
  }

  dynamic "dead_letter_policy" {
    for_each = each.value.dead_letter_topic == null ? [] : [each.value]
    content {
      dead_letter_topic     = "projects/${var.project}/topics/${dead_letter_policy.value.dead_letter_topic}"
      max_delivery_attempts = dead_letter_policy.value.max_delivery_attempts
    }
  }

  depends_on = [google_pubsub_topic.topic]
}

# ---------------------------------------------------------------- the Pub/Sub service agent (dead-lettering)

data "google_project" "project" {
  count      = var.grant_service_agent ? 1 : 0
  project_id = var.project
}

resource "google_pubsub_topic_iam_member" "service_agent_publisher" {
  for_each = var.grant_service_agent ? local.dead_letter_topics : toset([])

  project = var.project
  topic   = google_pubsub_topic.topic[each.value].name
  role    = "roles/pubsub.publisher"
  member  = local.service_agent
}

resource "google_pubsub_subscription_iam_member" "service_agent_subscriber" {
  for_each = var.grant_service_agent ? toset([for name, s in local.subscriptions : name if s.dead_letter_topic != null]) : toset([])

  project      = var.project
  subscription = google_pubsub_subscription.subscription[each.value].name
  role         = "roles/pubsub.subscriber"
  member       = local.service_agent
}

# ---------------------------------------------------------------- the service at runtime (optional)

resource "google_pubsub_subscription_iam_member" "runtime_subscriber" {
  for_each = var.runtime_member == null ? toset([]) : toset(keys(local.subscriptions))

  project      = var.project
  subscription = google_pubsub_subscription.subscription[each.value].name
  role         = "roles/pubsub.subscriber"
  member       = var.runtime_member
}

resource "google_pubsub_subscription_iam_member" "runtime_viewer" {
  for_each = var.runtime_member == null ? toset([]) : toset(keys(local.subscriptions))

  project      = var.project
  subscription = google_pubsub_subscription.subscription[each.value].name
  role         = "roles/pubsub.viewer"
  member       = var.runtime_member
}

# Publishes events, sends commands to remote endpoints, and moves malformed messages to its dead-letter topic.
resource "google_pubsub_topic_iam_member" "runtime_publisher" {
  for_each = var.runtime_member == null ? toset([]) : setunion(local.published_topics, local.remote_topics, local.dead_letter_topics)

  project = var.project
  topic   = each.value
  role    = "roles/pubsub.publisher"
  member  = var.runtime_member

  depends_on = [google_pubsub_topic.topic]
}

# Validate reads every topic of the topology.
resource "google_pubsub_topic_iam_member" "runtime_viewer" {
  for_each = var.runtime_member == null ? toset([]) : local.all_topics

  project = var.project
  topic   = each.value
  role    = "roles/pubsub.viewer"
  member  = var.runtime_member

  depends_on = [google_pubsub_topic.topic]
}

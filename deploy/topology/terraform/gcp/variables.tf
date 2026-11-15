variable "project" {
  description = "The Google Cloud project id (GcpTransportOptions.ProjectId)."
  type        = string
}

variable "topology" {
  description = "The JSON written by `dotnet sourceflow topology export` for the service, with the Google Cloud transport (for example file(\"accounts.topology.json\"))."
  type        = string
}

variable "create_subscribed_topics" {
  description = "Also create the topics the service subscribes to but does not publish. Leave false when each publisher's stack creates its topics; set true for a single stack holding every service."
  type        = bool
  default     = false
}

variable "grant_service_agent" {
  description = "Grant the project's Pub/Sub service agent publisher on the dead-letter topic and subscriber on each subscription with a dead-letter policy, which dead-lettering needs. Without it the receiver still enforces the delivery limit itself."
  type        = bool
  default     = true
}

variable "runtime_member" {
  description = "The service's identity (for example serviceAccount:accounts@my-project.iam.gserviceaccount.com). When set, it gets subscriber and viewer on its subscriptions, publisher on its published topics, remote endpoints and dead-letter topic, and viewer on every topic of the topology: what it needs with ProvisioningMode.Validate."
  type        = string
  default     = null
}

variable "labels" {
  description = "Labels for every topic and subscription."
  type        = map(string)
  default     = {}
}

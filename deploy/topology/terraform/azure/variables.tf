variable "namespace_id" {
  description = "The resource id of the Service Bus namespace (Standard or Premium tier: topics need Standard)."
  type        = string
}

variable "topology" {
  description = "The JSON written by `dotnet sourceflow topology export` for the service, with the Azure transport (for example file(\"accounts.topology.json\"))."
  type        = string
}

variable "create_subscribed_topics" {
  description = "Also create the topics the service subscribes to but does not publish. Leave false when each publisher's stack creates its topics; set true for a single stack holding every service."
  type        = bool
  default     = false
}

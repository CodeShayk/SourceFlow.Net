variable "topology" {
  description = "The JSON written by `dotnet sourceflow topology export` for the service, with the AWS transport (for example file(\"accounts.topology.json\"))."
  type        = string
}

variable "create_subscribed_topics" {
  description = "Also create the topics the service subscribes to but does not publish. Leave false when each publisher's stack creates its topics; set true for a single stack holding every service."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags for every queue and topic."
  type        = map(string)
  default     = {}
}

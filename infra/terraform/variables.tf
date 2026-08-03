variable "region" {
  type        = string
  description = "AWS region (must support ECS Express Mode)."
}

variable "project_name" {
  type        = string
  default     = "telegrambotplatform"
  description = "Name prefix and tag value for created resources."
}

variable "image_tag" {
  type        = string
  description = "Container image tag to deploy (CI sets this to the git SHA)."
}

variable "admin_publicly_routable" {
  type        = bool
  default     = false
  description = <<-EOT
    Expose /admin/* through the public API. Default false: the operator API is reached with
    `aws ecs execute-command` instead, which keeps it off the internet entirely.

    Setting this true leaves the admin key as the ONLY control — the WAF IP allowlist that used to
    front /admin cannot be carried over, because WAF does not support API Gateway HTTP APIs.
  EOT
}

variable "admin_api_key" {
  type        = string
  sensitive   = true
  description = "Platform admin API key. Provide via CI secret or non-committed *.auto.tfvars."
}

variable "db_instance_class" {
  type        = string
  default     = "db.t4g.micro"
  description = "RDS instance class (Graviton, single-AZ; sized for the tiny registry workload)."
}

variable "db_engine_version" {
  type        = string
  default     = "17"
  description = "PostgreSQL major version; RDS selects the latest supported minor."
}

variable "db_allocated_storage" {
  type        = number
  default     = 20
  description = "Initial RDS gp3 storage in GiB."
}

variable "db_max_allocated_storage" {
  type        = number
  default     = 100
  description = "Upper bound for RDS storage autoscaling in GiB."
}

variable "db_name" {
  type        = string
  default     = "telegrambotplatform"
  description = "Database (catalog) name; must match the app's expected connection string."
}

variable "db_username" {
  type        = string
  default     = "telegrambotplatform"
  description = "RDS master username."
}

variable "behaviors_prefix" {
  type        = string
  default     = "behaviors/"
  description = "Key prefix for behavior-extension packages in the extension bucket. Must end with '/'."

  validation {
    condition     = endswith(var.behaviors_prefix, "/")
    error_message = "behaviors_prefix must end with '/' so the IAM prefix condition and the application agree."
  }
}

variable "webhook_base_url" {
  type        = string
  default     = ""
  description = <<-EOT
    Override for the public HTTPS base URL bots register their webhooks under. Leave empty (the
    default) and the stack wires itself from the API Gateway endpoint in a single apply — the
    two-phase apply Express Mode required is gone.

    Set this only for a custom domain. It MUST end in the webhook path prefix, e.g.
    "https://bots.example.com/telegram-bot/webhook", because BotSupervisor appends just "/{botId}".
  EOT
}

variable "desired_count" {
  type        = number
  default     = 1
  description = "Number of Fargate tasks. Single-task by design (no multi-AZ HA); see the availability decision."
}

variable "cpu_architecture" {
  type        = string
  default     = "ARM64"
  description = <<-EOT
    Fargate CPU architecture. ARM64 (Graviton) is ~20% cheaper than X86_64 and is only reachable
    because this stack left ECS Express Mode, which has no runtime_platform attribute.

    The image must be built for the matching platform — CI builds arm64 on an ARM runner. Set to
    X86_64 if you cannot build arm64 images; that costs about $4/mo of the saving.
  EOT

  validation {
    condition     = contains(["ARM64", "X86_64"], var.cpu_architecture)
    error_message = "cpu_architecture must be ARM64 or X86_64."
  }
}

variable "cpu" {
  type        = string
  default     = "512"
  description = "Fargate task CPU units (power of 2, 256-4096)."
}

variable "memory" {
  type        = string
  default     = "1024"
  description = "Fargate task memory in MiB (512-8192)."
}

variable "log_retention_days" {
  type        = number
  default     = 30
  description = "CloudWatch log retention in days (application and API Gateway access logs)."
}

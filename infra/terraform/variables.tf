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

variable "admin_allowlist_cidrs" {
  type        = list(string)
  description = "Source CIDRs permitted to reach /admin/* (in addition to the admin key)."
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

variable "webhook_base_url" {
  type        = string
  default     = ""
  description = "Public HTTPS base URL for Telegram webhooks. Set on the phase-2 apply from the endpoint_url output (research R3)."
}

variable "cpu" {
  type        = string
  default     = "512"
  description = "Express task CPU units (power of 2, 256-4096)."
}

variable "memory" {
  type        = string
  default     = "1024"
  description = "Express task memory in MiB (512-8192)."
}

variable "log_retention_days" {
  type        = number
  default     = 30
  description = "CloudWatch log retention in days."
}

variable "express_infrastructure_role_policy_arn" {
  type        = string
  default     = "arn:aws:iam::aws:policy/service-role/AmazonECSInfrastructureRoleforExpressGatewayServices"
  description = "AWS-managed policy attached to the Express Mode infrastructure role."
}

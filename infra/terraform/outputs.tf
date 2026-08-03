output "ecr_repository_url" {
  value       = aws_ecr_repository.app.repository_url
  description = "Image push target (used by CI and push-image.sh)."
}

output "cluster_name" {
  value       = aws_ecs_cluster.app.name
  description = "ECS cluster hosting the service (used by CI to deploy)."
}

output "service_name" {
  value       = aws_ecs_service.app.name
  description = "ECS service name (used by CI to deploy)."
}

output "task_definition_family" {
  value       = aws_ecs_task_definition.app.family
  description = "Task-definition family CI registers new revisions against."
}

# Non-sensitive: a bucket name is not a credential, and the bucket is private + TLS-only.
output "behaviors_bucket_name" {
  value       = aws_s3_bucket.behaviors.bucket
  description = "S3 bucket holding operator-uploaded behavior-extension packages."
}

output "log_group_name" {
  value       = aws_cloudwatch_log_group.app.name
  description = "CloudWatch log group holding application logs."
}

# --- Managed database (feature 002-rds-postgres). The connection string / password are NEVER output. ---
output "db_endpoint" {
  value       = aws_db_instance.postgres.address
  description = "RDS PostgreSQL endpoint host (no credentials)."
}

output "db_instance_id" {
  value       = aws_db_instance.postgres.identifier
  description = "RDS instance identifier."
}

output "endpoint_url" {
  value       = aws_apigatewayv2_api.app.api_endpoint
  description = "Public HTTPS base URL (API Gateway). Only the routes in apigateway.tf are reachable."
}

# What each bot's webhook is actually registered under. Nothing needs to be fed back in — the task
# definition already consumes this value, so the stack converges in one apply.
output "webhook_base_url" {
  value       = local.webhook_base_url
  description = "Base URL bots register webhooks under; BotSupervisor appends '/{botId}'."
}

output "admin_publicly_routable" {
  value       = var.admin_publicly_routable
  description = "Whether /admin/* is exposed publicly. False means reach it via `aws ecs execute-command`."
}

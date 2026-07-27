output "ecr_repository_url" {
  value       = aws_ecr_repository.app.repository_url
  description = "Image push target (used by CI and push-image.sh)."
}

output "service_arn" {
  value       = aws_ecs_express_gateway_service.app.service_arn
  description = "Express service ARN (used by CI to deploy)."
}

output "log_group_name" {
  value       = aws_cloudwatch_log_group.app.name
  description = "CloudWatch log group holding application logs."
}

# The nested shape of `ingress_paths` is not documented for this new resource. Output the raw value
# so you can inspect it (e.g. `terraform state show aws_ecs_express_gateway_service.app`).
output "ingress_paths" {
  value       = aws_ecs_express_gateway_service.app.ingress_paths
  description = "Raw ingress paths — inspect to confirm endpoint + load balancer ARN attribute names."
}

# ingress_paths elements expose only `access_type` and `endpoint` — the Terraform provider does NOT
# surface the load balancer ARN. Inspect the `ingress_paths` output above to pick the PUBLIC entry.
output "endpoint_url" {
  value       = try(aws_ecs_express_gateway_service.app.ingress_paths[0].endpoint, null)
  description = "HTTPS base URL. Feed into webhook_base_url on the phase-2 apply (confirm it is the public ingress path)."
}

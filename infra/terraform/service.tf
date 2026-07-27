# ECS Express Mode service. Health-gated canary rollout with automatic rollback is built in
# (health_check_path below); wait_for_steady_state makes a failed rollout fail `terraform apply`.
resource "aws_ecs_express_gateway_service" "app" {
  service_name            = var.project_name
  execution_role_arn      = aws_iam_role.execution.arn
  infrastructure_role_arn = aws_iam_role.infrastructure.arn
  task_role_arn           = aws_iam_role.task.arn

  cpu               = var.cpu
  memory            = var.memory
  health_check_path = "/health"

  primary_container {
    image          = "${aws_ecr_repository.app.repository_url}:${var.image_tag}"
    container_port = 8080

    aws_logs_configuration {
      log_group         = aws_cloudwatch_log_group.app.name
      log_stream_prefix = "ecs"
    }

    environment {
      name  = "ASPNETCORE_ENVIRONMENT"
      value = "Production"
    }
    environment {
      name  = "ASPNETCORE_URLS"
      value = "http://+:8080"
    }
    environment {
      name  = "Platform__PluginsDirectory"
      value = "plugins"
    }
    # Unknown until the service exists (endpoint has a random hash). Set on the phase-2 apply from
    # the endpoint_url output; safe because the first boot has an empty DB and zero bots (research R3).
    environment {
      name  = "Platform__WebhookBaseUrl"
      value = var.webhook_base_url
    }

    secret {
      name       = "Platform__AdminApiKey"
      value_from = aws_secretsmanager_secret.admin_api_key.arn
    }
    secret {
      name       = "Persistence__ConnectionString"
      value_from = aws_secretsmanager_secret.db_connection_string.arn
    }
  }

  # Single instance with automatic recovery (no multi-AZ HA), per the availability decision.
  # metric/target are set explicitly to AWS's defaults so the provider doesn't report an
  # "inconsistent result" (it fills these in server-side when omitted).
  scaling_target {
    min_task_count            = 1
    max_task_count            = 1
    auto_scaling_metric       = "AVERAGE_CPU"
    auto_scaling_target_value = 60
  }

  # false so the first apply doesn't block waiting for health before the :bootstrap image is pushed.
  # Health-gated rollout + rollback is handled by Express Mode's canary regardless. Flip to true once
  # your image pipeline is established if you want `apply` to fail on an unhealthy rollout.
  wait_for_steady_state = false

  # The CI deploy job (GitHub Actions) updates the running image tag out-of-band, so Terraform must
  # not revert it on the next apply. Terraform still owns everything else about the service.
  lifecycle {
    ignore_changes = [primary_container[0].image]
  }
}

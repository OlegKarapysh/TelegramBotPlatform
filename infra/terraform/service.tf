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

  # Pin the tasks to the default VPC + the app security group so RDS can allow exactly this source
  # (feature 002-rds-postgres, FR-006). network_configuration is a list(object) nested attribute.
  network_configuration = [{
    subnets         = data.aws_subnets.default.ids
    security_groups = [aws_security_group.app.id]
  }]

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
    # Redeploy trigger (feature 002-rds-postgres, FR-008): secrets are read at task launch and not
    # hot-reloaded, and changing the secret's VALUE does not change this service resource. Tying an env
    # var to a hash of the connection string forces a health-gated rollout whenever the string changes
    # (first wiring + every password rotation). The value is a one-way hash, not the secret itself, so
    # it is safe in the task definition. The app ignores this unknown Persistence:* key. Kept LAST in
    # the env list so Terraform shows a clean single-add rather than a positional rename.
    environment {
      name  = "Persistence__ConnectionRevision"
      value = substr(sha256(local.db_connection_string), 0, 16)
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

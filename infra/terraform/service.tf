# Plain ECS Fargate service behind AWS Cloud Map (feature 004-apigateway-ingress).
#
# Replaces `aws_ecs_express_gateway_service`. Express Mode provisions its own Application Load
# Balancer, which cost ~$28/mo (hourly + three public IPv4 addresses) to terminate TLS for a service
# handling ~22 requests/day. API Gateway (apigateway.tf) does that job with no hourly charge; this
# file provides the compute it routes to.
#
# Leaving Express Mode is also what makes ARM64 possible — `aws_ecs_express_gateway_service` has no
# `runtime_platform` attribute, so Graviton pricing was unreachable there.

resource "aws_ecs_cluster" "app" {
  name = var.project_name
}

# --- Service discovery -------------------------------------------------------------------------
#
# API Gateway's private integration resolves the current task through Cloud Map instead of a load
# balancer target group. SRV records (not A) are deliberate: ECS registers IP *and port* in an SRV
# record, and API Gateway honours that port. With A records only the IP is registered and API
# Gateway would fall back to a default port, missing the container's 8080.

resource "aws_service_discovery_private_dns_namespace" "internal" {
  name        = "${var.project_name}.internal"
  vpc         = data.aws_vpc.default.id
  description = "Backs the API Gateway private integration for ${var.project_name}."
}

resource "aws_service_discovery_service" "app" {
  name = var.project_name

  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.internal.id
    routing_policy = "MULTIVALUE"

    dns_records {
      type = "SRV"
      ttl  = 10
    }
  }

  # ECS owns instance health here: it registers a task when it passes the container health check and
  # deregisters it when the task stops. Cloud Map must not run its own probe as well. The block is
  # empty because its only argument, failure_threshold, is deprecated and always 1.
  health_check_custom_config {}

  # Instances are registered by ECS, not Terraform; allow destroy to clean them up.
  force_destroy = true
}

# --- Task definition ---------------------------------------------------------------------------

locals {
  # BotSupervisor registers each bot's webhook as "{WebhookBaseUrl}/{botId}", and the endpoint is
  # mapped at "/telegram-bot/webhook/{botId}" — so the base URL MUST carry that path prefix. The
  # previous value was the bare host, which made Telegram POST to "https://<host>/{botId}" and get a
  # 404; no update ever reached a behavior. var.webhook_base_url stays as an override for a future
  # custom domain, but is empty by default so this stack is self-wiring in a single apply.
  webhook_base_url = coalesce(
    var.webhook_base_url,
    "${aws_apigatewayv2_api.app.api_endpoint}/telegram-bot/webhook",
  )
}

resource "aws_ecs_task_definition" "app" {
  family                   = var.project_name
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = var.cpu_architecture
  }

  container_definitions = jsonencode([
    {
      name      = var.project_name
      image     = "${aws_ecr_repository.app.repository_url}:${var.image_tag}"
      essential = true

      portMappings = [
        {
          containerPort = 8080
          protocol      = "tcp"
        },
      ]

      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "ASPNETCORE_URLS", value = "http://+:8080" },
        # Not the store (feature 003-s3-plugin-storage): packages live in S3, and this is only the
        # local staging directory they are written to so private dependencies resolve alongside them.
        { name = "Platform__PluginsDirectory", value = "plugins" },
        { name = "Platform__PluginsBucket", value = aws_s3_bucket.behaviors.bucket },
        { name = "Platform__PluginsPrefix", value = var.behaviors_prefix },
        # Set explicitly rather than relying on task metadata: a missing region is an obscure startup
        # failure, and this is the first AWS service the application itself calls.
        { name = "AWS_REGION", value = var.region },
        { name = "Platform__WebhookBaseUrl", value = local.webhook_base_url },
        # Redeploy trigger (feature 002-rds-postgres, FR-008): secrets are read at task launch and
        # not hot-reloaded, so a changed secret VALUE would otherwise not roll the service. The value
        # is a one-way hash, not the secret. The app ignores this unknown Persistence:* key.
        { name = "Persistence__ConnectionRevision", value = substr(sha256(local.db_connection_string), 0, 16) },
      ]

      secrets = [
        { name = "Platform__AdminApiKey", valueFrom = aws_secretsmanager_secret.admin_api_key.arn },
        { name = "Persistence__ConnectionString", valueFrom = aws_secretsmanager_secret.db_connection_string.arn },
      ]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.app.name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "ecs"
        }
      }

      # Replaces the ALB's HTTP probe. curl is installed in the runtime image for exactly this.
      # startPeriod is generous because entrypoint.sh applies EF migrations BEFORE the app starts
      # listening — failures during that window must not count as health-check failures.
      healthCheck = {
        command     = ["CMD-SHELL", "curl -fsS http://localhost:8080/health || exit 1"]
        interval    = 30
        timeout     = 5
        retries     = 3
        startPeriod = 120
      }
    },
  ])
}

# --- Service -----------------------------------------------------------------------------------

resource "aws_ecs_service" "app" {
  name            = var.project_name
  cluster         = aws_ecs_cluster.app.arn
  task_definition = aws_ecs_task_definition.app.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  # Tasks need a public IP for EGRESS (Telegram, ECR, Secrets Manager, S3, Logs). The alternative is
  # a NAT gateway at ~$40/mo — an order of magnitude more than the $3.65/mo this address costs.
  # Inbound is not open to the internet: the app security group only admits the VPC link (network.tf).
  network_configuration {
    subnets          = data.aws_subnets.default.ids
    security_groups  = [aws_security_group.app.id]
    assign_public_ip = true
  }

  service_registries {
    registry_arn   = aws_service_discovery_service.app.arn
    container_name = var.project_name
    container_port = 8080
  }

  # Replaces Express Mode's canary: a deployment that cannot reach a steady state is rolled back to
  # the previous task definition automatically.
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # Reaches the admin API when it is not publicly routed (the default). Requires the ssmmessages
  # grants on the task role — see iam.tf.
  enable_execute_command = true

  # false so the first apply does not block waiting for health before a real image is pushed. The
  # circuit breaker still gates and rolls back the rollout either way.
  wait_for_steady_state = false

  # CI registers a new task-definition revision and updates the service out of band, so Terraform
  # must not revert the running revision on the next apply. It still owns everything else.
  lifecycle {
    ignore_changes = [task_definition]
  }
}

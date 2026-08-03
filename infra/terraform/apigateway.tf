# Public HTTPS ingress via API Gateway HTTP API (feature 004-apigateway-ingress).
#
# Why not the Application Load Balancer this replaces: an ALB bills a fixed ~$17.48/mo plus ~$3.65/mo
# for each of the three public IPv4 addresses it holds (one per AZ) — ~$28/mo of fixed cost for a
# service taking ~22 requests/day. An HTTP API has NO hourly charge at all: $1.06 per million
# requests, i.e. effectively $0 at this volume, for the same managed TLS on a stable hostname.
#
# HTTP APIs (not REST APIs) are what make this cheap: their VPC links are free and can target Cloud
# Map directly. A REST API's VPC link requires a Network Load Balancer, which would reintroduce the
# hourly cost this change exists to remove.

resource "aws_apigatewayv2_api" "app" {
  name          = var.project_name
  protocol_type = "HTTP"
  description   = "Public HTTPS ingress for Telegram webhooks."
}

# Private ENIs that let the managed API Gateway service reach tasks inside the default VPC. No
# hourly charge on HTTP APIs.
resource "aws_apigatewayv2_vpc_link" "app" {
  name               = var.project_name
  subnet_ids         = data.aws_subnets.default.ids
  security_group_ids = [aws_security_group.vpc_link.id]
}

# integration_uri is the Cloud Map SERVICE ARN; API Gateway calls DiscoverInstances to find the
# current task. The SRV records registered by ECS carry the container port (see service.tf).
resource "aws_apigatewayv2_integration" "app" {
  api_id             = aws_apigatewayv2_api.app.id
  integration_type   = "HTTP_PROXY"
  integration_method = "ANY"
  integration_uri    = aws_service_discovery_service.app.arn
  connection_type    = "VPC_LINK"
  connection_id      = aws_apigatewayv2_vpc_link.app.id

  # The API Gateway maximum. Telegram webhook handling is far quicker, but a slow behavior should
  # fail on its own terms rather than be cut off early.
  timeout_milliseconds = 29000
}

# --- Routes ------------------------------------------------------------------------------------
#
# Only routes declared here are reachable; anything else gets a 404 from API Gateway and never
# touches the task. The webhook is the sole public surface by default.
#
# The endpoint itself is not unauthenticated: MapBotWebhook validates the
# X-Telegram-Bot-Api-Secret-Token header (HMAC-derived per bot, constant-time compared) before it
# looks at anything else. That, not the WAF this change removes, was always the real control.

resource "aws_apigatewayv2_route" "webhook" {
  api_id    = aws_apigatewayv2_api.app.id
  route_key = "POST /telegram-bot/webhook/{botId}"
  target    = "integrations/${aws_apigatewayv2_integration.app.id}"
}

# Off by default: the operator API is reached with `aws ecs execute-command` instead (the service
# sets enable_execute_command), which keeps it off the public internet entirely.
#
# Turning this on exposes /admin to the whole internet behind the admin key ALONE. WAF cannot
# replace the removed IP allowlist here — it does not support HTTP APIs — so only enable this if the
# admin key is strong and rotated, or put a custom domain plus your own authorizer in front.
resource "aws_apigatewayv2_route" "admin" {
  count     = var.admin_publicly_routable ? 1 : 0
  api_id    = aws_apigatewayv2_api.app.id
  route_key = "ANY /admin/{proxy+}"
  target    = "integrations/${aws_apigatewayv2_integration.app.id}"
}

# $default serves from the API root, so the invoke URL carries no stage segment and the webhook path
# is exactly the one the application maps.
resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.app.id
  name        = "$default"
  auto_deploy = true

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.api.arn
    format = jsonencode({
      requestId          = "$context.requestId"
      httpMethod         = "$context.httpMethod"
      path               = "$context.path"
      status             = "$context.status"
      integrationStatus  = "$context.integrationStatus"
      integrationLatency = "$context.integrationLatency"
      responseLatency    = "$context.responseLatency"
      errorMessage       = "$context.error.message"
    })
  }
}

# Separate from the application log group: this records the ingress hop (did API Gateway reach the
# task at all?), which is the first thing to check when a webhook stops arriving.
resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/apigateway/${var.project_name}"
  retention_in_days = var.log_retention_days
}

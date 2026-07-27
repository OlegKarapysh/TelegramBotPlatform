terraform {
  # >= 1.10 for native S3 state locking (use_lockfile) used in backend.tf.
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source = "hashicorp/aws"
      # ECS Express Mode (aws_ecs_express_gateway_service) requires a recent provider.
      # Pin to the current provider major in your environment; it MUST include Express Mode support.
      version = ">= 5.80"
    }
    # Generates the RDS master password (feature 002-rds-postgres).
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

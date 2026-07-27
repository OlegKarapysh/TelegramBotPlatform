# Networking for the managed PostgreSQL database (feature 002-rds-postgres).
#
# The ECS Express tasks run in the default VPC. RDS lives in that same VPC and is reachable ONLY from
# the app security group (spec FR-005/FR-006; decided in spec.md Clarifications, Q1 = option A). The
# app SG is attached to the Express tasks via service.tf's network_configuration, so it becomes the
# single named source the database security group allows.

data "aws_vpc" "default" {
  default = true
}

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }
}

# Attached to the Express tasks. Its real job is to be the single allowed source for the database; it
# also permits the Express-managed ALB (which lives in this VPC, SG not exposed by Express Mode) to
# reach the container port, and allows the tasks the egress they need (ECR, Secrets Manager, Logs,
# Telegram, RDS).
resource "aws_security_group" "app" {
  name        = "${var.project_name}-app"
  description = "ECS Express tasks: reach RDS, be reached by the managed ALB."
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description = "Container port from within the VPC (managed ALB path)."
    from_port   = 8080
    to_port     = 8080
    protocol    = "tcp"
    cidr_blocks = [data.aws_vpc.default.cidr_block]
  }

  egress {
    description = "All outbound (ECR, Secrets Manager, CloudWatch Logs, Telegram, RDS)."
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = { Name = "${var.project_name}-app" }
}

# Attached to the RDS instance. Allows PostgreSQL (5432) ONLY from the app security group (FR-006).
# No egress rule is defined on purpose — PostgreSQL does not initiate outbound connections.
resource "aws_security_group" "db" {
  name        = "${var.project_name}-db"
  description = "RDS PostgreSQL: inbound 5432 only from the app security group."
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description     = "PostgreSQL from the ECS Express app security group only."
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.app.id]
  }

  tags = { Name = "${var.project_name}-db" }
}

# Amazon RDS for PostgreSQL (feature 002-rds-postgres).
#
# Single-AZ db.t4g.micro, gp3, encrypted at rest, private (not publicly accessible), NO automated
# backups. The database holds only re-creatable data (bot registry + Data Protection key ring), so
# teardown is a clean destroy with no final snapshot (spec.md Clarifications, Q3 / FR-004). The app's
# EF migrations create the `platform` schema on first boot; Terraform provisions the empty DB only.

resource "aws_db_subnet_group" "db" {
  name        = "${var.project_name}-db"
  subnet_ids  = data.aws_subnets.default.ids
  description = "Default-VPC subnets for the platform database."
}

# Master password is generated (never hand-authored) and composed into the connection-string secret
# (secrets.tf). override_special excludes characters that are ambiguous in an Npgsql connection string
# or URL (`;` `@` `/` `'` `"` space) so the composed string is unambiguous. Rotate by tainting this
# resource and re-applying.
resource "random_password" "db" {
  length           = 32
  special          = true
  override_special = "!#%^*()-_=+[]{}"
}

resource "aws_db_instance" "postgres" {
  identifier     = var.project_name
  engine         = "postgres"
  engine_version = var.db_engine_version
  instance_class = var.db_instance_class

  allocated_storage     = var.db_allocated_storage
  max_allocated_storage = var.db_max_allocated_storage
  storage_type          = "gp3"
  storage_encrypted     = true

  db_name  = var.db_name
  username = var.db_username
  password = random_password.db.result

  db_subnet_group_name   = aws_db_subnet_group.db.name
  vpc_security_group_ids = [aws_security_group.db.id]
  publicly_accessible    = false
  multi_az               = false
  port                   = 5432

  # No backups / clean teardown (Q3 / FR-004). Revisit before storing non-re-creatable data:
  # set backup_retention_period = 7, skip_final_snapshot = false, deletion_protection = true.
  backup_retention_period = 0
  skip_final_snapshot     = true
  deletion_protection     = false

  auto_minor_version_upgrade = true
  apply_immediately          = true
}

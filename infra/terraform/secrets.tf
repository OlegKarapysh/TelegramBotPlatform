# Secret VALUES come from sensitive variables (CI secrets / non-committed *.auto.tfvars) and are
# injected into the service via the container `secret` blocks. They are never output.

resource "aws_secretsmanager_secret" "admin_api_key" {
  name        = "${var.project_name}/admin-api-key"
  description = "Platform admin API key."
}

resource "aws_secretsmanager_secret_version" "admin_api_key" {
  secret_id     = aws_secretsmanager_secret.admin_api_key.id
  secret_string = var.admin_api_key
}

# The connection string is COMPOSED from the RDS instance (rds.tf) + generated password and stored in
# this existing secret, replacing the former placeholder var (feature 002-rds-postgres). SSL Mode is
# required because RDS PostgreSQL 15+ sets rds.force_ssl=1 by default.
locals {
  db_connection_string = "Host=${aws_db_instance.postgres.address};Port=5432;Database=${var.db_name};Username=${var.db_username};Password=${random_password.db.result};SSL Mode=Require;Trust Server Certificate=true"
}

resource "aws_secretsmanager_secret" "db_connection_string" {
  name        = "${var.project_name}/db-connection-string"
  description = "PostgreSQL (RDS) connection string, composed from the managed database."
}

resource "aws_secretsmanager_secret_version" "db_connection_string" {
  secret_id     = aws_secretsmanager_secret.db_connection_string.id
  secret_string = local.db_connection_string
}

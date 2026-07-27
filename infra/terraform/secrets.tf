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

resource "aws_secretsmanager_secret" "db_connection_string" {
  name        = "${var.project_name}/db-connection-string"
  description = "External PostgreSQL connection string."
}

resource "aws_secretsmanager_secret_version" "db_connection_string" {
  secret_id     = aws_secretsmanager_secret.db_connection_string.id
  secret_string = var.db_connection_string
}

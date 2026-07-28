# --- Trust policy shared by the task execution role and the task role ---
data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# --- Task execution role: ECS agent pulls the image, writes logs, reads the injected secrets ---
resource "aws_iam_role" "execution" {
  name               = "${var.project_name}-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_assume.json
}

resource "aws_iam_role_policy_attachment" "execution_managed" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

# Least-privilege read of EXACTLY the two secrets this service consumes.
data "aws_iam_policy_document" "execution_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_secretsmanager_secret.admin_api_key.arn,
      aws_secretsmanager_secret.db_connection_string.arn,
    ]
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  name   = "read-service-secrets"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_secrets.json
}

# --- Task role: the identity the APPLICATION itself runs as. Its only AWS permission is the behavior
# extension store (feature 003-s3-plugin-storage); everything else the app talks to is Telegram. ---
resource "aws_iam_role" "task" {
  name               = "${var.project_name}-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_assume.json
}

data "aws_iam_policy_document" "task_behaviors_bucket" {
  # Exactly the four object operations the store performs, scoped to the prefix — nothing else.
  statement {
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.behaviors.arn}/${var.behaviors_prefix}*"]
  }

  # Listing is bucket-level, so it is constrained by an s3:prefix condition instead. NOTE: this makes the
  # app's ListObjectsV2 call REQUIRED to pass the prefix — an unscoped listing is denied.
  statement {
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.behaviors.arn]

    condition {
      test     = "StringLike"
      variable = "s3:prefix"
      values   = ["${var.behaviors_prefix}*"]
    }
  }
}

resource "aws_iam_role_policy" "task_behaviors_bucket" {
  name   = "behavior-extension-store"
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task_behaviors_bucket.json
}

# --- Express Mode infrastructure role: lets ECS manage the ALB/infra on your behalf ---
data "aws_iam_policy_document" "ecs_service_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "infrastructure" {
  name               = "${var.project_name}-express-infra"
  assume_role_policy = data.aws_iam_policy_document.ecs_service_assume.json
}

resource "aws_iam_role_policy_attachment" "infrastructure_managed" {
  role       = aws_iam_role.infrastructure.name
  policy_arn = var.express_infrastructure_role_policy_arn
}

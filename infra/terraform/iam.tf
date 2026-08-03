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

# ECS Exec (`aws ecs execute-command`) is how the operator API is reached now that it is not publicly
# routed — see apigateway.tf. This is a deliberate widening of a role that previously held nothing but
# the S3 grant above, and it is the cost of taking /admin off the internet.
#
# These four actions only open the SSM message channel used to attach to a running container; they
# grant no access to any other AWS resource. Resources cannot be scoped further — the channel is not
# addressable by ARN. If var.admin_publicly_routable is ever set true and ECS Exec is not wanted,
# drop enable_execute_command in service.tf and this policy together.
data "aws_iam_policy_document" "task_exec_channel" {
  statement {
    sid = "EcsExecSsmChannel"
    actions = [
      "ssmmessages:CreateControlChannel",
      "ssmmessages:CreateDataChannel",
      "ssmmessages:OpenControlChannel",
      "ssmmessages:OpenDataChannel",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "task_exec_channel" {
  name   = "ecs-exec-channel"
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task_exec_channel.json
}

# GitHub Actions OIDC + deploy role — short-lived credentials, no long-lived keys (FR-011).

variable "github_owner" {
  type        = string
  description = "GitHub org/user that owns the repository (for the OIDC trust)."
}

variable "github_repo" {
  type        = string
  description = "GitHub repository name (for the OIDC trust)."
}

# GitHub embeds these numeric IDs in the OIDC subject claim (see the sub condition below). Read them
# with: gh api repos/<OWNER>/<REPO> --jq '{owner_id: .owner.id, repo_id: .id}'
variable "github_owner_id" {
  type        = string
  description = "Numeric GitHub owner (user/org) ID — part of the immutable OIDC subject claim."
}

variable "github_repo_id" {
  type        = string
  description = "Numeric GitHub repository ID — part of the immutable OIDC subject claim."
}

variable "deploy_role_name" {
  type        = string
  default     = "githubTelegramBotPlatformOidc"
  description = <<-EOT
    Name of the GitHub Actions deploy role. Deliberately not derived from project_name.

    Changing this forces a replacement. AWS documents IAM names as not distinguished by case, so a
    same-name-different-case role left over outside Terraform will collide on create — delete it
    first.
  EOT
}

variable "create_github_oidc_provider" {
  type        = bool
  default     = true
  description = "Create the GitHub OIDC provider. Set false if it already exists in the account (one per account)."
}

resource "aws_iam_openid_connect_provider" "github" {
  count           = var.create_github_oidc_provider ? 1 : 0
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]
}

data "aws_iam_openid_connect_provider" "github" {
  count = var.create_github_oidc_provider ? 0 : 1
  url   = "https://token.actions.githubusercontent.com"
}

locals {
  github_oidc_provider_arn = var.create_github_oidc_provider ? aws_iam_openid_connect_provider.github[0].arn : data.aws_iam_openid_connect_provider.github[0].arn
}

data "aws_iam_policy_document" "github_deploy_assume" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [local.github_oidc_provider_arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    # Restrict to this repo's main branch only.
    #
    # The subject claim embeds the numeric owner and repo IDs:
    #   repo:OWNER@OWNER_ID/REPO@REPO_ID:ref:refs/heads/main
    # GitHub made that immutable form the default for repositories created (or renamed/transferred)
    # after 2026-07-15, and it cannot be opted out of — the name-only form
    # `repo:OWNER/REPO:ref:...` no longer matches anything this repo presents, and a trust policy
    # written that way fails with a bare "Not authorized to perform sts:AssumeRoleWithWebIdentity".
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_owner}@${var.github_owner_id}/${var.github_repo}@${var.github_repo_id}:ref:refs/heads/main"]
    }
  }
}

resource "aws_iam_role" "github_deploy" {
  name               = var.deploy_role_name
  assume_role_policy = data.aws_iam_policy_document.github_deploy_assume.json
}

data "aws_iam_policy_document" "github_deploy" {
  statement {
    sid       = "EcrAuth"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"] # registry-level action; cannot be scoped to a repo
  }

  statement {
    sid = "EcrPushPull"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:InitiateLayerUpload",
      "ecr:UploadLayerPart",
      "ecr:CompleteLayerUpload",
      "ecr:PutImage",
      "ecr:BatchGetImage",
      "ecr:GetDownloadUrlForLayer",
    ]
    resources = [aws_ecr_repository.app.arn]
  }

  # Registering a revision is account-level: RegisterTaskDefinition takes no resource ARN, and
  # DescribeTaskDefinition addresses a revision that does not exist yet at policy-evaluation time.
  statement {
    sid = "RegisterTaskDefinition"
    actions = [
      "ecs:RegisterTaskDefinition",
      "ecs:DescribeTaskDefinition",
    ]
    resources = ["*"]
  }

  # Rolling the new revision out, scoped to this one service.
  statement {
    sid = "UpdateService"
    actions = [
      "ecs:DescribeServices",
      "ecs:UpdateService",
    ]
    resources = [aws_ecs_service.app.arn]
  }

  statement {
    sid       = "PassTaskRoles"
    actions   = ["iam:PassRole"]
    resources = [aws_iam_role.execution.arn, aws_iam_role.task.arn]
  }
}

resource "aws_iam_role_policy" "github_deploy" {
  name   = "deploy"
  role   = aws_iam_role.github_deploy.id
  policy = data.aws_iam_policy_document.github_deploy.json
}

output "deploy_role_arn" {
  value       = aws_iam_role.github_deploy.arn
  description = "Set as the GitHub repo variable AWS_DEPLOY_ROLE_ARN."
}

# Durable storage for operator-uploaded behavior-extension packages (feature 003-s3-plugin-storage).
# Before this, packages lived on the ECS task's ephemeral disk and were lost on every task replacement.

data "aws_caller_identity" "current" {}

# Bucket names are globally unique across all AWS accounts, so suffix with the account id — the same
# convention the Terraform state bucket uses. Derived rather than a variable so the name cannot drift
# apart from the IAM policy and the container's environment.
resource "aws_s3_bucket" "behaviors" {
  bucket = "${var.project_name}-behaviors-${data.aws_caller_identity.current.account_id}"

  # The store holds only re-creatable content — every package can be re-uploaded from its source build —
  # so teardown is a clean destroy rather than a manual empty-then-delete (feature 003, FR-011).
  force_destroy = true
}

resource "aws_s3_bucket_public_access_block" "behaviors" {
  bucket = aws_s3_bucket.behaviors.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# ACLs off entirely: the bucket owner is the only grantor, so access is decided by IAM alone.
resource "aws_s3_bucket_ownership_controls" "behaviors" {
  bucket = aws_s3_bucket.behaviors.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

# SSE-S3 rather than a customer-managed KMS key: the content is operator-supplied plugin code, not user
# data, so this is proportionate and costs nothing (no KMS grants needed on the task role).
resource "aws_s3_bucket_server_side_encryption_configuration" "behaviors" {
  bucket = aws_s3_bucket.behaviors.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# Explicitly disabled, not merely omitted. Only the current build of a package is addressable (retaining
# prior builds is out of scope), and non-current versions would defeat the clean force_destroy above.
resource "aws_s3_bucket_versioning" "behaviors" {
  bucket = aws_s3_bucket.behaviors.id

  versioning_configuration {
    status = "Disabled"
  }
}

data "aws_iam_policy_document" "behaviors_tls_only" {
  statement {
    sid       = "DenyInsecureTransport"
    effect    = "Deny"
    actions   = ["s3:*"]
    resources = [aws_s3_bucket.behaviors.arn, "${aws_s3_bucket.behaviors.arn}/*"]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "behaviors" {
  bucket = aws_s3_bucket.behaviors.id
  policy = data.aws_iam_policy_document.behaviors_tls_only.json

  # The public access block must land first, or applying a bucket policy can race it and fail.
  depends_on = [aws_s3_bucket_public_access_block.behaviors]
}

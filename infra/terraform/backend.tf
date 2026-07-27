# Remote state in S3 with native S3 state locking (use_lockfile — requires Terraform >= 1.10).
# The bucket is created once by infra/terraform/bootstrap/ BEFORE this backend is initialized.
#
# Backend blocks cannot use variables. Either edit the placeholders below, or (preferred) pass them
# at init time:
#   terraform init \
#     -backend-config="bucket=<state bucket from bootstrap output>" \
#     -backend-config="region=<region>"
terraform {
  backend "s3" {
    bucket       = "telegrambotplatform-tfstate-089496391422"
    key          = "telegrambotplatform/aws-deployment.tfstate"
    region       = "eu-north-1"
    encrypt      = true
    use_lockfile = true
  }
}

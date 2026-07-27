# AWS Deployment (Terraform + GitHub Actions)

Deploys the TelegramBotPlatform WebApi to **Amazon ECS Express Mode**. See the full design in
[specs/001-aws-deployment/](../../specs/001-aws-deployment/) (spec, plan, research, contracts,
quickstart). This README is the operator runbook.

## Prerequisites

- Terraform ≥ 1.10, AWS CLI, Docker, .NET 10 SDK.
- An AWS account + credentials for the initial applies.
- **An external PostgreSQL reachable from the ECS tasks** — this feature does **not** create the
  database (deferred to a follow-up RDS spec). Have its connection string ready.
- Confirm **ECS Express Mode is available** in your target region.
- Your operator public IP/CIDR for the `/admin/*` allowlist.

## 1. Bootstrap remote state (once)

```bash
terraform -chdir=infra/terraform/bootstrap init
terraform -chdir=infra/terraform/bootstrap apply \
  -var 'region=<REGION>' -var 'state_bucket_name=<GLOBALLY_UNIQUE_NAME>'
```

Put the resulting bucket + region into `backend.tf` (or pass them via `terraform init -backend-config=...`).

## 2. Provision (phase 1 — create)

```bash
terraform -chdir=infra/terraform init
terraform -chdir=infra/terraform apply \
  -var 'region=<REGION>' \
  -var 'admin_allowlist_cidrs=["<YOUR_IP>/32"]' \
  -var 'admin_api_key=<STRONG_KEY>' \
  -var 'db_connection_string=<NPGSQL_CONN_STRING>' \
  -var 'github_owner=<OWNER>' -var 'github_repo=<REPO>' \
  -var 'image_tag=bootstrap'
```

Publish the first image, then note the outputs (`endpoint_url`, `ecr_repository_url`, `service_arn`,
`deploy_role_arn`):

```bash
ECR_REPOSITORY_URL=$(terraform -chdir=infra/terraform output -raw ecr_repository_url) \
AWS_REGION=<REGION> IMAGE_TAG=bootstrap ./infra/scripts/push-image.sh
```

## 3. Set the webhook base URL (phase 2)

```bash
terraform -chdir=infra/terraform apply \
  -var 'webhook_base_url=<endpoint_url from step 2>' <same vars as above>
```

The endpoint has a random hash so it is unknown until the service exists; setting it in a second
apply is safe because the first boot has an empty DB and zero bots (see research R3).

## 4. Enable continuous deploy

Set these **GitHub repository variables** from the Terraform outputs, then push to `main`:

| Variable | From output |
|----------|-------------|
| `AWS_DEPLOY_ROLE_ARN` | `deploy_role_arn` |
| `AWS_REGION` | your region |
| `ECR_REPOSITORY_URL` | `ecr_repository_url` |
| `EXPRESS_SERVICE_ARN` | `service_arn` |

## 5. Teardown

```bash
terraform -chdir=infra/terraform destroy
# optionally destroy the bootstrap state bucket afterward
```

## Accepted limitations (per the 2026-07-26 clarifications)

- **Uploaded plugins are ephemeral.** Express Mode has no persistent storage, so operator-uploaded
  plugin DLLs are lost on redeploy/replacement and must be re-uploaded. **Built-in behaviors are
  unaffected.** Durable plugin storage is deferred to the follow-up work.
- **Brief downtime** during restart/rollout is acceptable — Telegram retries webhook deliveries.
- **The database is external.** Provisioning managed PostgreSQL (RDS) is a separate feature.

## `TODO(verify)` items in the Terraform

The `aws_ecs_express_gateway_service` resource is new; a few specifics must be confirmed against your
provider version on first apply (all flagged with `TODO(verify)` in the code):

- The nested attribute names of `ingress_paths` for the **endpoint URL** and **load balancer ARN**
  (`outputs.tf`, `waf.tf`) — inspect via `terraform state show aws_ecs_express_gateway_service.app`.
- The AWS-managed policy ARN for the **Express infrastructure role** (`variables.tf`).
- The exact IAM action / AWS CLI verb to **update an Express service image** (`iam_github.tf`,
  `.github/workflows/deploy.yml`).

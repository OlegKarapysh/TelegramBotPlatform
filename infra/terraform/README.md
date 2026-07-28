# AWS Deployment (Terraform + GitHub Actions)

Deploys the TelegramBotPlatform WebApi to **Amazon ECS Express Mode**. See the full design in
[specs/001-aws-deployment/](../../specs/001-aws-deployment/) (spec, plan, research, contracts,
quickstart). This README is the operator runbook.

## Prerequisites

- Terraform ≥ 1.10, AWS CLI, Docker, .NET 10 SDK.
- An AWS account + credentials for the initial applies.
- The platform's **PostgreSQL database is provisioned by this Terraform** (Amazon RDS — feature
  [002-rds-postgres](../../specs/002-rds-postgres/)); no external database is needed. It is private
  (reachable only from the ECS tasks), encrypted at rest, and has **no automated backups** by design.
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

The RDS instance is destroyed **cleanly** — no final snapshot, no deletion-protection block
(`skip_final_snapshot = true`, `deletion_protection = false`). Because backups are disabled, **destroy
is irreversible data loss**; the data is re-creatable (bots re-registered, Data Protection keys
regenerated), which is the accepted trade-off (feature 002 spec, Q3).

## Database (Amazon RDS) — feature 002-rds-postgres

- **What Terraform manages**: a single-AZ `db.t4g.micro` PostgreSQL instance (`rds.tf`), a subnet group
  over the default VPC, two security groups (`network.tf` — the app SG on the ECS tasks and a DB SG
  allowing 5432 **only** from the app SG), and a generated master password composed into the existing
  `telegrambotplatform/db-connection-string` secret (`secrets.tf`). Sizing is tunable via the
  `db_instance_class` / `db_engine_version` / `db_*_storage` / `db_name` / `db_username` variables.
- **Applying the secret**: the connection string is read at task launch, so `service.tf` carries a
  `Persistence__ConnectionRevision` env (a hash of the connection string) that forces a health-gated
  rollout whenever the string changes — no manual redeploy step needed.
- **Rotate the credential** (no app code change):

  ```bash
  terraform -chdir=infra/terraform taint random_password.db
  terraform -chdir=infra/terraform apply <same vars as step 2>
  # the recomposed secret + changed revision env trigger a rollout; verify /health = 200 afterward
  ```

- **No backups (accepted limitation)**: automated backups / point-in-time recovery are off. Before the
  platform stores non-re-creatable data, set `backup_retention_period = 7`, `skip_final_snapshot =
  false`, and `deletion_protection = true` in `rds.tf`.

## Behavior extension store (Amazon S3) — feature 003-s3-plugin-storage

- **What Terraform manages** (`s3.tf`): a private bucket
  `telegrambotplatform-behaviors-<account-id>` holding operator-uploaded behavior-extension packages,
  with all four public-access blocks on, ACLs disabled (`BucketOwnerEnforced`), SSE-S3 encryption,
  versioning **disabled**, and a bucket policy denying any non-TLS request. Access is granted by an
  inline policy on the **task** role (`iam.tf`) — the first AWS permission the application itself uses.
- **Why**: before this, uploaded extensions lived on the ECS task's ephemeral disk, so every deployment,
  restart, or recovery silently lost them and any bot assigned to one went dark.
- **Least privilege**: `GetObject`/`PutObject`/`DeleteObject` are scoped to `${behaviors_prefix}*`, and
  `ListBucket` is constrained by an `s3:prefix` condition. That condition makes passing the prefix
  **mandatory** in the application's list call — an unscoped listing is denied.
- **Container wiring** (`service.tf`): `Platform__PluginsBucket`, `Platform__PluginsPrefix`, and an
  explicit `AWS_REGION`. `Platform__PluginsDirectory` remains, but now only names the local *staging*
  directory a downloaded package is written to so its private dependencies resolve alongside it.
- **Startup is fail-fast**: if the bucket cannot be read within ~30s the app exits without binding a
  port, so the health-gated canary rolls the release back. Note `wait_for_steady_state = false`, so
  `terraform apply` still succeeds — the rollback, not the apply exit code, is the signal.
- **Unset the bucket to opt out**: with `Platform__PluginsBucket` empty the app falls back to the local
  directory (today's pre-003 behavior). That is also what makes local development and the test suite work
  with no AWS credentials.
- **Teardown**: `force_destroy = true`, so `terraform destroy` removes the bucket even with packages in
  it — no manual emptying. Packages are re-uploadable from their source builds, so this is not data loss.

## Accepted limitations (per the 2026-07-26 clarifications)

- **Uploaded plugins are ephemeral.** Express Mode has no persistent storage, so operator-uploaded
  plugin DLLs are lost on redeploy/replacement and must be re-uploaded. **Built-in behaviors are
  unaffected.** Durable plugin storage is deferred to the follow-up work.
- **Brief downtime** during restart/rollout is acceptable — Telegram retries webhook deliveries.
- **The database has no backups and is single-AZ** (feature 002-rds-postgres, accepted trade-off) —
  data is re-creatable; multi-AZ HA and backups are future work.

## `TODO(verify)` items in the Terraform

The `aws_ecs_express_gateway_service` resource is new; a few specifics must be confirmed against your
provider version on first apply (all flagged with `TODO(verify)` in the code):

- The nested attribute names of `ingress_paths` for the **endpoint URL** and **load balancer ARN**
  (`outputs.tf`, `waf.tf`) — inspect via `terraform state show aws_ecs_express_gateway_service.app`.
- The AWS-managed policy ARN for the **Express infrastructure role** (`variables.tf`).
- The exact IAM action / AWS CLI verb to **update an Express service image** (`iam_github.tf`,
  `.github/workflows/deploy.yml`).

# AWS Deployment (Terraform + GitHub Actions)

Deploys the TelegramBotPlatform WebApi to **AWS Fargate behind an API Gateway HTTP API**. See the
original design in [specs/001-aws-deployment/](../../specs/001-aws-deployment/) (spec, plan, research,
contracts, quickstart) — note that the ingress described there is ECS Express Mode, which this stack
has since replaced (see [Ingress](#ingress-api-gateway--cloud-map) below). This README is the operator
runbook.

## Prerequisites

- Terraform ≥ 1.10, AWS CLI, Docker, .NET 10 SDK.
- An AWS account + credentials for the initial applies.
- The platform's **PostgreSQL database is provisioned by this Terraform** (Amazon RDS — feature
  [002-rds-postgres](../../specs/002-rds-postgres/)); no external database is needed. It is private
  (reachable only from the ECS tasks), encrypted at rest, and has **no automated backups** by design.
- A way to build **linux/arm64** images (CI uses an ARM GitHub runner). Set
  `cpu_architecture = "X86_64"` if you cannot; it costs ~$4/mo of the saving.

## 1. Bootstrap remote state (once)

```bash
terraform -chdir=infra/terraform/bootstrap init
```

```bash
terraform -chdir=infra/terraform/bootstrap apply -var 'region=<REGION>' -var 'state_bucket_name=<GLOBALLY_UNIQUE_NAME>'
```

Put the resulting bucket + region into `backend.tf` (or pass them via `terraform init -backend-config=...`).

## 2. Provision (one apply)

```bash
terraform -chdir=infra/terraform init
```

```bash
terraform -chdir=infra/terraform apply -var 'region=<REGION>' -var 'admin_api_key=<STRONG_KEY>' -var 'github_owner=<OWNER>' -var 'github_repo=<REPO>' -var 'github_owner_id=<OWNER_ID>' -var 'github_repo_id=<REPO_ID>' -var 'image_tag=bootstrap'
```

`github_owner_id` / `github_repo_id` are the **numeric** IDs, not the names. GitHub puts both in the
OIDC subject claim (`repo:OWNER@OWNER_ID/REPO@REPO_ID:ref:refs/heads/main`) for every repository
created or renamed after 2026-07-15, and the form cannot be opted out of — a trust policy written
with names alone is rejected with a bare `Not authorized to perform sts:AssumeRoleWithWebIdentity`.
Read them with:

```bash
gh api repos/<OWNER>/<REPO> --jq '{owner_id: .owner.id, repo_id: .id}'
```

**A single apply is enough.** Terraform derives `Platform__WebhookBaseUrl` from the API Gateway
endpoint it creates, so the two-phase apply Express Mode needed (endpoint first, then feed it back in)
is gone. `webhook_base_url` remains as an override for a custom domain only.

Then publish the first image:

```bash
ECR_REPOSITORY_URL=$(terraform -chdir=infra/terraform output -raw ecr_repository_url) AWS_REGION=<REGION> IMAGE_TAG=bootstrap ./infra/scripts/push-image.sh
```

## 3. Enable continuous deploy

Set these **GitHub repository variables** from the Terraform outputs, then push to `main`:

| Variable | From output |
|----------|-------------|
| `AWS_DEPLOY_ROLE_ARN` | `deploy_role_arn` |
| `AWS_REGION` | your region |
| `ECR_REPOSITORY_URL` | `ecr_repository_url` |
| `ECS_CLUSTER` | `cluster_name` |
| `ECS_SERVICE` | `service_name` |
| `ECS_TASK_FAMILY` | `task_definition_family` |

> `EXPRESS_SERVICE_ARN` is obsolete — delete it. The deploy job now registers a new task-definition
> revision (copying the current one and swapping only the image) and waits for the service to
> stabilise.

## 4. Teardown

```bash
terraform -chdir=infra/terraform destroy
```

The RDS instance is destroyed **cleanly** — no final snapshot, no deletion-protection block
(`skip_final_snapshot = true`, `deletion_protection = false`). Because backups are disabled, **destroy
is irreversible data loss**; the data is re-creatable (bots re-registered, Data Protection keys
regenerated), which is the accepted trade-off (feature 002 spec, Q3).

## Ingress (API Gateway + Cloud Map)

**Why this replaced ECS Express Mode.** Express Mode provisions an Application Load Balancer, which
bills ~$17.48/mo plus ~$3.65/mo for each of the three public IPv4 addresses it holds — ~$28/mo of
fixed cost, 39% of the total bill, to terminate TLS for a service taking ~22 requests/day. An API
Gateway **HTTP API** has no hourly charge at all ($1.06 per million requests), and its VPC links are
free and can target Cloud Map directly. Leaving Express Mode also unlocked ARM64: the
`aws_ecs_express_gateway_service` resource has no `runtime_platform` attribute, so Graviton pricing
was unreachable there. Measured effect: **$72.62/mo → ~$34/mo.**

```
Telegram → API Gateway HTTP API → VPC Link → Cloud Map → Fargate task (ARM64) → RDS / S3
```

- **Only declared routes are reachable.** `POST /telegram-bot/webhook/{botId}` is the sole public
  surface by default; anything else gets a 404 from API Gateway and never touches the task.
- **Cloud Map uses SRV records, not A records** (`service.tf`). ECS registers IP *and port* in an SRV
  record and API Gateway honours that port; with A records only the IP is registered and API Gateway
  would miss the container's 8080.
- **The webhook base URL must carry the path prefix.** `BotSupervisor` registers
  `"{WebhookBaseUrl}/{botId}"` against an endpoint mapped at `/telegram-bot/webhook/{botId}`, so the
  base URL has to end in `/telegram-bot/webhook`. Terraform composes this correctly; if you override
  `webhook_base_url` for a custom domain, include the prefix or **every Telegram delivery will 404**.
- **Access logs** go to `/aws/apigateway/<project>`, separate from the application log group. That is
  the first place to look when a webhook stops arriving — it tells you whether API Gateway reached the
  task at all.

### Reaching the admin API

`/admin/*` is **not publicly routed** by default. Reach it from inside the running task:

```bash
aws ecs execute-command --cluster telegrambotplatform --task <TASK_ID> --container telegrambotplatform --interactive --command "/bin/sh"
```

Then `curl -H "X-Admin-Api-Key: <KEY>" http://localhost:8080/admin/bots`.

To expose it publicly instead, set `admin_publicly_routable = true`. Be deliberate: that leaves the
admin key as the **only** control. The WAF IP allowlist that used to front `/admin` could not be
carried over, because **WAF does not support API Gateway HTTP APIs**.

## Database (Amazon RDS) — feature 002-rds-postgres

- **What Terraform manages**: a single-AZ `db.t4g.micro` PostgreSQL instance (`rds.tf`), a subnet group
  over the default VPC, security groups (`network.tf` — the app SG on the ECS tasks, a DB SG allowing
  5432 **only** from the app SG, and a VPC-link SG), and a generated master password composed into the
  existing `telegrambotplatform/db-connection-string` secret (`secrets.tf`). Sizing is tunable via the
  `db_instance_class` / `db_engine_version` / `db_*_storage` / `db_name` / `db_username` variables.
- **Applying the secret**: the connection string is read at task launch, so `service.tf` carries a
  `Persistence__ConnectionRevision` env (a hash of the connection string) that forces a health-gated
  rollout whenever the string changes — no manual redeploy step needed.
- **Rotate the credential** (no app code change):

  ```bash
  terraform -chdir=infra/terraform taint random_password.db
  ```

  Then re-apply with the same vars as step 2; the recomposed secret + changed revision env trigger a
  rollout. Verify `/health` = 200 afterward.

- **No backups (accepted limitation)**: automated backups / point-in-time recovery are off. Before the
  platform stores non-re-creatable data, set `backup_retention_period = 7`, `skip_final_snapshot =
  false`, and `deletion_protection = true` in `rds.tf`.

## Behavior extension store (Amazon S3) — feature 003-s3-plugin-storage

- **What Terraform manages** (`s3.tf`): a private bucket
  `telegrambotplatform-behaviors-<account-id>` holding operator-uploaded behavior-extension packages,
  with all four public-access blocks on, ACLs disabled (`BucketOwnerEnforced`), SSE-S3 encryption,
  versioning **disabled**, and a bucket policy denying any non-TLS request. Access is granted by an
  inline policy on the **task** role (`iam.tf`).
- **Why**: before this, uploaded extensions lived on the task's ephemeral disk, so every deployment,
  restart, or recovery silently lost them and any bot assigned to one went dark.
- **Least privilege**: `GetObject`/`PutObject`/`DeleteObject` are scoped to `${behaviors_prefix}*`, and
  `ListBucket` is constrained by an `s3:prefix` condition. That condition makes passing the prefix
  **mandatory** in the application's list call — an unscoped listing is denied.
- **Container wiring** (`service.tf`): `Platform__PluginsBucket`, `Platform__PluginsPrefix`, and an
  explicit `AWS_REGION`. `Platform__PluginsDirectory` remains, but now only names the local *staging*
  directory a downloaded package is written to so its private dependencies resolve alongside it.
- **Startup is fail-fast**: if the bucket cannot be read within ~30s the app exits without binding a
  port, so the deployment circuit breaker rolls the release back. Note `wait_for_steady_state = false`,
  so `terraform apply` still succeeds — the rollback, not the apply exit code, is the signal.
- **Unset the bucket to opt out**: with `Platform__PluginsBucket` empty the app falls back to the local
  directory. That is also what makes local development and the test suite work with no AWS credentials.
- **Teardown**: `force_destroy = true`, so `terraform destroy` removes the bucket even with packages in
  it — no manual emptying. Packages are re-uploadable from their source builds, so this is not data loss.

## Accepted limitations

- **Single task, no multi-AZ HA** (`desired_count = 1`). The task is the single point of failure; the
  ingress is not. Rollouts briefly run two tasks, which is harmless — update handling is stateless per
  request and `SetWebhook` is idempotent.
- **Brief downtime** during restart/rollout is acceptable — Telegram retries webhook deliveries.
- **The database has no backups and is single-AZ** (feature 002-rds-postgres, accepted trade-off) —
  data is re-creatable; multi-AZ HA and backups are future work.
- **The admin API has no network-level restriction** when `admin_publicly_routable = true`, because
  WAF cannot front an HTTP API. Default (`false`) avoids this by not routing it at all.

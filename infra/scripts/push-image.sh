#!/usr/bin/env bash
set -euo pipefail

# Build the WebApi image and push it to ECR. The Docker build context is the REPO ROOT (the
# Dockerfile copies the full source before restore). Run from the repo root.
#
#   ECR_REPOSITORY_URL=<acct>.dkr.ecr.<region>.amazonaws.com/telegrambotplatform \
#   AWS_REGION=<region> IMAGE_TAG=<tag> ./infra/scripts/push-image.sh
#
# IMAGE_TAG defaults to the short git SHA.

: "${ECR_REPOSITORY_URL:?set ECR_REPOSITORY_URL (from the terraform output)}"
: "${AWS_REGION:?set AWS_REGION}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD)}"

registry="${ECR_REPOSITORY_URL%%/*}"

aws ecr get-login-password --region "$AWS_REGION" \
  | docker login --username AWS --password-stdin "$registry"

docker build -f src/TelegramBotPlatform.WebApi/Dockerfile -t "${ECR_REPOSITORY_URL}:${IMAGE_TAG}" .
docker push "${ECR_REPOSITORY_URL}:${IMAGE_TAG}"

echo "Pushed ${ECR_REPOSITORY_URL}:${IMAGE_TAG}"

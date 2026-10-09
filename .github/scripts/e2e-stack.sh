#!/usr/bin/env bash
# Starts what the end-to-end tests of the web interface need (ADR-040): PostgreSQL and the S3 store in Docker, then the published API and workers on the host, with the SUNAT
# simulator (Sunat__Environment=Sandbox, ADR-039). Run from the repository root after `dotnet publish` of both hosts into run/api and run/workers and with a .env file.
# It leaves the API on http://localhost:5180 and writes the platform administrator for the tests to $GITHUB_ENV (SF_E2E_ADMIN_EMAIL, SF_E2E_ADMIN_PASSWORD).
set -euo pipefail

set -a
# shellcheck disable=SC1091
. ./.env
set +a

docker compose up -d --wait postgres s3
docker compose up --no-deps s3-init

export ConnectionStrings__Migrations="Host=localhost;Port=${SF_POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
export ConnectionStrings__App="Host=localhost;Port=${SF_POSTGRES_PORT};Database=${POSTGRES_DB};Username=${SF_APP_DB_USER};Password=${SF_APP_DB_PASSWORD}"
export Identity__SigningKey="${SF_JWT_SIGNING_KEY}"
export Security__LocalDevKek="${SF_LOCAL_DEV_KEK}"
export Storage__S3__ServiceUrl="http://localhost:${SF_S3_PORT}"
export Storage__S3__Bucket="${SF_STORAGE_BUCKET}"
export Storage__S3__AccessKey="${S3_ACCESS_KEY}"
export Storage__S3__SecretKey="${S3_SECRET_KEY}"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:5180
export DOTNET_ENVIRONMENT=Development
export Sunat__Environment=Sandbox
# The simulator of the DNS of the domains of the resellers (ADR-051): every check passes. Never in production.
export Domains__Dns__Provider=Sandbox
# The e-mail goes to files that the tests read (ADR-052): the link of a password reset arrives there. Never in production.
MAIL_DIR="${RUNNER_TEMP:-/tmp}/sf-mail"
mkdir -p "${MAIL_DIR}"
export Email__Provider=Sandbox
export Email__From=no-responder@securefact.test
export Email__Sandbox__Directory="${MAIL_DIR}"
export Web__PublicUrl=http://localhost:5173
# The webhooks of the tests point at a server on this machine (ADR-067); the API refuses this in production.
export Webhooks__AllowLocalTargets=true
# The tests sign in many times from one address; the limit of the sign-in endpoint is raised for them only.
export RateLimiting__AuthPermitPerMinute=1000
# The workers look for work every 3 seconds instead of every 15.
export Cpe__WorkerIntervalSeconds=3

dotnet run/api/SecureFact.Api.dll migrate

ADMIN_EMAIL="e2e-admin@securefact.test"
ADMIN_PASSWORD="E2e-$(openssl rand -hex 12)-Aa1!"
echo "::add-mask::${ADMIN_PASSWORD}"
SF_BOOTSTRAP_ADMIN_EMAIL="${ADMIN_EMAIL}" SF_BOOTSTRAP_ADMIN_PASSWORD="${ADMIN_PASSWORD}" dotnet run/api/SecureFact.Api.dll bootstrap-platform-admin
{
  echo "SF_E2E_ADMIN_EMAIL=${ADMIN_EMAIL}"
  echo "SF_E2E_ADMIN_PASSWORD=${ADMIN_PASSWORD}"
  echo "SF_E2E_MAIL_DIR=${MAIL_DIR}"
} >> "${GITHUB_ENV:?GITHUB_ENV is not set}"

mkdir -p logs
(cd run/api && nohup dotnet SecureFact.Api.dll > ../../logs/api.log 2>&1 &)
(cd run/workers && nohup dotnet SecureFact.Workers.dll > ../../logs/workers.log 2>&1 &)

for _ in $(seq 1 60); do
  if curl -fsS http://localhost:5180/health/ready > /dev/null 2>&1; then
    echo "API ready"
    exit 0
  fi
  sleep 1
done
echo "The API did not become ready" >&2
tail -50 logs/api.log >&2 || true
exit 1

#!/usr/bin/env bash
#
# Post-apply step: creates a contained database user for an app's managed
# identity and grants it db_datareader/db_datawriter.
#
# infra/terraform/modules/sql provisions the Azure SQL server and database
# with Entra-ID-only authentication -- there is no SQL login, and Terraform
# cannot run CREATE USER ... FROM EXTERNAL PROVIDER because that statement
# requires an authenticated connection to the database itself, not a call
# to the ARM API. It must run as an Entra ID administrator on the server
# (see the server's azuread_administrator block / entra_admin_object_id).
#
# Run this once per app identity per environment, after `terraform apply`
# and before the app's first deployment. Re-running it is safe: the
# underlying create-app-user.sql skips the CREATE USER and role grants if
# the user already exists / is already a member.
#
# Requires sqlcmd (mssql-tools18, go-sqlcmd) and an active `az login` session
# authenticated as a member of the server's Entra ID administrator group.
#
# Usage:
#   ./scripts/create-app-user.sh <sql-server-fqdn> <database-name> <app-name>
#
# Example:
#   ./scripts/create-app-user.sh \
#     sql-desicon-fw-dev.database.windows.net \
#     DesiconFinanceWorkflow \
#     app-desicon-fw-api-dev

set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "Usage: $0 <sql-server-fqdn> <database-name> <app-name>" >&2
  exit 1
fi

SQL_SERVER="$1"
DATABASE_NAME="$2"
APP_NAME="$3"

if ! command -v sqlcmd >/dev/null 2>&1; then
  echo "ERROR: sqlcmd is required but was not found on PATH. Install mssql-tools18." >&2
  exit 1
fi

az account show >/dev/null 2>&1 || {
  echo "ERROR: not logged in. Run 'az login' as a member of the SQL server's Entra ID administrator group first." >&2
  exit 1
}

echo "Connecting as: $(az account show --query user.name -o tsv)"
echo "Granting [${APP_NAME}] access to ${DATABASE_NAME} on ${SQL_SERVER}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

sqlcmd \
  -S "${SQL_SERVER}" \
  -d "${DATABASE_NAME}" \
  -G \
  --authentication-method=ActiveDirectoryDefault \
  -i "${SCRIPT_DIR}/create-app-user.sql" \
  -v AppName="${APP_NAME}" DatabaseName="${DATABASE_NAME}"

echo "Done."

#!/usr/bin/env bash
# Creates the solution file and wires up project references.
# Run once after cloning. Safe to re-run.
set -euo pipefail

SLN="Desicon.FinanceWorkflow.sln"

if [ ! -f "$SLN" ]; then
  echo "Creating $SLN"
  dotnet new sln -n Desicon.FinanceWorkflow
fi

add() {
  if [ -f "$1" ]; then
    dotnet sln "$SLN" add "$1" >/dev/null 2>&1 || true
    echo "  + $1"
  fi
}

echo "Adding projects:"
add src/Desicon.Workflow.Core/Desicon.Workflow.Core.csproj
add src/Desicon.Workflow.Domain/Desicon.Workflow.Domain.csproj
add src/Desicon.Workflow.Infrastructure/Desicon.Workflow.Infrastructure.csproj
add src/Desicon.Workflow.Api/Desicon.Workflow.Api.csproj
add src/Desicon.Workflow.Functions/Desicon.Workflow.Functions.csproj
add tests/Desicon.Workflow.UnitTests/Desicon.Workflow.UnitTests.csproj
add tests/Desicon.Workflow.IntegrationTests/Desicon.Workflow.IntegrationTests.csproj

echo ""
echo "Restoring..."
dotnet restore

echo ""
echo "Validating workflow definitions..."
if command -v node >/dev/null 2>&1; then
  node tools/validate-definitions.mjs modules
else
  echo "  (node not found — skipping; the C# validator runs in CI regardless)"
fi

echo ""
echo "Next: dotnet build && dotnet test"

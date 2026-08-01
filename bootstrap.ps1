#Requires -Version 7
# Windows equivalent of bootstrap.sh
$ErrorActionPreference = "Stop"

$sln = "Desicon.FinanceWorkflow.sln"

if (-not (Test-Path $sln)) {
    Write-Host "Creating $sln"
    dotnet new sln -n Desicon.FinanceWorkflow
}

$projects = @(
    "src/Desicon.Workflow.Core/Desicon.Workflow.Core.csproj",
    "src/Desicon.Workflow.Domain/Desicon.Workflow.Domain.csproj",
    "src/Desicon.Workflow.Infrastructure/Desicon.Workflow.Infrastructure.csproj",
    "src/Desicon.Workflow.Api/Desicon.Workflow.Api.csproj",
    "src/Desicon.Workflow.Functions/Desicon.Workflow.Functions.csproj",
    "tests/Desicon.Workflow.UnitTests/Desicon.Workflow.UnitTests.csproj",
    "tests/Desicon.Workflow.IntegrationTests/Desicon.Workflow.IntegrationTests.csproj"
)

Write-Host "Adding projects:"
foreach ($p in $projects) {
    if (Test-Path $p) {
        dotnet sln $sln add $p 2>$null | Out-Null
        Write-Host "  + $p"
    }
}

Write-Host "`nRestoring..."
dotnet restore

Write-Host "`nValidating workflow definitions..."
if (Get-Command node -ErrorAction SilentlyContinue) {
    node tools/validate-definitions.mjs modules
}

Write-Host "`nNext: dotnet build; dotnet test"

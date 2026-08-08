<#
.SYNOPSIS
    Defines the Entra app roles the API authorises on, and assigns them to
    people. Idempotent.

.DESCRIPTION
    docs/04-Security-and-DevSecOps.md carries a seven-row RBAC table. Nothing
    in this repository has ever created a single one of those roles: no
    Terraform (app registrations are directory objects, not Azure resources),
    no bootstrap script, no manual step recorded anywhere. The table describes
    a model that does not exist in the directory.

    CurrentUserAccessor.GetRoles() reads the token's "roles" claim, so an
    unassigned user simply presents an empty set, every role-resolved
    transition finds no actor, and the request stops with nothing to explain
    why. That is the state dev is in.

    WHAT THIS CREATES, AND WHAT IT DELIBERATELY DOES NOT
    ---------------------------------------------------
    Two roles: FinanceOfficer and FinanceManager. Those are the only two the
    code actually reads -- they are the `"actor": { "role": ... }` specs in
    modules/*.workflow.json, and the only values RequestActionService can act
    on.

    The other five rows in the table (Employee, LineManager, DepartmentHead,
    ProcurementOfficer, Administrator) are NOT created here, and that is
    deliberate. LineManager and DepartmentHead authority comes from the org
    chart -- Employees.LineManagerId and Departments.HeadEmployeeId, resolved
    by EmployeeActorResolver -- not from a claim; creating claims that grant
    nothing would make the directory look like it enforces a model it does
    not. ProcurementOfficer and Administrator have no code behind them at all
    yet. This script prints them as an outstanding gap rather than silently
    manufacturing them.

    TRAPS
    -----
    * appRoles is replaced wholesale by PATCH, not merged. Existing roles are
      read and preserved here; a naive PATCH would delete every role not in
      the body, and Entra would report success.

    * Role ids must be stable. They are hardcoded constants below rather than
      generated, because an appRoleAssignment references the role by id -- a
      regenerated id silently orphans every assignment already made.

    * A role cannot be deleted while isEnabled is true. Removing one is a
      two-step PATCH (disable, then remove). This script only adds, so it
      never hits that, but anything editing these later will.

    * Assignments do not appear in a token that was already issued. Whoever
      you assign must sign out and back in, or they will keep presenting the
      old claim set and you will conclude, wrongly, that this did not work.

.PARAMETER ApiClientId
    Application (client) id of the API registration -- entra_client_id in
    dev.auto.tfvars. The same registration the SPA signs in against by
    default, which is why roles assigned here reach the browser's token.

.PARAMETER Assign
    Zero or more "upn=RoleValue" pairs, e.g.
        -Assign "ada@desicon.com=FinanceOfficer","obi@desicon.com=FinanceManager"
    Assign the two to DIFFERENT people. The maker-checker guard on AUTHORISE
    refuses a posting authorised by whoever input it, so one person holding
    both roles cannot complete a claim -- correctly.

.EXAMPLE
    ./scripts/bootstrap-app-roles.ps1 -ApiClientId "<guid>" `
        -Assign "ada@desicon.com=FinanceOfficer","obi@desicon.com=FinanceManager"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiClientId,

    [string[]]$Assign = @()
)

$ErrorActionPreference = "Stop"

# Stable ids. Do not regenerate -- see TRAPS above.
$RoleDefinitions = @(
    @{
        id                 = "3f1b8c2a-6d4e-4a7b-9c15-2e8f0d6a4b31"
        value              = "FinanceOfficer"
        displayName        = "Finance Officer"
        description        = "Verifies receipts, captures the Treasury number, prepares GL lines as inputer, and executes payment. Cannot authorise their own posting."
        allowedMemberTypes = @("User")
        isEnabled          = $true
    },
    @{
        id                 = "b7e94d05-1a83-4c62-8f0d-5a3e71c9b284"
        value              = "FinanceManager"
        displayName        = "Finance Manager"
        description        = "Gives final approval and authorises postings as checker. Cannot authorise a posting they input."
        allowedMemberTypes = @("User")
        isEnabled          = $true
    }
)

function Invoke-Graph {
    param([string]$Method, [string]$Uri, [object]$Body)

    if ($null -eq $Body) {
        $result = az rest --method $Method --uri $Uri --headers "Content-Type=application/json"
    }
    else {
        $tmp = New-TemporaryFile
        ($Body | ConvertTo-Json -Depth 10 -Compress) | Set-Content -Path $tmp -NoNewline
        try {
            $result = az rest --method $Method --uri $Uri `
                --headers "Content-Type=application/json" --body "@$tmp"
        }
        finally {
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Graph $Method $Uri failed."
    }

    if ([string]::IsNullOrWhiteSpace($result)) { return $null }
    return $result | ConvertFrom-Json
}

# ── The application object ───────────────────────────────────────────────
$app = (Invoke-Graph GET "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$ApiClientId'").value | Select-Object -First 1

if ($null -eq $app) {
    throw "No application registration found with appId '$ApiClientId'. Check entra_client_id in dev.auto.tfvars."
}

Write-Host "Application: $($app.displayName) ($($app.id))" -ForegroundColor Cyan

# ── Merge, never replace ─────────────────────────────────────────────────
$existing = @($app.appRoles)
$merged = [System.Collections.ArrayList]::new()

foreach ($role in $existing) {
    [void]$merged.Add($role)
}

$added = @()
foreach ($definition in $RoleDefinitions) {
    $match = $existing | Where-Object { $_.value -eq $definition.value }

    if ($match) {
        Write-Host "  role '$($definition.value)' already defined ($($match.id))" -ForegroundColor DarkGray

        if ($match.id -ne $definition.id) {
            Write-Warning "  '$($definition.value)' exists with id $($match.id), not the expected $($definition.id). Leaving it alone -- existing assignments reference the id in the directory, and changing it would orphan them."
        }
        continue
    }

    [void]$merged.Add([pscustomobject]$definition)
    $added += $definition.value
}

if ($added.Count -gt 0) {
    Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{ appRoles = @($merged) } | Out-Null
    Write-Host "Added role(s): $($added -join ', ')" -ForegroundColor Green
}
else {
    Write-Host "No new roles to add." -ForegroundColor DarkGray
}

# ── The service principal assignments are made against ───────────────────
$sp = (Invoke-Graph GET "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$ApiClientId'").value | Select-Object -First 1

if ($null -eq $sp) {
    throw "The application exists but has no service principal in this tenant. Create one with: az ad sp create --id $ApiClientId"
}

# Re-read: the ids to assign against must come from the directory's current
# state, not from the local definitions, in case a role already existed under
# a different id (warned about above).
$appNow = Invoke-Graph GET "https://graph.microsoft.com/v1.0/applications/$($app.id)"
$roleIdByValue = @{}
foreach ($role in $appNow.appRoles) { $roleIdByValue[$role.value] = $role.id }

$assignments = (Invoke-Graph GET "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo").value

foreach ($pair in $Assign) {
    $parts = $pair -split "=", 2
    if ($parts.Count -ne 2) {
        throw "'$pair' is not in the form upn=RoleValue."
    }

    $upn = $parts[0].Trim()
    $roleValue = $parts[1].Trim()

    if (-not $roleIdByValue.ContainsKey($roleValue)) {
        throw "Role '$roleValue' is not defined on this registration. Defined: $($roleIdByValue.Keys -join ', ')"
    }

    $user = Invoke-Graph GET "https://graph.microsoft.com/v1.0/users/$upn"
    $roleId = $roleIdByValue[$roleValue]

    $already = $assignments | Where-Object { $_.principalId -eq $user.id -and $_.appRoleId -eq $roleId }
    if ($already) {
        Write-Host "  $upn already holds $roleValue" -ForegroundColor DarkGray
        continue
    }

    Invoke-Graph POST "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo" @{
        principalId = $user.id
        resourceId  = $sp.id
        appRoleId   = $roleId
    } | Out-Null

    Write-Host "  $upn -> $roleValue" -ForegroundColor Green
}

Write-Host ""
Write-Host "Assigned users must sign out and back in. An access token already issued does not gain the claim." -ForegroundColor Yellow
Write-Host ""
Write-Host "Still undefined, and documented in docs/04 as though they exist:" -ForegroundColor Yellow
Write-Host "  Employee, LineManager, DepartmentHead  -- authority comes from the org chart, not a claim; the table is misleading on this point" -ForegroundColor DarkGray
Write-Host "  ProcurementOfficer, Administrator       -- no code reads either; the rows describe intent, not behaviour" -ForegroundColor DarkGray

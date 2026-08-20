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
    Four roles: CostControlOfficer, TreasuryOfficer, FinanceManager and
    DirectorOfFinance. Those are the values the code actually reads -- the
    `"actor": { "role": ... }` specs in modules/*.workflow.json, and the only
    values RequestActionService can act on.

    FinanceOfficer, which covered Cost Control and Treasury as one role until
    version 3, is no longer created here. See the note beside the definitions
    below: removing it stops the role being re-created but does not delete it
    from the directory.

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
        -Assign "ada@desicon.com=CostControlOfficer","obi@desicon.com=TreasuryOfficer"

    Assign each to a DIFFERENT person. Nothing in this script enforces that,
    and nothing at runtime will complain: the guards compare Employee.Id, so
    one human holding two roles satisfies every one of them while providing no
    separation at all. That is the failure mode -- not a refusal, a silence.

    FinanceOfficer is no longer defined here and must not be assigned to
    anyone. If it still exists in the directory, delete it -- docs/15 §1b.

.EXAMPLE
    ./scripts/bootstrap-app-roles.ps1 -ApiClientId "<guid>" `
        -Assign "ada@desicon.com=CostControlOfficer", `
                "obi@desicon.com=TreasuryOfficer", `
                "chima@desicon.com=FinanceManager", `
                "tomy@desicon.com=DirectorOfFinance"
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
        id                 = "9d2c5f31-4a86-4b09-b7e2-1c6083f4a5d9"
        value              = "CostControlOfficer"
        displayName        = "Cost Control Officer"
        description        = "Verifies that a claim or advance is costed to the right cost centre or project, checks receipts, and captures the Treasury number. Does not post to Business Central and does not touch money."
        allowedMemberTypes = @("User")
        isEnabled          = $true
    },
    @{
        id                 = "2a7f4b60-c358-4e91-8d04-6b19e73c2f85"
        value              = "TreasuryOfficer"
        displayName        = "Treasury Officer (Accounts)"
        description        = "Posts the approved request in Microsoft Dynamics Business Central, records the BC document number, executes payment and releases cash. Must not be the same person as the Cost Control Officer who verified it."
        allowedMemberTypes = @("User")
        isEnabled          = $true
    },
    # FinanceOfficer is deliberately absent. It covered Cost Control and
    # Treasury as one role until workflow version 3, and was retained only
    # while requests pinned to version 2 still existed. None do: the database
    # was reset for UAT on 10 Aug 2026 and the version-2 definition files were
    # deleted on the 19th.
    #
    # This script MERGES rather than replaces, so removing it here stops the
    # role being re-created but does NOT delete it from the directory. That is
    # a manual two-step -- a role cannot be deleted while isEnabled is true --
    # and it is listed in docs/15 section 1b. Until it is done, an
    # administrator can still assign a role that grants everything both
    # replacement roles grant.
    @{
        id                 = "b7e94d05-1a83-4c62-8f0d-5a3e71c9b284"
        value              = "FinanceManager"
        displayName        = "Accounts Manager"
        description        = "Approves claims and advances on behalf of Accounts, and confirms refunds. Releases the request for treatment, but does not release money."
        allowedMemberTypes = @("User")
        isEnabled          = $true
    },
    @{
        id                 = "c41a6e78-9b25-4d13-a7f6-8e02c5b9d746"
        value              = "DirectorOfFinance"
        displayName        = "Director of Finance (DMD)"
        description        = "Final approval before any payment. No money leaves Desicon without this role, and no other role can substitute for it."
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

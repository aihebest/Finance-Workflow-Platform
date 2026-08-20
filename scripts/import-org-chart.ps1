<#
.SYNOPSIS
    Imports Desicon's real org chart -- departments, requesters and Heads of
    Department -- from a CSV. Idempotent.

.DESCRIPTION
    scripts/seed-dev-org-chart.ps1 is a dev fixture: six named slots, one
    department, useful for a walkthrough and useless for the company. This is
    the one that loads the real list.

    WHAT THE CSV LOOKS LIKE
    -----------------------
    One row per person who RAISES requests:

        Email,FullName,DepartmentCode,DepartmentName,LineManagerEmail
        logistics@desicongroup.com,Logistics Desicon,Logistics,Logistics,emem.allagoa@desicongroup.com

    The approver in LineManagerEmail is the Head of Department. They do NOT
    need their own row -- this script creates one for them, taking their name
    from Entra ID. That matters because the HOD needs an employee record as
    much as the requester does, and the first version of Desicon's file listed
    only requesters.

    An IsDepartmentHead column is accepted and IGNORED, deliberately. In the
    file Desicon supplied it was set against the requester in every row while
    the actual head sat in LineManagerEmail, and reading it would have made
    every requester the head of their own department -- which is to say, an
    approver of their own claims. Headship is derived from LineManagerEmail
    instead, where the answer demonstrably is.

    WHAT IT VERIFIES BEFORE IT WRITES ANYTHING
    ------------------------------------------
    Every email is resolved against Entra ID first. A typo, a leaver, or a
    distribution list rather than an account fails here, named, with nothing
    written. The alternative is an employee row whose EntraObjectId matches no
    real account: it looks correct in every report and the person can never
    sign in.

    WHAT IT DOES NOT DO
    -------------------
    Grant roles. Cost Control, Treasury, the Accounts Manager and the Director
    of Finance are Entra app roles -- see bootstrap-app-roles.ps1. A person
    needs both the row this creates AND the claim that grants, and four
    separate people on this project have had one without the other.

.PARAMETER Path
    The CSV.

.PARAMETER WhatIf
    Resolve and report, write nothing. Run this first, always: it is the only
    way to see whose email does not resolve before it matters.

.EXAMPLE
    . .\scripts\dev-db-connect.ps1
    .\scripts\import-org-chart.ps1 -Path .\org-chart.csv -WhatIf
    .\scripts\import-org-chart.ps1 -Path .\org-chart.csv
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Path,

    [string]$SqlServer = "sql-desicon-fw-dev.database.windows.net",
    [string]$Database  = "DesiconFinanceWorkflow"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) { throw "CSV not found: $Path" }

if ([string]::IsNullOrWhiteSpace($token)) {
    throw "`$token is empty. Dot-source the connection script first:  . .\scripts\dev-db-connect.ps1"
}

$csv = Import-Csv -Path $Path

foreach ($column in @("Email", "FullName", "DepartmentCode", "DepartmentName", "LineManagerEmail")) {
    if (-not ($csv | Get-Member -Name $column -MemberType NoteProperty)) {
        throw "CSV is missing the '$column' column."
    }
}

# ── Resolve every address against the directory ─────────────────────────────
# Cached: heads appear on several rows, and one Graph call per row would be
# both slow and needlessly rate-limited.
$directory = @{}

function Resolve-Account {
    param([string]$Upn, [string]$Context)

    $key = $Upn.Trim().ToLowerInvariant()
    if ($directory.ContainsKey($key)) { return $directory[$key] }

    $json = az ad user show --id $key --query "{oid:id, name:displayName, upn:userPrincipalName, mail:mail}" -o json 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "No directory account for '$Upn' ($Context). Check it against: az ad user list --query `"[?contains(userPrincipalName,'<part>')]`" -o table"
    }

    $user = $json | ConvertFrom-Json
    $resolved = [pscustomobject]@{
        Oid   = $user.oid
        Name  = $user.name
        Email = if ($user.mail) { $user.mail } else { $user.upn }
    }

    $directory[$key] = $resolved
    return $resolved
}

$rows = [System.Collections.ArrayList]::new()
$seen = @{}

foreach ($line in $csv) {
    $requester = Resolve-Account -Upn $line.Email -Context "requester"
    $head      = Resolve-Account -Upn $line.LineManagerEmail -Context "head of $($line.DepartmentCode)"

    if ($requester.Oid -eq $head.Oid) {
        throw "$($line.Email) is listed as their own approver in department $($line.DepartmentCode). Nobody can approve their own request -- the guard refuses it, so the claim would stop dead."
    }

    foreach ($person in @(
        @{ Acct = $requester; Head = 0 },
        @{ Acct = $head;      Head = 1 })) {

        # A head named on several rows, or a requester listed twice, is one
        # person. Deduplicated on object id, and the first mention wins.
        $dedupe = "$($person.Acct.Oid)|$($line.DepartmentCode)"
        if ($seen.ContainsKey($dedupe)) { continue }
        $seen[$dedupe] = $true

        [void]$rows.Add([pscustomobject]@{
            Email          = $person.Acct.Email
            FullName       = $person.Acct.Name
            EntraObjectId  = $person.Acct.Oid
            DepartmentCode = $line.DepartmentCode.Trim()
            DepartmentName = $line.DepartmentName.Trim()
            IsHead         = $person.Head
        })
    }
}

Write-Host ""
Write-Host "Resolved $($rows.Count) people across $(($rows.DepartmentCode | Sort-Object -Unique).Count) departments:" -ForegroundColor Cyan
$rows |
    Sort-Object DepartmentCode, IsHead, FullName |
    Format-Table @{ L = "Dept"; E = { $_.DepartmentCode } },
                 @{ L = "Role"; E = { if ($_.IsHead -eq 1) { "HEAD" } else { "raises" } } },
                 FullName, Email -AutoSize |
    Out-String | Write-Host

# A head who heads more than one department is legitimate -- worth surfacing
# rather than discovering when two departments queue on one person.
$rows |
    Where-Object IsHead -eq 1 |
    Group-Object EntraObjectId |
    Where-Object Count -gt 1 |
    ForEach-Object {
        $name = ($_.Group | Select-Object -First 1).FullName
        Write-Host "  $name heads $($_.Count) departments: $(($_.Group.DepartmentCode) -join ', ')" -ForegroundColor Yellow
    }

if (-not $PSCmdlet.ShouldProcess("$Database on $SqlServer", "import $($rows.Count) people")) {
    Write-Host ""
    Write-Host "-WhatIf: nothing written." -ForegroundColor DarkGray
    return
}

$json = $rows | ConvertTo-Json -Compress -Depth 3

Invoke-Sqlcmd `
    -ServerInstance $SqlServer `
    -Database $Database `
    -AccessToken $token `
    -InputFile (Join-Path $PSScriptRoot "import-org-chart.sql") `
    -Variable @("RowsJson=$json") `
    -Verbose |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Imported. Two things this script did NOT do:" -ForegroundColor Yellow
Write-Host "  1. Grant Entra app roles -- see bootstrap-app-roles.ps1. A row without a claim" -ForegroundColor Gray
Write-Host "     approves nothing; a claim without a row cannot sign in." -ForegroundColor Gray
Write-Host "  2. Deactivate anyone missing from the file. Leavers are a decision." -ForegroundColor Gray

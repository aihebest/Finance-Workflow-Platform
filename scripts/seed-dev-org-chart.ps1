<#
.SYNOPSIS
    Seeds the dev org chart from five email addresses. Resolves Entra object
    ids itself.

.DESCRIPTION
    seed-dev-org-chart.sql takes fifteen sqlcmd variables -- an object id, a
    name and an email for each of five people. Assembling that by hand means
    copying five GUIDs out of `az ad user list` without transposing a
    character, and a wrong one does not fail: it inserts a person whose
    EntraObjectId matches no token, so they sign in and are told they are not
    provisioned, which reads like an authentication problem and is not.

    This takes the five email addresses instead and asks the directory for
    everything else.

    WHAT IT REFUSES
    ---------------
    The same person in both Finance slots. The AUTHORISE guard requires the
    authoriser to differ from the inputer, so that configuration produces a
    claim that reaches AUTHORISATION and can never leave it. Better to refuse
    here, where the reason is legible, than at 5pm against a real claim.

    It does not refuse the requester also being the Finance Officer, because
    that is a legitimate thing to test -- the self-approval check should catch
    it, and watching it do so is worth more than being prevented from trying.

.PARAMETER Requester
    The person who raises claims. If dev already has an employee seeded by
    seed-dev-employee.sql, pass that same address here so the existing row is
    relinked rather than a second one created alongside it.

.EXAMPLE
    . .\scripts\dev-db-connect.ps1
    .\scripts\seed-dev-org-chart.ps1 `
        -Requester      "a.best@saidelafrica.com" `
        -Manager        "victor.obasi@desicongroup.com" `
        -Head           "uche.obodiwe@desicongroup.com" `
        -CostControlOfficer "anita.ekeke@desicongroup.com" `
        -TreasuryOfficer    "olanrewaju.atanda@desicongroup.com" `
        -FinanceManager     "wisdom.iheagbam@desicongroup.com" `
        -DirectorOfFinance  "tomy.john@desicongroup.com"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Requester,
    [Parameter(Mandatory = $true)][string]$Manager,
    [Parameter(Mandatory = $true)][string]$Head,
    # Two Accounts desks, not one. Cost Control checks the costing;
    # Treasury posts it in Business Central and moves the money. Workflow
    # version 3 separated them -- see docs/14.
    [Parameter(Mandatory = $true)][string]$CostControlOfficer,
    [Parameter(Mandatory = $true)][string]$TreasuryOfficer,
    [Parameter(Mandatory = $true)][string]$FinanceManager,

    # The DMD. He needs an Employees row like everyone else -- holding the
    # DirectorOfFinance claim gets him the notification, not the ability to
    # open it. Every payment stops at him, so he is the worst person in the
    # chain to leave unseeded.
    [Parameter(Mandatory = $true)][string]$DirectorOfFinance,

    [string]$SqlServer = "sql-desicon-fw-dev.database.windows.net",

    # NOT sqldb-desicon-fw-dev. The server follows the Azure resource naming
    # convention and the database does not -- see database_name in
    # infra/terraform/environments/dev/main.tf, and the default in
    # dev-bootstrap-db.ps1 which has been right all along.
    #
    # Worth knowing because the failure is actively misleading: connecting as
    # an Entra principal to a database that does not exist reports
    #   Login failed for user '<token-identified principal>'
    # not "database not found". That sends you to check the SQL Entra admin,
    # the token tenant and the firewall rule, none of which are wrong.
    [string]$Database  = "DesiconFinanceWorkflow"
)

$ErrorActionPreference = "Stop"

if (-not $token) {
    throw "No `$token in this session. Dot-source the connection helper first -- note the leading dot, without it the token does not survive into your session:`n`n    . .\scripts\dev-db-connect.ps1"
}

function Resolve-DirectoryUser {
    param([string]$Upn, [string]$Slot)

    $json = az ad user show --id $Upn 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "No directory user matches '$Upn' (needed for the $Slot slot). Check it against: az ad user list --query `"[?contains(userPrincipalName,'<part>')]`" -o table"
    }

    $user = $json | ConvertFrom-Json

    [pscustomobject]@{
        Slot  = $Slot
        Oid   = $user.id
        Name  = $user.displayName
        # mail is frequently null on accounts created without a mailbox;
        # userPrincipalName is always present and is what people recognise.
        Email = if ($user.mail) { $user.mail } else { $user.userPrincipalName }
    }
}

$people = @(
    Resolve-DirectoryUser -Upn $Requester      -Slot "Requester"
    Resolve-DirectoryUser -Upn $Manager        -Slot "Line manager"
    Resolve-DirectoryUser -Upn $Head           -Slot "Department head"
    Resolve-DirectoryUser -Upn $CostControlOfficer -Slot "Cost Control"
    Resolve-DirectoryUser -Upn $TreasuryOfficer    -Slot "Treasury"
    Resolve-DirectoryUser -Upn $FinanceManager     -Slot "Finance Manager"
    Resolve-DirectoryUser -Upn $DirectorOfFinance  -Slot "Director of Finance"
)

$officer  = $people | Where-Object Slot -eq "Cost Control"
$treasury = $people | Where-Object Slot -eq "Treasury"
$finMgr   = $people | Where-Object Slot -eq "Finance Manager"
$dmd      = $people | Where-Object Slot -eq "Director of Finance"

# Checked pairwise rather than one pair at a time. The version-2 script only
# compared Cost Control against the Accounts Manager, which was the only
# separation that existed then; a third and fourth approver added later would
# have slipped past a check written to know about two.
$accounts = @($officer, $treasury, $finMgr, $dmd)
foreach ($a in $accounts) {
    foreach ($b in $accounts) {
        if ($a.Slot -lt $b.Slot -and $a.Oid -eq $b.Oid) {
            throw "$($a.Slot) and $($b.Slot) resolve to the same person ($($a.Name)). Each stage exists to be a separate pair of eyes; one person holding both satisfies every guard while providing no separation. Use different people."
        }
    }
}

$people | Format-Table Slot, Name, Email, Oid -AutoSize | Out-String | Write-Host

$requesterUser = $people | Where-Object Slot -eq "Requester"
$managerUser   = $people | Where-Object Slot -eq "Line manager"
$headUser      = $people | Where-Object Slot -eq "Department head"

Invoke-Sqlcmd `
    -ServerInstance $SqlServer `
    -Database $Database `
    -AccessToken $token `
    -InputFile (Join-Path $PSScriptRoot "seed-dev-org-chart.sql") `
    -Variable @(
        "RequesterOid=$($requesterUser.Oid)",
        "RequesterName=$($requesterUser.Name)",
        "RequesterEmail=$($requesterUser.Email)",
        "ManagerOid=$($managerUser.Oid)",
        "ManagerName=$($managerUser.Name)",
        "ManagerEmail=$($managerUser.Email)",
        "HeadOid=$($headUser.Oid)",
        "HeadName=$($headUser.Name)",
        "HeadEmail=$($headUser.Email)",
        "OfficerOid=$($officer.Oid)",
        "OfficerName=$($officer.Name)",
        "OfficerEmail=$($officer.Email)",
        "TreasuryOid=$($treasury.Oid)",
        "TreasuryName=$($treasury.Name)",
        "TreasuryEmail=$($treasury.Email)",
        "DmdOid=$($dmd.Oid)",
        "DmdName=$($dmd.Name)",
        "DmdEmail=$($dmd.Email)",
        "FinManagerOid=$($finMgr.Oid)",
        "FinManagerName=$($finMgr.Name)",
        "FinManagerEmail=$($finMgr.Email)"
    ) | Format-Table -AutoSize

Write-Host ""
Write-Host "Now grant the Finance roles -- they are Entra claims, not database rows:" -ForegroundColor Yellow
Write-Host "  .\scripts\bootstrap-app-roles.ps1 -ApiClientId `"8deb5019-590d-4ef3-bb61-f5d450d341b5`" ``" -ForegroundColor Gray
Write-Host "      -Assign `"$($officer.Email)=CostControlOfficer`",`"$($treasury.Email)=TreasuryOfficer`",`"$($finMgr.Email)=FinanceManager`",`"$($dmd.Email)=DirectorOfFinance`"" -ForegroundColor Gray

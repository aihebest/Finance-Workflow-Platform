<#
.SYNOPSIS
    Prepares the Entra app registration the SPA signs in against, and prints
    the GitHub secrets the deploy job needs.

.DESCRIPTION
    Three things must be true before the frontend can authenticate, and none
    of them is Terraform's to do -- app registrations are directory objects,
    not Azure resources:

      1. The API's registration exposes a delegated scope the SPA can ask for.
         Without it, MSAL requests a scope Entra does not recognise and the
         sign-in fails with AADSTS65001 (consent) rather than anything that
         mentions scopes.

      2. A Single-page application platform exists with the Front Door origin
         as a redirect URI. This is the step most often got wrong: adding the
         URI under the *Web* platform looks identical in the portal and fails
         at token exchange, because the Web platform expects a client secret
         and a browser cannot hold one. Entra reports it as
         AADSTS9002326 "cross-origin token redemption is permitted only for
         the Single-Page Application client-type".

      3. The SPA is authorised to request that scope without every user being
         prompted to consent individually.

    DEFAULT: one registration serving both
    --------------------------------------
    By default this reuses the API's registration for the SPA as well, which
    is normal for a single-tenant line-of-business application and keeps one
    object to manage. The alternative -- a separate client registration -- is
    a cleaner separation of client from resource and is worth doing if the
    API ever gains a second consumer. Pass -SeparateClientRegistration to get
    that shape instead.

.PARAMETER ApiClientId
    Application (client) id of the API registration. dev.auto.tfvars calls
    this entra_client_id.

.PARAMETER RedirectUri
    Where Entra returns the user after sign-in. The Front Door endpoint,
    with no trailing path -- MSAL is configured with
    redirectUri = window.location.origin.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiClientId,

    [Parameter(Mandatory = $true)]
    [string]$RedirectUri,

    [string]$ScopeName = "access_as_user",
    [switch]$SeparateClientRegistration
)

$ErrorActionPreference = "Stop"

function Invoke-Graph {
    param([string]$Method, [string]$Uri, [object]$Body)

    if ($null -eq $Body) {
        return az rest --method $Method --uri $Uri --headers "Content-Type=application/json" | ConvertFrom-Json
    }

    $tmp = New-TemporaryFile
    ($Body | ConvertTo-Json -Depth 10 -Compress) | Set-Content -Path $tmp -NoNewline

    try {
        az rest --method $Method --uri $Uri --headers "Content-Type=application/json" --body "@$tmp" | ConvertFrom-Json
    }
    finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

# ── The API registration, and the scope it exposes ───────────────────────
$api = az ad app show --id $ApiClientId | ConvertFrom-Json
Write-Host "API registration: $($api.displayName) ($($api.appId))"

$identifierUri = "api://$ApiClientId"

if ($api.identifierUris -notcontains $identifierUri) {
    Write-Host "Setting identifier URI $identifierUri ..."
    az ad app update --id $ApiClientId --identifier-uris $identifierUri
    $api = az ad app show --id $ApiClientId | ConvertFrom-Json
}

# ── Token version ────────────────────────────────────────────────────────
# The registration must issue v2 tokens, because modules/app-service points
# Easy Auth at the v2 issuer (tenant_auth_endpoint .../v2.0) and MSAL v3 is a
# v2-endpoint library.
#
# Left at the default (null = v1), the token's audience is api://<client-id>
# -- which matches -- but its issuer is https://sts.windows.net/<tenant>/,
# which does not. Easy Auth then returns a bare 401 with no indication that
# the issuer was the problem, before the request reaches ASP.NET at all. The
# sign-in succeeds, the SPA renders, and every API call fails.
if ($api.api.requestedAccessTokenVersion -ne 2) {
    Write-Host "Setting requestedAccessTokenVersion to 2 (was $($api.api.requestedAccessTokenVersion)) ..."

    Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($api.id)" `
        -Body @{ api = @{ requestedAccessTokenVersion = 2 } } | Out-Null

    $api = az ad app show --id $ApiClientId | ConvertFrom-Json
} else {
    Write-Host "requestedAccessTokenVersion already 2." -ForegroundColor DarkGray
}

$existingScope = $api.api.oauth2PermissionScopes | Where-Object { $_.value -eq $ScopeName }

if ($existingScope) {
    Write-Host "Scope '$ScopeName' already exposed ($($existingScope.id))." -ForegroundColor DarkGray
    $scopeId = $existingScope.id
} else {
    Write-Host "Exposing delegated scope '$ScopeName' ..."
    $scopeId = [guid]::NewGuid().ToString()

    $scopes = @($api.api.oauth2PermissionScopes) + @(@{
            id                      = $scopeId
            value                   = $ScopeName
            type                    = "User"
            isEnabled               = $true
            adminConsentDisplayName = "Access the finance workflow API"
            adminConsentDescription = "Allows the signed-in user to raise, approve and act on finance workflow requests through the API."
            userConsentDisplayName  = "Access the finance workflow platform"
            userConsentDescription  = "Allows you to raise and approve requests on your behalf."
        })

    Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($api.id)" `
        -Body @{ api = @{ oauth2PermissionScopes = $scopes } } | Out-Null
}

# ── The client: either the same registration, or a separate one ──────────
if ($SeparateClientRegistration) {
    $clientName = "$($api.displayName)-spa"
    $existing = az ad app list --display-name $clientName --query "[0]" | ConvertFrom-Json

    if ($existing) {
        Write-Host "Client registration '$clientName' already exists." -ForegroundColor DarkGray
        $client = $existing
    } else {
        Write-Host "Creating client registration '$clientName' ..."
        $client = az ad app create --display-name $clientName --sign-in-audience AzureADMyOrg | ConvertFrom-Json
    }
} else {
    Write-Host "Reusing the API registration as the SPA client." -ForegroundColor DarkGray
    $client = $api
}

# ── The SPA platform. Not "Web" -- see the notes above. ──────────────────
$currentSpaUris = @($client.spa.redirectUris)

if ($currentSpaUris -contains $RedirectUri) {
    Write-Host "Redirect URI already registered under the SPA platform." -ForegroundColor DarkGray
} else {
    Write-Host "Adding $RedirectUri to the SPA platform ..."
    $uris = @($currentSpaUris | Where-Object { $_ }) + $RedirectUri

    Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($client.id)" `
        -Body @{ spa = @{ redirectUris = $uris } } | Out-Null
}

# ── Pre-authorise the client for the scope ───────────────────────────────
# Without this every user sees a consent prompt on first sign-in. For an
# internal platform where the client and the API are the same organisation,
# that prompt is noise that teaches people to click through dialogs.
if ($SeparateClientRegistration) {
    $api = az ad app show --id $ApiClientId | ConvertFrom-Json
    $preAuth = @($api.api.preAuthorizedApplications)

    if (-not ($preAuth | Where-Object { $_.appId -eq $client.appId })) {
        Write-Host "Pre-authorising the SPA for '$ScopeName' ..."
        $preAuth += @{ appId = $client.appId; delegatedPermissionIds = @($scopeId) }

        Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($api.id)" `
            -Body @{ api = @{ preAuthorizedApplications = $preAuth } } | Out-Null
    }
}

$tenantId = az account show --query tenantId -o tsv

Write-Host ""
Write-Host "Set these as GitHub repository secrets:" -ForegroundColor Cyan
Write-Host "  VITE_ENTRA_CLIENT_ID  $($client.appId)"
Write-Host "  VITE_API_SCOPE        api://$ApiClientId/$ScopeName"
Write-Host ""
Write-Host "  (AZURE_TENANT_ID is already set: $tenantId)"
Write-Host ""
Write-Host "gh secret set VITE_ENTRA_CLIENT_ID --body `"$($client.appId)`""
Write-Host "gh secret set VITE_API_SCOPE --body `"api://$ApiClientId/$ScopeName`""

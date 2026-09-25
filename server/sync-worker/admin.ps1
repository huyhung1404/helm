<#
.SYNOPSIS
  Admin commands for the Helm sync server: accounts and access tokens.

.EXAMPLE
  $env:HELM_SYNC_ADMIN = '<ADMIN_TOKEN from your password manager>'
  ./admin.ps1 new-account 'Anh'
  ./admin.ps1 new-token <accountId> 'PC nhà' -Days 365
  ./admin.ps1 new-token <accountId> 'Viewer' -ReadOnly
  ./admin.ps1 tokens <accountId>
  ./admin.ps1 revoke <accountId> <tokenId>
  ./admin.ps1 accounts
  ./admin.ps1 disable <accountId>        # revokes every token of the account

  Use -Server http://127.0.0.1:8787 against `npm run dev`.
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('accounts', 'new-account', 'tokens', 'new-token', 'revoke', 'disable')]
    [string] $Command,
    [Parameter(Position = 1)] [string] $Arg1,
    [Parameter(Position = 2)] [string] $Arg2,
    [int] $Days,
    [switch] $ReadOnly,
    [string] $Server = 'https://sync.huyhung1404.com'
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 defaults to the ANSI code page; names such as 'PC nhà' must survive the round trip.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$admin = $env:HELM_SYNC_ADMIN
if (-not $admin) { throw 'Set $env:HELM_SYNC_ADMIN to the ADMIN_TOKEN secret first.' }

function Invoke-Admin([string] $Method, [string] $Path, $Body) {
    $params = @{
        Method  = $Method
        Uri     = "$($Server.TrimEnd('/'))/$Path"
        Headers = @{ Authorization = "Bearer $admin" }
    }
    if ($null -ne $Body) {
        $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress))
        $params.ContentType = 'application/json; charset=utf-8'
    }
    Invoke-RestMethod @params
}

function Require([string] $Value, [string] $Name) {
    if (-not $Value) { throw "Missing <$Name>." }
    $Value
}

function Show-Time($Ms) {
    if ($null -eq $Ms) { return '' }
    [DateTimeOffset]::FromUnixTimeMilliseconds([long]$Ms).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
}

switch ($Command) {
    'accounts' {
        (Invoke-Admin GET 'admin/accounts').accounts |
            Select-Object accountId, name, @{ n = 'created'; e = { Show-Time $_.createdAt } }, @{ n = 'disabled'; e = { Show-Time $_.disabledAt } } |
            Format-Table -AutoSize
    }
    'new-account' {
        $account = Invoke-Admin POST 'admin/accounts' @{ name = (Require $Arg1 'name') }
        "Account created: $($account.accountId) ($($account.name))"
        "Next: ./admin.ps1 new-token $($account.accountId) 'PC nhà'"
    }
    'tokens' {
        $id = Require $Arg1 'accountId'
        (Invoke-Admin GET "admin/accounts/$id/tokens").tokens |
            Select-Object id, name, @{ n = 'scopes'; e = { $_.scopes -join ' ' } },
                @{ n = 'created'; e = { Show-Time $_.createdAt } }, @{ n = 'expires'; e = { Show-Time $_.expiresAt } },
                @{ n = 'lastUsed'; e = { Show-Time $_.lastUsedAt } }, @{ n = 'revoked'; e = { Show-Time $_.revokedAt } } |
            Format-Table -AutoSize
    }
    'new-token' {
        $id = Require $Arg1 'accountId'
        $body = @{ name = (Require $Arg2 'name') }
        if ($Days -gt 0) { $body.expiresInDays = $Days }
        if ($ReadOnly) { $body.scopes = @('sync:read') }
        $token = Invoke-Admin POST "admin/accounts/$id/tokens" $body
        ''
        "Token '$($token.name)' ($($token.id)), scopes: $($token.scopes -join ' '), expires: $(if ($token.expiresAt) { Show-Time $token.expiresAt } else { 'never' })"
        ''
        "    $($token.token)"
        ''
        'Copy it now: it is shown only once. Paste it into Helm > General > Sync on that device.'
    }
    'revoke' {
        $r = Invoke-Admin DELETE "admin/accounts/$(Require $Arg1 'accountId')/tokens/$(Require $Arg2 'tokenId')"
        if ($r.revoked) { 'Token revoked. That device can no longer sync.' }
    }
    'disable' {
        $r = Invoke-Admin POST "admin/accounts/$(Require $Arg1 'accountId')/disable" $null
        "Account disabled; $($r.revoked) token(s) revoked."
    }
}

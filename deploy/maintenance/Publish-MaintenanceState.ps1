#Requires -Version 5.1
<#[.SYNOPSIS] Publishes through the authenticated maintenance API, including durable gate and HTTPS readback. #>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][uri]$ApiBaseUrl,
    [Parameter(Mandatory)][Security.SecureString]$AccessToken,
    [Parameter(Mandatory)][ValidateRange(0, [long]::MaxValue)][long]$ExpectedRevision,
    [Parameter(Mandatory)][ValidateSet('normal', 'scheduled', 'maintenance', 'recovering')][string]$State,
    [ValidateLength(0, 500)][string]$Message = '',
    [Nullable[DateTimeOffset]]$StartsAt,
    [Nullable[DateTimeOffset]]$ExpectedEndAt
)
$ErrorActionPreference = 'Stop'
if (-not $ApiBaseUrl.IsAbsoluteUri -or $ApiBaseUrl.Scheme -ne 'https' -or $ApiBaseUrl.UserInfo -or $ApiBaseUrl.Query -or $ApiBaseUrl.Fragment) {
    throw 'ApiBaseUrl must be an HTTPS URL without credentials, query or fragment.'
}
if ($StartsAt -and $ExpectedEndAt -and $ExpectedEndAt -lt $StartsAt) { throw 'ExpectedEndAt must not precede StartsAt.' }
$maintenanceUrl = $ApiBaseUrl.AbsoluteUri.TrimEnd('/') + '/api/admin/maintenance'
$payload = @{
    expectedRevision = $ExpectedRevision; state = $State; message = $Message
    startsAtUtc = $(if ($StartsAt) { $StartsAt.UtcDateTime.ToString('o') } else { $null })
    expectedEndAtUtc = $(if ($ExpectedEndAt) { $ExpectedEndAt.UtcDateTime.ToString('o') } else { $null })
} | ConvertTo-Json
if (-not $PSCmdlet.ShouldProcess($maintenanceUrl, "Set $State at expected revision $ExpectedRevision")) { return }
$tokenPointer = [IntPtr]::Zero
$plainToken = $null
$headers = $null
try {
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($AccessToken)
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    $headers = @{ Authorization = "Bearer $plainToken"; 'Cache-Control' = 'no-store' }
    # Do not redirect credentials. A 409/503 is a failure; reload the admin status before retrying.
    Invoke-RestMethod -Method Put -Uri $maintenanceUrl -Headers $headers -MaximumRedirection 0 -TimeoutSec 45 `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
}
finally {
    if ($tokenPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer) }
    if ($headers) { $headers.Clear() }
    $plainToken = $null
}

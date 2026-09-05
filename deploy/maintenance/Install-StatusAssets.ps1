#Requires -Version 5.1
<#[.SYNOPSIS] Copies static status assets to an explicitly chosen directory; preserves an existing status.json. #>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Destination) -or $Destination -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+[\\/])') {
    throw 'Destination must be an absolute local or UNC directory path.'
}
$statusDestination = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
if ($statusDestination.TrimEnd('\') -eq [IO.Path]::GetPathRoot($statusDestination).TrimEnd('\')) {
    throw 'A filesystem/share root cannot be used as the status site directory.'
}
if (Test-Path -LiteralPath $statusDestination -PathType Leaf) { throw 'Destination is a file.' }
foreach ($name in @('index.html', 'web.config', 'status.json')) {
    $source = Join-Path $PSScriptRoot $name
    $target = Join-Path $statusDestination $name
    if ($name -eq 'status.json' -and (Test-Path -LiteralPath $target)) {
        Write-Verbose 'Preserving existing status.json; use the authenticated API to change state.'
        continue
    }
    if ($PSCmdlet.ShouldProcess($target, 'Install static status asset')) {
        [IO.Directory]::CreateDirectory($statusDestination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
Write-Verbose 'IIS sites, bindings, certificates, ACLs and Server configuration are not modified by this script.'

#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Watashi の初回パスワード設定 API だけに IIS Windows 認証を設定する。

.DESCRIPTION
  1. Windows Authentication の IIS 役割サービスを確認し、未導入ならインストール
  2. 現在の IIS 構成をバックアップ
  3. サイト全体は匿名認証、/api/auth/win だけは Windows 認証に設定
  4. 設定値を検証し、必要ならアプリケーションプールを再起動

  この設定は IIS の applicationHost.config に保存されるため、Watashi を再発行しても維持される。

.PARAMETER SitePath
  IIS のサイトまたはアプリケーションの構成パス。
  例: Watashi.Server
      Default Web Site/Watashi

.PARAMETER AppPoolName
  Watashi.Server を実行しているアプリケーションプール名。

.PARAMETER WindowsAuthPath
  SitePath から見た Windows 認証対象の URL パス。通常は変更しない。

.PARAMETER SkipWindowsFeatureInstall
  IIS の Windows Authentication 役割サービスの確認と自動インストールを省略する。

.PARAMETER SkipAppPoolRestart
  設定後にアプリケーションプールを再起動しない。

.EXAMPLE
  .\configure-iis-windows-auth.ps1

.EXAMPLE
  .\configure-iis-windows-auth.ps1 -SitePath "Default Web Site/Watashi" -AppPoolName "Watashi.Server.Production"

.EXAMPLE
  .\configure-iis-windows-auth.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    # ★ IIS マネージャーの「サイト」名。配下のアプリの場合は "サイト名/アプリ名"。
    [ValidateNotNullOrEmpty()]
    [string]$SitePath = "Watashi.Server",

    # ★ IIS マネージャーの「アプリケーション プール」名。
    [ValidateNotNullOrEmpty()]
    [string]$AppPoolName = "Watashi.Server",

    # Watashi の固定 API パス。通常は変更しない。
    [ValidateNotNullOrEmpty()]
    [string]$WindowsAuthPath = "api/auth/win",

    [switch]$SkipWindowsFeatureInstall,
    [switch]$SkipAppPoolRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$normalizedSitePath = $SitePath.Trim().Trim("/")
$normalizedAuthPath = $WindowsAuthPath.Trim().Trim("/")
if ([string]::IsNullOrWhiteSpace($normalizedSitePath)) {
    throw "SitePath が空です。"
}
if ([string]::IsNullOrWhiteSpace($normalizedAuthPath)) {
    throw "WindowsAuthPath が空です。"
}

$windowsAuthLocation = "$normalizedSitePath/$normalizedAuthPath"
$iisProviderRelativePath = $normalizedSitePath.Replace('/', '\')
$iisContentPath = "IIS:\Sites\$iisProviderRelativePath"
$iisRoot = 'IIS:\'

Write-Host "Watashi IIS Windows 認証セットアップ" -ForegroundColor Cyan
Write-Host "  サイト/アプリ : $normalizedSitePath"
Write-Host "  認証対象 URL   : /$normalizedAuthPath"
Write-Host "  App Pool       : $AppPoolName"
Write-Host ""

Import-Module WebAdministration -ErrorAction Stop

if (-not (Test-Path -LiteralPath $iisContentPath)) {
    throw "IIS の '$normalizedSitePath' が見つかりません。SitePath を IIS マネージャーの表示名に合わせてください。"
}
if (-not (Test-Path -LiteralPath "IIS:\AppPools\$AppPoolName")) {
    throw "IIS のアプリケーションプール '$AppPoolName' が見つかりません。AppPoolName を確認してください。"
}

if (-not $SkipWindowsFeatureInstall) {
    $getWindowsFeature = Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue
    if ($null -eq $getWindowsFeature) {
        throw "Get-WindowsFeature が使えません。サーバーマネージャーで IIS の「Windows 認証」を導入してから -SkipWindowsFeatureInstall を指定してください。"
    }

    $feature = Get-WindowsFeature -Name Web-Windows-Auth
    if (-not $feature.Installed) {
        if (-not $PSCmdlet.ShouldProcess("IIS 役割サービス Web-Windows-Auth", "インストール")) {
            return
        }

        Write-Host "[1/5] IIS Windows Authentication をインストール..."
        $installResult = Install-WindowsFeature -Name Web-Windows-Auth
        if (-not $installResult.Success) {
            throw "Web-Windows-Auth のインストールに失敗しました。"
        }
    }
    else {
        Write-Host "[1/5] IIS Windows Authentication は導入済みです。"
    }
}
else {
    Write-Host "[1/5] IIS Windows Authentication の確認を省略します。"
}

if (-not $PSCmdlet.ShouldProcess(
    "$normalizedSitePath と $windowsAuthLocation",
    "認証設定を変更して IIS 構成をバックアップ")) {
    return
}

$backupName = "Watashi-WindowsAuth-{0}" -f (Get-Date -Format "yyyyMMdd-HHmmss-fff")
Write-Host "[2/5] IIS 構成をバックアップ: $backupName"
Backup-WebConfiguration -Name $backupName | Out-Null

$anonymousFilter = "/system.webServer/security/authentication/anonymousAuthentication"
$windowsFilter = "/system.webServer/security/authentication/windowsAuthentication"

function Set-AuthenticationEnabled {
    param(
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][string]$Filter,
        [Parameter(Mandatory = $true)][bool]$Enabled
    )

    $setParams = @{
        PSPath = $iisRoot
        Location = $Location
        Filter = $Filter
        Name = "enabled"
        Value = $Enabled
    }
    Set-WebConfigurationProperty @setParams
}

Write-Host "[3/5] サイト全体を匿名認証に設定..."
Set-AuthenticationEnabled -Location $normalizedSitePath -Filter $anonymousFilter -Enabled $true
Set-AuthenticationEnabled -Location $normalizedSitePath -Filter $windowsFilter -Enabled $false

Write-Host "[4/5] /$normalizedAuthPath だけを Windows 認証に設定..."
Set-AuthenticationEnabled -Location $windowsAuthLocation -Filter $anonymousFilter -Enabled $false
Set-AuthenticationEnabled -Location $windowsAuthLocation -Filter $windowsFilter -Enabled $true

function Get-AuthenticationEnabled {
    param(
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][string]$Filter
    )

    $getParams = @{
        PSPath = $iisRoot
        Location = $Location
        Filter = $Filter
        Name = "enabled"
    }
    return [bool](Get-WebConfigurationProperty @getParams).Value
}

$siteAnonymous = Get-AuthenticationEnabled -Location $normalizedSitePath -Filter $anonymousFilter
$siteWindows = Get-AuthenticationEnabled -Location $normalizedSitePath -Filter $windowsFilter
$pathAnonymous = Get-AuthenticationEnabled -Location $windowsAuthLocation -Filter $anonymousFilter
$pathWindows = Get-AuthenticationEnabled -Location $windowsAuthLocation -Filter $windowsFilter

if (-not $siteAnonymous -or $siteWindows -or $pathAnonymous -or -not $pathWindows) {
    throw "設定後の検証に失敗しました。バックアップ '$backupName' から復元できます。"
}

Write-Host "[5/5] 設定結果を確認..."
[pscustomobject]@{
    Location = $normalizedSitePath
    AnonymousAuthentication = $siteAnonymous
    WindowsAuthentication = $siteWindows
}, [pscustomobject]@{
    Location = $windowsAuthLocation
    AnonymousAuthentication = $pathAnonymous
    WindowsAuthentication = $pathWindows
} | Format-Table -AutoSize

if (-not $SkipAppPoolRestart) {
    $poolState = (Get-WebAppPoolState -Name $AppPoolName).Value
    if ($poolState -eq "Started") {
        Restart-WebAppPool -Name $AppPoolName
        Write-Host "アプリケーションプール '$AppPoolName' を再起動しました。"
    }
    else {
        Write-Warning "アプリケーションプール '$AppPoolName' は $poolState のため再起動していません。"
    }
}

Write-Host ""
Write-Host "完了しました。IIS 構成バックアップ: $backupName" -ForegroundColor Green
Write-Host "確認コマンド:"
Write-Host "  curl.exe -s --negotiate -u : https://<サーバー>/api/auth/win/whoami"

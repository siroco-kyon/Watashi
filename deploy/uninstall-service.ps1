<#
.SYNOPSIS
  Watashi.Server / Watashi.Agent の Windows Service を停止・削除する。

.EXAMPLE
  .\uninstall-service.ps1 -ServiceName Watashi.Server
  .\uninstall-service.ps1 -ServiceName Watashi.Agent
#>
param(
    [Parameter(Mandatory=$true)][string]$ServiceName
)

$ErrorActionPreference = "Stop"

if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Warning "サービス '$ServiceName' は見つかりませんでした。"
    return
}

Write-Host "サービス '$ServiceName' を停止..."
Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "サービスを削除..."
sc.exe delete $ServiceName | Out-Null
Write-Host "完了。"

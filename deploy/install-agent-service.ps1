<#
.SYNOPSIS
  Watashi.Agent を Windows Service として登録する PowerShell スクリプト。

.PARAMETER PublishDir
  dotnet publish した Watashi.Agent の出力ディレクトリ

.PARAMETER InstallDir
  サービスバイナリの最終配置先（デフォルト: C:\Program Files\Watashi\Agent）

.PARAMETER ServiceName
  Windows Service 名（デフォルト: Watashi.Agent）

.EXAMPLE
  .\install-agent-service.ps1 -PublishDir D:\publish\Agent
#>
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [string]$InstallDir = "$env:ProgramFiles\Watashi\Agent",
    [string]$ServiceName = "Watashi.Agent",
    [string]$DisplayName = "Watashi Agent",
    [string]$Description = "Watashi リモート SMB 実行エージェント",
    [string]$ServiceAccount = "LocalSystem"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "PublishDir '$PublishDir' が見つかりません。"
}

Write-Host "[1/5] 既存サービスを停止/削除..."
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "[2/5] バイナリを $InstallDir にコピー..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path "$PublishDir\*" -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir "Watashi.Agent.exe"
if (-not (Test-Path $exePath)) {
    throw "Watashi.Agent.exe が $InstallDir に存在しません。"
}

Write-Host "[3/5] Windows Service を作成..."
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto obj= $ServiceAccount DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName $Description | Out-Null

Write-Host "[4/5] 自動再起動を設定..."
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

Write-Host "[5/5] サービスを起動..."
Start-Service $ServiceName
Start-Sleep -Seconds 2
Get-Service $ServiceName | Format-Table -AutoSize

Write-Host ""
Write-Host "完了。停止: Stop-Service $ServiceName / 削除: sc.exe delete $ServiceName"

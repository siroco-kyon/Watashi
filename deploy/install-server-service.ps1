<#
.SYNOPSIS
  Watashi.Server を Windows Service として登録する PowerShell スクリプト。

.DESCRIPTION
  1. publish 済みの Watashi.Server.exe を指定パスからコピー
  2. sc.exe で Windows Service を作成・起動
  3. リカバリ設定で異常終了時の自動再起動を有効化

.PARAMETER PublishDir
  dotnet publish した出力ディレクトリ（exe + dll + appsettings.json を含む）

.PARAMETER InstallDir
  サービスバイナリの最終配置先（デフォルト: C:\Program Files\Watashi\Server）

.PARAMETER ServiceName
  Windows Service 名（デフォルト: Watashi.Server）

.PARAMETER ServiceAccount
  サービス起動アカウント。デフォルト LocalSystem。専用アカウントが推奨。

.EXAMPLE
  .\install-server-service.ps1 -PublishDir D:\publish\Server
#>
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [string]$InstallDir = "$env:ProgramFiles\Watashi\Server",
    [string]$ServiceName = "Watashi.Server",
    [string]$DisplayName = "Watashi Central Server",
    [string]$Description = "Watashi 中央 API/管理サーバ (ASP.NET Core)",
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

$exePath = Join-Path $InstallDir "Watashi.Server.exe"
if (-not (Test-Path $exePath)) {
    throw "Watashi.Server.exe が $InstallDir に存在しません。dotnet publish を先に実行してください。"
}

Write-Host "[3/5] Windows Service を作成..."
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto obj= $ServiceAccount DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName $Description | Out-Null

Write-Host "[4/5] 異常終了時の自動再起動を設定..."
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

Write-Host "[5/5] サービスを起動..."
Start-Service $ServiceName
Start-Sleep -Seconds 2
Get-Service $ServiceName | Format-Table -AutoSize

Write-Host ""
Write-Host "完了。ログは Event Viewer の Application、もしくは appsettings.json の Serilog 出力先で確認してください。"
Write-Host "停止: Stop-Service $ServiceName"
Write-Host "削除: sc.exe delete $ServiceName"

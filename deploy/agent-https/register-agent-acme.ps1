<#
.SYNOPSIS
  Watashi.Agent 用のサーバ証明書を社内 ACME CA から取得するよう Win-ACME に登録する (初回のみ実行)。

.DESCRIPTION
  Win-ACME (wacs.exe) を「PFX ファイル出力 + 更新後に Agent サービス再起動」の構成で登録する。
  成功すると以下が行われる:
    - <PfxDir>\<HostName>.pfx に証明書を出力
    - タスクスケジューラに自動更新タスク (win-acme renew) を登録
    - 更新のたびに after-renew.ps1 (サービス再起動) を自動実行

  登録後、Agent の appsettings.json の Kestrel:Endpoints:Https:Certificate に
  PFX のパスと同じパスワードを設定すること (手順書 AGENT-HTTPS-ACME.md 参照)。

.PARAMETER WacsPath
  wacs.exe のフルパス

.PARAMETER AcmeUrl
  社内 ACME CA のディレクトリ URL (例: https://ca.internal/acme/directory)

.PARAMETER HostName
  証明書を発行するホスト名。中央サーバのノード Endpoint に書くホスト名と一致させること。

.PARAMETER PfxPassword
  PFX のパスワード (SecureString)。appsettings.json に書く値と同じものを指定する。

.PARAMETER PfxDir
  PFX の出力先ディレクトリ

.PARAMETER RenewScript
  更新後フックスクリプトのパス (after-renew.ps1)

.EXAMPLE
  .\register-agent-acme.ps1 `
      -WacsPath "C:\tools\win-acme\wacs.exe" `
      -AcmeUrl  "https://ca.internal/acme/directory" `
      -HostName "agent-a.internal" `
      -PfxPassword (Read-Host -AsSecureString "PFX パスワード")
#>
param(
    [Parameter(Mandatory = $true)][string]$WacsPath,
    [Parameter(Mandatory = $true)][string]$AcmeUrl,
    [Parameter(Mandatory = $true)][string]$HostName,
    [Parameter(Mandatory = $true)][securestring]$PfxPassword,
    [string]$PfxDir = "C:\ProgramData\WatashiAgent\certs",
    [string]$RenewScript = "C:\ProgramData\WatashiAgent\scripts\after-renew.ps1"
)

$ErrorActionPreference = "Stop"

# --- 事前チェック ---
if (-not (Test-Path $WacsPath)) { throw "wacs.exe が見つかりません: $WacsPath" }
if (-not (Test-Path $RenewScript)) {
    throw "更新後フックが見つかりません: $RenewScript`nafter-renew.ps1 を先に配置してください。"
}
New-Item -ItemType Directory -Force -Path $PfxDir | Out-Null

$plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringUni(
    [System.Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($PfxPassword))

Write-Host "Win-ACME へ登録します:"
Write-Host "  ACME CA  : $AcmeUrl"
Write-Host "  ホスト名 : $HostName"
Write-Host "  PFX 出力 : $PfxDir"
Write-Host "  更新フック: $RenewScript"
Write-Host ""

# HTTP-01 (self-hosting) 検証。Win-ACME が検証時のみ 80 番に一時リスナーを立てる。
# 社内 CA が DNS-01 のみの場合は --validation 関連の引数を CA の手順に合わせて変更すること。
& $WacsPath `
    --source manual `
    --host $HostName `
    --baseuri $AcmeUrl `
    --store pfxfile `
    --pfxfilepath $PfxDir `
    --pfxpassword $plainPassword `
    --installation script `
    --script $RenewScript `
    --accepttos

if ($LASTEXITCODE -ne 0) {
    throw "wacs.exe が終了コード $LASTEXITCODE で失敗しました。上記の出力を確認してください。"
}

# --- 結果確認 ---
$pfx = Get-ChildItem -Path $PfxDir -Filter "*.pfx" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $pfx) {
    throw "PFX が $PfxDir に見つかりません。wacs.exe の出力を確認してください。"
}

Write-Host ""
Write-Host "=== 登録完了 ==="
Write-Host "PFX: $($pfx.FullName)"
Write-Host ""
Write-Host "次の手順:"
Write-Host "  1. Agent の appsettings.json の Kestrel:Endpoints を以下に変更:"
Write-Host '       "Https": {'
Write-Host "         `"Url`": `"https://0.0.0.0:8081`","
Write-Host '         "Certificate": {'
Write-Host "           `"Path`": `"$($pfx.FullName.Replace('\', '\\'))`","
Write-Host "           `"Password`": `"<このスクリプトで入力したパスワード>`""
Write-Host '         }'
Write-Host '       }'
Write-Host "  2. Restart-Service Watashi.Agent"
Write-Host "  3. 中央サーバのノード管理画面で Endpoint を https://$($HostName):8081 に変更"
Write-Host "  4. 中央サーバから curl.exe https://$($HostName):8081/health で確認"

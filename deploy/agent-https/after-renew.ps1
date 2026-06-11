<#
.SYNOPSIS
  Win-ACME の証明書更新後フック。Watashi.Agent サービスを再起動して新しい PFX を読み込ませる。

.DESCRIPTION
  Win-ACME の --installation script から呼ばれることを想定。
  Kestrel は起動時にしか PFX を読まないため、更新のたびにサービス再起動が必要。
  実行結果は C:\ProgramData\WatashiAgent\logs\cert-renew.log に追記する。

  Win-ACME 登録例:
    --installation script --script "C:\ProgramData\WatashiAgent\scripts\after-renew.ps1"

.PARAMETER ServiceName
  再起動する Windows Service 名 (デフォルト: Watashi.Agent)

.PARAMETER LogPath
  ログファイルパス
#>
param(
    [string]$ServiceName = "Watashi.Agent",
    [string]$LogPath = "C:\ProgramData\WatashiAgent\logs\cert-renew.log"
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$Message) {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    $dir = Split-Path $LogPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    Write-Host $line
}

try {
    Write-Log "証明書更新フック開始: サービス '$ServiceName' を再起動します。"

    $svc = Get-Service -Name $ServiceName -ErrorAction Stop
    Restart-Service -Name $ServiceName -Force -ErrorAction Stop

    # 起動完了を待って状態確認 (最大 30 秒)
    $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Write-Log "再起動完了: サービス状態 = $((Get-Service $ServiceName).Status)"

    # ローカルの health エンドポイントで応答確認 (証明書名は localhost と不一致のため検証はスキップ)
    $port = 8081
    try {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
        $res = Invoke-WebRequest -Uri "https://localhost:$port/health" -UseBasicParsing -TimeoutSec 10
        Write-Log "health 確認 OK: HTTP $($res.StatusCode) $($res.Content)"
    }
    catch {
        Write-Log "警告: health 確認に失敗しました ($($_.Exception.Message))。ポート番号 ($port) が実際の設定と一致しているか確認してください。"
    }
    finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
    }

    exit 0
}
catch {
    Write-Log "エラー: $($_.Exception.Message)"
    exit 1
}

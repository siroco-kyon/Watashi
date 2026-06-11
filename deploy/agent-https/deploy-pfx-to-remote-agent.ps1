<#
.SYNOPSIS
  PFX をリモートの Agent サーバに配布し、Watashi.Agent サービスを再起動する。

.DESCRIPTION
  CA サーバに到達できない隔離セグメントの Agent (チェーン構成の Agent B など) 向け。
  CA に到達できるマシン (Agent A など) で代理取得した PFX を、管理共有経由でコピーして
  リモートのサービスを再起動する。Win-ACME の更新フック (--installation script) から
  呼び出せば、更新→配布→再起動まで無人化できる。

  リモート再起動は WinRM (Invoke-Command) を優先し、使えない場合は sc.exe (RPC) に
  フォールバックする。

.PARAMETER PfxPath
  配布する PFX のローカルパス

.PARAMETER ComputerName
  配布先の Agent サーバ名

.PARAMETER RemotePfxDir
  配布先ディレクトリ (リモートマシン上のローカルパス)

.PARAMETER ServiceName
  再起動するリモートの Windows Service 名

.PARAMETER HealthUrl
  配布後に確認する health URL (省略可。例: https://agent-b.internal:8081/health)。
  証明書のホスト名検証はスキップして応答のみ確認する。

.EXAMPLE
  .\deploy-pfx-to-remote-agent.ps1 `
      -PfxPath "C:\ProgramData\WatashiAgent\certs\agent-b.internal.pfx" `
      -ComputerName "agent-b" `
      -HealthUrl "https://agent-b.internal:8081/health"
#>
param(
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [Parameter(Mandatory = $true)][string]$ComputerName,
    [string]$RemotePfxDir = "C:\ProgramData\WatashiAgent\certs",
    [string]$ServiceName = "Watashi.Agent",
    [string]$HealthUrl = "",
    [string]$LogPath = "C:\ProgramData\WatashiAgent\logs\cert-deploy.log"
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$Message) {
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $ComputerName, $Message
    $dir = Split-Path $LogPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    Write-Host $line
}

try {
    if (-not (Test-Path $PfxPath)) { throw "PFX が見つかりません: $PfxPath" }

    # --- 1. 管理共有経由でコピー ---
    $remoteUnc = "\\$ComputerName\" + $RemotePfxDir.Replace(":", "$")
    Write-Log "PFX を配布します: $PfxPath -> $remoteUnc"
    if (-not (Test-Path $remoteUnc)) { New-Item -ItemType Directory -Force -Path $remoteUnc | Out-Null }
    Copy-Item -Path $PfxPath -Destination $remoteUnc -Force
    Write-Log "コピー完了"

    # --- 2. リモートのサービス再起動 (WinRM 優先、ダメなら sc.exe) ---
    $restarted = $false
    try {
        Invoke-Command -ComputerName $ComputerName -ErrorAction Stop -ScriptBlock {
            param($svc)
            Restart-Service -Name $svc -Force
            (Get-Service -Name $svc).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
        } -ArgumentList $ServiceName
        $restarted = $true
        Write-Log "サービス再起動完了 (WinRM)"
    }
    catch {
        Write-Log "WinRM での再起動に失敗 ($($_.Exception.Message))。sc.exe にフォールバックします。"
    }

    if (-not $restarted) {
        sc.exe \\$ComputerName stop $ServiceName | Out-Null
        # 停止完了を待つ (最大 30 秒)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Seconds 2
            $state = (sc.exe \\$ComputerName query $ServiceName | Select-String "STATE").ToString()
        } while ($state -notmatch "STOPPED" -and $sw.Elapsed.TotalSeconds -lt 30)
        if ($state -notmatch "STOPPED") { throw "サービスが 30 秒以内に停止しませんでした: $state" }

        sc.exe \\$ComputerName start $ServiceName | Out-Null
        Start-Sleep -Seconds 3
        $state = (sc.exe \\$ComputerName query $ServiceName | Select-String "STATE").ToString()
        if ($state -notmatch "RUNNING") { throw "サービスの起動を確認できませんでした: $state" }
        Write-Log "サービス再起動完了 (sc.exe)"
    }

    # --- 3. health 確認 (任意) ---
    if (-not [string]::IsNullOrWhiteSpace($HealthUrl)) {
        try {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
            $res = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 15
            Write-Log "health 確認 OK: HTTP $($res.StatusCode) $($res.Content)"
        }
        catch {
            Write-Log "警告: health 確認に失敗しました ($($_.Exception.Message))"
        }
        finally {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
        }
    }

    Write-Log "配布処理が完了しました。"
    exit 0
}
catch {
    Write-Log "エラー: $($_.Exception.Message)"
    exit 1
}

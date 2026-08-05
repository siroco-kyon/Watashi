<#
.SYNOPSIS
  Agent サーバ上で PFX を手動入れ替えし、Watashi.Agent サービスを再起動する (定期作業用)。

.DESCRIPTION
  ファイアウォール等で ACME の自動取得・自動配布ができず、証明書を定期的に手作業で
  入れ替える運用向け。入れ替え対象の Agent サーバ上で管理者 PowerShell から実行する。

  実行内容:
    1. 新しい PFX を開いて中身を検証 (パスワード誤り / 期限切れ / ホスト名不一致を事前検出)
    2. 現在配置されている PFX をタイムスタンプ付きでバックアップ
    3. 新しい PFX を配置し、ACL を SYSTEM + Administrators の読み取りのみに絞る
    4. Watashi.Agent サービスを再起動
    5. /health で応答確認。失敗した場合は自動でバックアップに戻して再起動する

  appsettings.json は変更しない。既に設定済みの Path と同じ場所に上書きする前提のため、
  -TargetPath には appsettings.json の Kestrel:Endpoints:Https:Certificate:Path と
  同じ値を指定すること (既定値のまま運用しているなら指定不要)。

.PARAMETER PfxPath
  新しく配置する PFX のパス (USB や作業フォルダに置いたもの)

.PARAMETER PfxPassword
  PFX のパスワード (SecureString)。省略すると事前検証をスキップする。
  appsettings.json に書いてある値と一致していること。

.PARAMETER TargetPath
  配置先のフルパス。appsettings.json に書いてあるパスと一致させる。

.PARAMETER ExpectedHostName
  証明書に入っているべきホスト名。指定すると Subject / SAN と照合する。

.PARAMETER ServiceName
  再起動するサービス名

.PARAMETER HealthUrl
  配置後に確認する health URL。既定はローカルの 8081。

.PARAMETER BackupDir
  入れ替え前の PFX を退避するフォルダ

.PARAMETER SkipRestart
  配置とバックアップだけ行い、サービス再起動を行わない (メンテナンス時間まで待つ場合)

.EXAMPLE
  .\replace-agent-pfx.ps1 -PfxPath D:\agent-b.internal.pfx `
      -PfxPassword (Read-Host -AsSecureString "PFX パスワード") `
      -ExpectedHostName agent-b.internal

.EXAMPLE
  # 中身の確認だけ行い、入れ替えはしない
  .\replace-agent-pfx.ps1 -PfxPath D:\agent-b.internal.pfx `
      -PfxPassword (Read-Host -AsSecureString "PFX パスワード") -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [securestring]$PfxPassword,
    [string]$TargetPath = "C:\ProgramData\WatashiAgent\certs\agent.pfx",
    [string]$ExpectedHostName = "",
    [string]$ServiceName = "Watashi.Agent",
    [string]$HealthUrl = "https://localhost:8081/health",
    [string]$BackupDir = "C:\ProgramData\WatashiAgent\certs\backup",
    [string]$LogPath = "C:\ProgramData\WatashiAgent\logs\cert-replace.log",
    [switch]$SkipRestart
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$Message) {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    $dir = Split-Path $LogPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    Write-Host $line
}

function Get-PfxInfo([string]$Path, [securestring]$Password) {
    # パスワード無指定なら検証をスキップ (null を返す)
    if ($null -eq $Password) { return $null }
    try {
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Path, $Password)
    }
    catch {
        throw "PFX を開けませんでした: $Path`n  → パスワード誤り、またはファイル破損の可能性があります。`n  ($($_.Exception.Message))"
    }
}

function Show-CertInfo($Cert, [string]$Label) {
    Write-Host ""
    Write-Host "--- $Label ---"
    Write-Host "  Subject   : $($Cert.Subject)"
    Write-Host "  発行者    : $($Cert.Issuer)"
    Write-Host "  有効期間  : $($Cert.NotBefore) ～ $($Cert.NotAfter)"
    Write-Host "  Thumbprint: $($Cert.Thumbprint)"
    $san = $Cert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" }
    if ($san) { Write-Host "  SAN       : $($san.Format($false))" }
    Write-Host "  秘密鍵    : $(if ($Cert.HasPrivateKey) { 'あり' } else { 'なし ★TLS に使えません' })"
}

$backupPath = $null
$newCert = $null
$oldCert = $null

try {
    Write-Log "=== PFX 入れ替え開始: $PfxPath -> $TargetPath ==="

    # --- 1. 新しい PFX の検証 ---
    if (-not (Test-Path $PfxPath)) { throw "PFX が見つかりません: $PfxPath" }

    $newCert = Get-PfxInfo -Path $PfxPath -Password $PfxPassword
    if ($null -eq $newCert) {
        Write-Log "警告: -PfxPassword が未指定のため事前検証をスキップします。"
    }
    else {
        Show-CertInfo -Cert $newCert -Label "これから配置する証明書"

        if (-not $newCert.HasPrivateKey) {
            throw "この PFX には秘密鍵が含まれていません。TLS には使えないため中止します。"
        }
        if ($newCert.NotAfter -lt (Get-Date)) {
            throw "この証明書は既に期限切れです ($($newCert.NotAfter))。中止します。"
        }
        if ($newCert.NotBefore -gt (Get-Date)) {
            throw "この証明書はまだ有効期間前です ($($newCert.NotBefore))。中止します。"
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedHostName)) {
            $names = @()
            $simple = $newCert.GetNameInfo("SimpleName", $false)
            if ($simple) { $names += $simple }
            try { $names += ($newCert.DnsNameList | ForEach-Object { $_.Unicode }) } catch { }

            # SAN の表示形式は OS の言語で変わる (DNS Name= / DNS 名= 等) ため "=" の右側を拾う
            $sanExt = $newCert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" }
            if ($sanExt) {
                $names += [regex]::Matches($sanExt.Format($false), '=\s*([^,\r\n]+)') |
                    ForEach-Object { $_.Groups[1].Value.Trim() }
            }
            $names = @($names | Where-Object { $_ } | Select-Object -Unique)

            if ($names.Count -eq 0) {
                Write-Log "警告: 証明書からホスト名を読み取れなかったため照合をスキップします。"
            }
            elseif ($names -contains $ExpectedHostName) {
                Write-Log "ホスト名の確認 OK: $ExpectedHostName (証明書の名前: $($names -join ', '))"
            }
            else {
                throw "証明書に '$ExpectedHostName' が含まれていません (検出: $($names -join ', '))。`n  → 接続元でホスト名不一致エラーになります。中止します。"
            }
        }

        $days = [int]($newCert.NotAfter - (Get-Date)).TotalDays
        Write-Log "この証明書の残り日数: $days 日 (次回作業の目安: $($newCert.NotAfter.AddDays(-14).ToString('yyyy-MM-dd')) まで)"
    }

    # --- 2. 現在の PFX を確認してバックアップ ---
    if (Test-Path $TargetPath) {
        if ($null -ne $PfxPassword) {
            try {
                $oldCert = Get-PfxInfo -Path $TargetPath -Password $PfxPassword
                Show-CertInfo -Cert $oldCert -Label "現在配置されている証明書"
            }
            catch {
                Write-Log "注意: 現在の PFX は今回のパスワードでは開けませんでした (パスワードも変更する場合は appsettings.json の更新も必要です)。"
            }
        }

        if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null }
        $backupPath = Join-Path $BackupDir ("{0}.{1}.bak" -f (Split-Path $TargetPath -Leaf), (Get-Date -Format "yyyyMMdd-HHmmss"))
        if ($PSCmdlet.ShouldProcess($TargetPath, "バックアップ -> $backupPath")) {
            Copy-Item -Path $TargetPath -Destination $backupPath -Force
            Write-Log "バックアップ作成: $backupPath"
        }
    }
    else {
        Write-Log "配置先に既存ファイルはありません (新規配置): $TargetPath"
    }

    # --- 3. 配置 + ACL ---
    if ($PSCmdlet.ShouldProcess($TargetPath, "PFX を配置して ACL を設定")) {
        $targetDir = Split-Path $TargetPath -Parent
        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
        Copy-Item -Path $PfxPath -Destination $TargetPath -Force
        Write-Log "配置完了: $TargetPath"

        # グループ名の言語差を避けるため SID 指定 (S-1-5-18=SYSTEM, S-1-5-32-544=Administrators)
        icacls $TargetPath /inheritance:r /grant "*S-1-5-18:R" /grant "*S-1-5-32-544:R" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Log "ACL 設定完了 (SYSTEM / Administrators の読み取りのみ)"
        }
        else {
            Write-Log "警告: ACL 設定に失敗しました (icacls 終了コード $LASTEXITCODE)。手動で権限を確認してください。"
        }
    }

    # --- 4. サービス再起動 ---
    if ($SkipRestart) {
        Write-Log "-SkipRestart が指定されたため再起動しません。メンテナンス時間に 'Restart-Service $ServiceName' を実行してください。"
        Write-Log "=== 配置のみ完了 ==="
        exit 0
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, "サービス再起動")) {
        Write-Log "サービスを再起動します: $ServiceName"
        Restart-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
        Write-Log "再起動完了: 状態 = $((Get-Service $ServiceName).Status)"
    }

    # --- 5. health 確認 (失敗したらロールバック) ---
    if ($PSCmdlet.ShouldProcess($HealthUrl, "health 確認")) {
        Start-Sleep -Seconds 2
        try {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
            $res = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 15
            Write-Log "health 確認 OK: HTTP $($res.StatusCode) $($res.Content)"
        }
        catch {
            Write-Log "エラー: health 確認に失敗しました ($($_.Exception.Message))"
            if ($backupPath -and (Test-Path $backupPath)) {
                Write-Log "ロールバックします: $backupPath -> $TargetPath"
                Copy-Item -Path $backupPath -Destination $TargetPath -Force
                Restart-Service -Name $ServiceName -Force
                (Get-Service -Name $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
                Write-Log "ロールバック完了。元の証明書で稼働しています。新しい PFX の内容を確認してください。"
            }
            else {
                Write-Log "バックアップが無いためロールバックできません。appsettings.json のパス/パスワードを確認してください。"
            }
            exit 1
        }
        finally {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
        }
    }

    if ($WhatIfPreference) {
        Write-Log "=== -WhatIf のため実際の変更は行っていません (事前確認のみ) ==="
        exit 0
    }

    Write-Log "=== 入れ替え完了 ==="
    if ($null -ne $newCert) {
        Write-Host ""
        Write-Host "次回の入れ替え期限: $($newCert.NotAfter.ToString('yyyy-MM-dd')) (2 週間前の $($newCert.NotAfter.AddDays(-14).ToString('yyyy-MM-dd')) までに作業)"
        Write-Host "台帳・カレンダーへの記録を忘れずに行ってください。"
    }
    Write-Host "接続元のマシンから test-agent-https.ps1 を実行し、証明書検証込みで疎通することを確認してください。"
    exit 0
}
catch {
    Write-Log "エラー: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($newCert) { $newCert.Dispose() }
    if ($oldCert) { $oldCert.Dispose() }
}

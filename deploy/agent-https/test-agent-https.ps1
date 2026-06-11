<#
.SYNOPSIS
  中央サーバから Agent への HTTPS 疎通を検証する。証明書の内容と /health 応答を表示する。

.DESCRIPTION
  中央サーバ上で実行することを想定。以下を順に確認する:
    1. TCP 接続
    2. TLS ハンドシェイクと証明書チェーンの検証 (実際の AgentForwarder と同じ標準検証)
    3. 証明書の Subject / SAN / 有効期限 / 発行者
    4. GET /health の応答

.PARAMETER AgentUrl
  Agent の URL (例: https://agent-a.internal:8081)

.EXAMPLE
  .\test-agent-https.ps1 -AgentUrl https://agent-a.internal:8081
#>
param(
    [Parameter(Mandatory = $true)][string]$AgentUrl
)

$ErrorActionPreference = "Stop"

$uri = [Uri]$AgentUrl
if ($uri.Scheme -ne "https") { throw "https:// の URL を指定してください: $AgentUrl" }
$targetHost = $uri.Host
$port = $uri.Port

Write-Host "=== 1/4 TCP 接続: ${targetHost}:${port} ==="
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $tcp.Connect($targetHost, $port)
    Write-Host "OK: TCP 接続成功"
}
catch {
    Write-Host "NG: TCP 接続失敗 - $($_.Exception.Message)"
    Write-Host "  → Agent サービスの起動状態とファイアウォール (TCP $port) を確認してください。"
    exit 1
}

Write-Host ""
Write-Host "=== 2/4 TLS ハンドシェイク + 証明書チェーン検証 ==="
$chainErrors = $null
try {
    $ssl = New-Object System.Net.Security.SslStream(
        $tcp.GetStream(), $false,
        {
            param($s, $cert, $chain, $errors)
            Set-Variable -Name chainErrors -Value $errors -Scope 1
            return $true  # 検証結果に関わらずハンドシェイクは継続し、証明書情報の表示まで行う
        })
    $ssl.AuthenticateAsClient($targetHost)

    if ($chainErrors -eq [System.Net.Security.SslPolicyErrors]::None) {
        Write-Host "OK: 証明書検証に問題なし (AgentForwarder からの接続も成功するはずです)"
    }
    else {
        Write-Host "NG: 証明書検証エラー = $chainErrors"
        if ("$chainErrors" -match "RemoteCertificateNameMismatch") {
            Write-Host "  → 証明書のホスト名 (下記 Subject/SAN) と接続先ホスト名 '$targetHost' が一致していません。"
        }
        if ("$chainErrors" -match "RemoteCertificateChainErrors") {
            Write-Host "  → ルート CA がこのマシンで信頼されていません。社内 CA のルート証明書を"
            Write-Host "    「信頼されたルート証明機関 (LocalMachine)」にインポートしてください。"
        }
    }

    Write-Host ""
    Write-Host "=== 3/4 証明書の内容 ==="
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
    Write-Host "Subject   : $($cert.Subject)"
    Write-Host "発行者    : $($cert.Issuer)"
    Write-Host "有効期間  : $($cert.NotBefore) ～ $($cert.NotAfter)"
    if ($cert.NotAfter -lt (Get-Date)) { Write-Host "NG: 証明書の有効期限が切れています。" }
    $san = $cert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" }
    if ($san) { Write-Host "SAN       : $($san.Format($false))" }
}
finally {
    if ($ssl) { $ssl.Dispose() }
    $tcp.Dispose()
}

Write-Host ""
Write-Host "=== 4/4 GET /health ==="
try {
    $res = Invoke-WebRequest -Uri "$($AgentUrl.TrimEnd('/'))/health" -UseBasicParsing -TimeoutSec 15
    Write-Host "OK: HTTP $($res.StatusCode) $($res.Content)"
}
catch {
    Write-Host "NG: /health 取得失敗 - $($_.Exception.Message)"
    exit 1
}

if ($chainErrors -ne [System.Net.Security.SslPolicyErrors]::None) {
    Write-Host ""
    Write-Host "※ /health は取得できましたが証明書検証エラーがあるため、実際の Server → Agent 転送は失敗します。上記 2/4 の対処を行ってください。"
    exit 1
}

Write-Host ""
Write-Host "すべて OK。ノード管理画面の Endpoint を $AgentUrl に設定できます。"

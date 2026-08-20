<#
.SYNOPSIS
  Agent への HTTPS 疎通を検証する。証明書の内容と /health 応答を表示する。

.DESCRIPTION
  以下を順に確認する:
    1. TCP 接続
    2. TLS ハンドシェイクと証明書チェーンの検証 (実際の AgentForwarder と同じ標準検証)
    3. 証明書の Subject / SAN / 有効期限 / 発行者
    4. GET /health の応答

  実行する場所は「検証したい区間の接続元」のマシン:
    - Server -> Agent A を調べる   … 中央サーバ上で実行し、-AgentUrl に A の URL
    - Agent A -> Agent B を調べる  … Agent A 上で実行し、-AgentUrl に B の URL
  証明書を検証するのは接続元マシンの証明書ストアなので、実行場所を間違えると結果が変わる。

  [Windows PowerShell 5.1 での注意]
  .NET Framework の SslStream / ServicePointManager は既定で SSL3.0 / TLS1.0 しか提示しない。
  Kestrel は TLS1.2 以上しか受け付けないため、既定のままだとサーバが正常でも
  「SSPI への呼び出しに失敗しました」(AuthenticationException) になる。
  本スクリプトは TLS1.2 (環境が対応していれば TLS1.3 も) を明示的に有効化して接続する。

.PARAMETER AgentUrl
  Agent の URL (例: https://agent-a.internal:8081)。ポートを省略した場合は 443。

.PARAMETER SslProtocol
  握手に使うプロトコルを明示指定する (Tls13 / Tls12 / Tls11 / Tls)。
  省略時は TLS1.2 (+ 利用可能なら TLS1.3)。サーバがどのバージョンに対応しているかの
  切り分けに使う。

.PARAMETER TimeoutSec
  TCP 接続と /health 取得のタイムアウト秒数 (既定 15)。

.EXAMPLE
  .\test-agent-https.ps1 -AgentUrl https://agent-a.internal:8081

.EXAMPLE
  .\test-agent-https.ps1 -AgentUrl https://agent-b.internal:443 -SslProtocol Tls12
#>
param(
    [Parameter(Mandatory = $true)][string]$AgentUrl,
    [string]$SslProtocol,
    [int]$TimeoutSec = 15
)

$ErrorActionPreference = "Stop"

# --- ヘルパー ---------------------------------------------------------------

function Get-EnumNames {
    param([Type]$EnumType)
    return [Enum]::GetNames($EnumType)
}

function Resolve-SslProtocols {
    # 明示指定があればそれを、無ければ Tls12 (+ 環境が対応していれば Tls13) を返す。
    param([string]$Name)
    $names = Get-EnumNames ([System.Security.Authentication.SslProtocols])
    if ($Name) {
        if ($names -notcontains $Name) {
            throw "この環境では -SslProtocol $Name を指定できません。指定可能な値: $($names -join ', ')"
        }
        return [System.Security.Authentication.SslProtocols]$Name
    }
    $value = [int][System.Security.Authentication.SslProtocols]::Tls12
    if ($names -contains 'Tls13') {
        $value = $value -bor [int][System.Security.Authentication.SslProtocols]::Tls13
    }
    return [System.Security.Authentication.SslProtocols]$value
}

function Write-ExceptionChain {
    # SSPI 系のエラーは InnerException 側に本当の理由が入っているため全段表示する。
    param($Exception, [string]$Indent = "  ")
    $ex = $Exception
    $depth = 0
    while ($null -ne $ex -and $depth -lt 5) {
        Write-Host "$Indent$($ex.GetType().Name): $($ex.Message)"
        $ex = $ex.InnerException
        $depth++
    }
}

function Connect-Tcp {
    param([string]$TargetHost, [int]$Port, [int]$TimeoutSec)
    $tcp = New-Object System.Net.Sockets.TcpClient
    $async = $tcp.BeginConnect($TargetHost, $Port, $null, $null)
    if (-not $async.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))) {
        $tcp.Close()
        throw "接続がタイムアウトしました ($TimeoutSec 秒)"
    }
    $tcp.EndConnect($async)
    return $tcp
}

function Invoke-HttpsGet {
    # 確立済みの SslStream 上で直接 HTTP/1.1 GET を行う。
    # Invoke-WebRequest を使わないのは、Windows PowerShell 5.1 では
    # ServicePointManager.ServerCertificateValidationCallback にスクリプトブロックを
    # 渡すと別スレッドから呼ばれた際に実行空間エラーになり、証明書検証を無視した
    # 疎通確認ができないため。
    param([System.Net.Security.SslStream]$Ssl, [string]$HostHeader, [string]$Path, [int]$TimeoutSec)

    $Ssl.ReadTimeout = $TimeoutSec * 1000
    $Ssl.WriteTimeout = $TimeoutSec * 1000
    $request = "GET $Path HTTP/1.1`r`nHost: $HostHeader`r`nUser-Agent: test-agent-https.ps1`r`nAccept: */*`r`nConnection: close`r`n`r`n"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
    $Ssl.Write($bytes, 0, $bytes.Length)
    $Ssl.Flush()

    $buffer = New-Object byte[] 8192
    $memory = New-Object System.IO.MemoryStream
    try {
        while ($true) {
            $read = $Ssl.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }
            $memory.Write($buffer, 0, $read)
        }
    }
    catch [System.IO.IOException] {
        # サーバが Connection: close で切断した際の例外は正常終了として扱う。
    }
    return [System.Text.Encoding]::UTF8.GetString($memory.ToArray())
}

function Split-HttpResponse {
    # 生レスポンスをステータス行 / ヘッダ / 本文に分ける。chunked は簡易的に結合する。
    param([string]$Raw)

    $separator = $Raw.IndexOf("`r`n`r`n")
    if ($separator -lt 0) { return @{ StatusLine = $Raw.Trim(); Body = "" } }
    $head = $Raw.Substring(0, $separator)
    $body = $Raw.Substring($separator + 4)
    $lines = $head -split "`r`n"
    if ($head -match '(?im)^Transfer-Encoding:\s*chunked') {
        $decoded = New-Object System.Text.StringBuilder
        $rest = $body
        while ($rest) {
            $eol = $rest.IndexOf("`r`n")
            if ($eol -lt 0) { break }
            $size = 0
            if (-not [int]::TryParse($rest.Substring(0, $eol).Trim(), [System.Globalization.NumberStyles]::HexNumber, $null, [ref]$size)) { break }
            if ($size -le 0) { break }
            $start = $eol + 2
            if ($start + $size -gt $rest.Length) { break }
            [void]$decoded.Append($rest.Substring($start, $size))
            $rest = $rest.Substring([Math]::Min($start + $size + 2, $rest.Length))
        }
        $body = $decoded.ToString()
    }
    return @{ StatusLine = $lines[0]; Body = $body.Trim() }
}

function Invoke-TlsHandshake {
    # 指定プロトコルで握手を試み、結果をハッシュテーブルで返す。
    # 証明書の検証結果に関わらず握手は継続し、証明書情報の表示まで行う。
    # HttpPath を指定した場合は、同じ接続上で HTTP GET も実行する。
    param(
        [string]$TargetHost,
        [int]$Port,
        [System.Security.Authentication.SslProtocols]$Protocols,
        [int]$TimeoutSec,
        [string]$HttpPath
    )

    $result = @{
        Success      = $false
        Error        = $null
        PolicyErrors = $null
        ChainStatus  = @()
        Certificate  = $null
        Negotiated   = $null
        Cipher       = $null
        HttpStatus   = $null
        HttpBody     = $null
        HttpError    = $null
    }
    $tcp = $null
    $ssl = $null
    try {
        $tcp = Connect-Tcp -TargetHost $TargetHost -Port $Port -TimeoutSec $TimeoutSec
        $script:lastPolicyErrors = $null
        $script:lastChainStatus = @()
        $callback = {
            param($senderObj, $cert, $chain, $errors)
            $script:lastPolicyErrors = $errors
            $script:lastChainStatus = if ($chain) { $chain.ChainStatus } else { @() }
            return $true
        }
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, $callback)
        $ssl.AuthenticateAsClient($TargetHost, $null, $Protocols, $false)

        $result.Success = $true
        $result.PolicyErrors = $script:lastPolicyErrors
        $result.ChainStatus = $script:lastChainStatus
        $result.Certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        $result.Negotiated = $ssl.SslProtocol
        try { $result.Cipher = $ssl.CipherAlgorithm } catch { }

        if ($HttpPath) {
            try {
                $raw = Invoke-HttpsGet -Ssl $ssl -HostHeader "${TargetHost}:${Port}" -Path $HttpPath -TimeoutSec $TimeoutSec
                $parsed = Split-HttpResponse -Raw $raw
                $result.HttpStatus = $parsed.StatusLine
                $result.HttpBody = $parsed.Body
            }
            catch {
                $result.HttpError = $_.Exception
            }
        }
    }
    catch {
        $result.Error = $_.Exception
    }
    finally {
        if ($ssl) { $ssl.Dispose() }
        if ($tcp) { $tcp.Dispose() }
    }
    return $result
}

# --- 入力の解釈 -------------------------------------------------------------

$uri = [Uri]$AgentUrl
if ($uri.Scheme -ne "https") { throw "https:// の URL を指定してください: $AgentUrl" }
$targetHost = $uri.Host
$port = $uri.Port
$isIpLiteral = [bool]($targetHost -as [System.Net.IPAddress])
$protocols = Resolve-SslProtocols -Name $SslProtocol

Write-Host "接続先          : ${targetHost}:${port}"
if (-not $AgentUrl.Contains(":$port")) {
    Write-Host "  ※ URL にポート指定が無いため既定の $port を使用します。Agent の待ち受けポートと一致しているか確認してください。"
}
Write-Host "使用プロトコル  : $protocols"
Write-Host "PowerShell      : $($PSVersionTable.PSVersion)"
Write-Host ""

# --- 1/4 TCP ----------------------------------------------------------------

Write-Host "=== 1/4 TCP 接続: ${targetHost}:${port} ==="
try {
    $probe = Connect-Tcp -TargetHost $targetHost -Port $port -TimeoutSec $TimeoutSec
    $probe.Dispose()
    Write-Host "OK: TCP 接続成功"
}
catch {
    Write-Host "NG: TCP 接続失敗 - $($_.Exception.Message)"
    Write-Host "  → Agent サービスの起動状態とファイアウォール (TCP $port) を確認してください。"
    exit 1
}

# --- 2/4 TLS ----------------------------------------------------------------

Write-Host ""
Write-Host "=== 2/4 TLS ハンドシェイク + 証明書チェーン検証 ==="
$healthPath = "$($uri.AbsolutePath.TrimEnd('/'))/health"
$tls = Invoke-TlsHandshake -TargetHost $targetHost -Port $port -Protocols $protocols -TimeoutSec $TimeoutSec -HttpPath $healthPath

if (-not $tls.Success) {
    Write-Host "NG: TLS ハンドシェイクに失敗しました。"
    Write-ExceptionChain -Exception $tls.Error
    Write-Host ""
    Write-Host "--- プロトコル別の再試行 (サーバがどのバージョンを受け付けるかの確認) ---"
    $names = Get-EnumNames ([System.Security.Authentication.SslProtocols])
    foreach ($candidate in @('Tls13', 'Tls12', 'Tls11', 'Tls')) {
        if ($names -notcontains $candidate) {
            Write-Host ("{0,-6}: この環境の .NET では指定できません" -f $candidate)
            continue
        }
        $one = Invoke-TlsHandshake -TargetHost $targetHost -Port $port -TimeoutSec $TimeoutSec -Protocols ([System.Security.Authentication.SslProtocols]$candidate)
        if ($one.Success) {
            Write-Host ("{0,-6}: OK (ネゴシエート結果 = {1})" -f $candidate, $one.Negotiated)
        }
        else {
            Write-Host ("{0,-6}: NG ({1})" -f $candidate, $one.Error.Message)
        }
    }
    Write-Host ""
    Write-Host "  → すべて NG の場合、その待ち受けポートは TLS を喋っていない可能性があります。"
    Write-Host "    (HTTP のまま待ち受けている / http.sys に SSL 証明書がバインドされていない など)"
    Write-Host "    Agent 側で 'netstat -ano' と 'netsh http show sslcert' を確認してください。"
    Write-Host "  → 一部だけ OK の場合は、そのプロトコルしか有効になっていません。接続元/接続先双方の"
    Write-Host "    Schannel 設定 (レジストリ) を確認してください。Agent 本体は TLS1.2/1.3 を使用します。"
    exit 1
}

$cipherText = ""
if ($tls.Cipher) { $cipherText = ", 暗号 = $($tls.Cipher)" }
Write-Host "OK: ハンドシェイク成功 (プロトコル = $($tls.Negotiated)$cipherText)"

$chainErrors = $tls.PolicyErrors
if ($chainErrors -eq [System.Net.Security.SslPolicyErrors]::None) {
    Write-Host "OK: 証明書検証に問題なし (AgentForwarder / チェーン転送からの接続も成功するはずです)"
}
else {
    Write-Host "NG: 証明書検証エラー = $chainErrors"
    foreach ($status in $tls.ChainStatus) {
        if ($status.Status -ne 'NoError') {
            Write-Host "  チェーン状態: $($status.Status) - $($status.StatusInformation)"
        }
    }
    if ("$chainErrors" -match "RemoteCertificateNameMismatch") {
        Write-Host "  → 証明書のホスト名 (下記 Subject/SAN) と接続先ホスト名 '$targetHost' が一致していません。"
        if ($isIpLiteral) {
            Write-Host "    IP アドレスで接続しているため、証明書に IP SAN が必要です。DNS 名での接続を推奨します。"
        }
    }
    if ("$chainErrors" -match "RemoteCertificateChainErrors") {
        Write-Host "  → ルート CA がこのマシンで信頼されていないか、中間 CA が不足しています。社内 CA の"
        Write-Host "    ルート証明書を「信頼されたルート証明機関 (LocalMachine)」にインポートしてください。"
        Write-Host "    ※ CurrentUser ではなく LocalMachine です (Agent は LocalSystem で動作するため)。"
    }
}

# --- 3/4 証明書の内容 -------------------------------------------------------

Write-Host ""
Write-Host "=== 3/4 証明書の内容 ==="
$cert = $tls.Certificate
Write-Host "Subject   : $($cert.Subject)"
Write-Host "発行者    : $($cert.Issuer)"
Write-Host "有効期間  : $($cert.NotBefore) ～ $($cert.NotAfter)"
Write-Host "拇印      : $($cert.Thumbprint)"
if ($cert.NotAfter -lt (Get-Date)) { Write-Host "NG: 証明書の有効期限が切れています。" }
$san = $cert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" }
if ($san) {
    $sanText = $san.Format($false)
    Write-Host "SAN       : $sanText"
    if ($sanText -notmatch [Regex]::Escape($targetHost)) {
        Write-Host "  ※ SAN に接続先ホスト名 '$targetHost' が見当たりません。"
    }
}
else {
    Write-Host "SAN       : (なし) ※ SAN の無い証明書は最近のクライアントでは検証に失敗します。"
}

# --- 4/4 /health ------------------------------------------------------------

Write-Host ""
Write-Host "=== 4/4 GET $healthPath (証明書検証は意図的に無視して HTTP 層のみ確認) ==="
if ($tls.HttpError) {
    Write-Host "NG: /health 取得失敗"
    Write-ExceptionChain -Exception $tls.HttpError
    exit 1
}
if (-not $tls.HttpStatus) {
    Write-Host "NG: 応答がありませんでした (TLS は成立しているが HTTP 応答が無い)。"
    Write-Host "  → そのポートで待っているのが Watashi.Agent 以外である可能性があります。"
    exit 1
}
Write-Host "応答      : $($tls.HttpStatus)"
Write-Host "本文      : $($tls.HttpBody)"
if ($tls.HttpStatus -notmatch '^HTTP/1\.[01] 200') {
    Write-Host "NG: 200 以外が返っています。"
    exit 1
}
Write-Host "OK: /health 応答を取得しました。"

# --- 総合判定 ---------------------------------------------------------------

if ($chainErrors -ne [System.Net.Security.SslPolicyErrors]::None) {
    Write-Host ""
    Write-Host "※ /health は取得できましたが、これは証明書検証を無視した結果です。証明書検証エラーが"
    Write-Host "  残っているため実際の転送は失敗します。上記 2/4 の対処を行ってください。"
    exit 1
}

Write-Host ""
Write-Host "すべて OK。ノード管理画面の Endpoint を $AgentUrl に設定できます。"

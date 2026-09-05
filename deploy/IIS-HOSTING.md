# IIS を入口にする Watashi.Server 構築手順

この手順は、Watashi.Server を Windows Service として直接公開するのではなく、IIS を HTTPS の入口として使う構成を一から作る人向けの手順です。

最終形は次の構成です。

```text
Watashi.Client / browser
  -> https://watashi.internal/       または https://watashi.internal:8443/
  -> IIS HTTPS binding
  -> ASP.NET Core Module
  -> Watashi.Server
```

この構成では、HTTPS 証明書は IIS が使います。Watashi.Server の `appsettings.json` には Kestrel 用の HTTPS 証明書設定を書きません。

## この手順の前提

- サーバー OS は Windows Server を想定します。
- Watashi.Server は IIS の ASP.NET Core アプリとして動かします。
- `Watashi.Server` Windows Service は作りません。
- この手順では Client -> Server の HTTPS を IIS で終端します。
- Server -> Agent はまず `Routing:UseMtls=false` + `SharedSecret` を推奨します。
- Server -> Agent でも mTLS を使う場合は、IIS 側のクライアント証明書設定が別途必要になるため、Kestrel 直受け構成の方が単純です。

注意: `watashi.internal` は例です。Let's Encrypt などの公的 CA で証明書を取る場合、原則として公的に検証可能な実ドメインが必要です。社内 ACME / 社内 CA を使う場合は、クライアント PC がその CA を信頼している必要があります。

## 1. IIS をインストールする

管理者 PowerShell を開きます。

```powershell
Install-WindowsFeature Web-Server, Web-Mgmt-Tools, Web-AppInit
```

インストール後、ブラウザでサーバー自身から次を開き、IIS の初期ページが出ることを確認します。

```text
http://localhost/
```

GUI で入れる場合は、Server Manager から `Add roles and features` を開き、`Web Server (IIS)` と `Management Tools` を有効にします。Watashi.Server は常時起動させたいので、可能なら `Application Initialization` も有効にします。

## 2. .NET 8 Hosting Bundle をインストールする

IIS で ASP.NET Core アプリを動かすには、.NET ランタイムだけでなく ASP.NET Core Module も必要です。これは `.NET 8 Hosting Bundle` に含まれます。

1. サーバーで Microsoft 公式の [.NET 8 download page](https://dotnet.microsoft.com/download/dotnet/8.0) を開く
2. `ASP.NET Core Runtime 8.0.x` の `Hosting Bundle` をダウンロードする
3. `dotnet-hosting-8.0.x-win.exe` を管理者として実行する
4. インストール後、IIS を再起動する

```powershell
iisreset
```

確認:

```powershell
dotnet --list-runtimes
```

出力に次のような行があれば OK です。

```text
Microsoft.AspNetCore.App 8.0.x
Microsoft.NETCore.App 8.0.x
```

IIS を Hosting Bundle より後に入れた場合は、Hosting Bundle をもう一度実行して `Repair` してください。ASP.NET Core Module が IIS に登録されないことがあります。

## 3. Watashi.Server を publish する

開発機またはサーバー上で、リポジトリのルートから実行します。

```powershell
dotnet publish .\src\Watashi.Server\Watashi.Server.csproj `
  -c Release `
  -o C:\Sites\Watashi.Server
```

publish 先に次のファイルがあることを確認します。

```powershell
Get-ChildItem C:\Sites\Watashi.Server | Select-Object Name
```

最低限、次が必要です。

- `Watashi.Server.exe`
- `Watashi.Server.dll`
- `appsettings.json`
- `web.config`

`web.config` は IIS の ASP.NET Core Module が使うファイルです。これが無い場合は、別のプロジェクトを publish している可能性があります。

## 4. appsettings.json を IIS 用に書く

IIS 入口構成では、`Kestrel:Endpoints:Https:Certificate` は不要です。空欄にするのではなく、HTTPS endpoint 自体を書かない方が安全です。

推奨は、Watashi.Server の `appsettings.json` から `Kestrel` セクションを削除することです。IIS 上で動かす場合、HTTPS 待受は IIS の binding が担当します。

### 必須の秘密値を作る

`Jwt:Secret` と `Encryption:MasterKey` は空欄にできません。別々の値を生成してください。

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
[Convert]::ToBase64String($bytes)
```

このコマンドを 2 回実行し、片方を `Jwt:Secret`、もう片方を `Encryption:MasterKey` に入れます。

### IIS 用 appsettings.json 例

`C:\Sites\Watashi.Server\appsettings.json` を次の形にします。JSON にはコメントを書けないので、このまま使う場合は値だけ差し替えてください。

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=C:\\ProgramData\\Watashi\\watashi.db;Cache=Shared;Foreign Keys=True;"
  },
  "Jwt": {
    "Secret": "REPLACE-WITH-RANDOM-BASE64-OR-LONG-RANDOM-STRING",
    "Issuer": "Watashi",
    "Audience": "Watashi",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 30
  },
  "Encryption": {
    "MasterKey": "REPLACE-WITH-BASE64-ENCODED-32-BYTE-KEY"
  },
  "Auth": {
    "AllowHttpForAutoLogin": false,
    "LoginPerMinutePerIp": 10
  },
  "Routing": {
    "UseMtls": false,
    "ClientCertificatePath": "",
    "ClientCertificatePassword": "",
    "SharedSecret": "REPLACE-WITH-SAME-LONG-RANDOM-SECRET-AS-AGENT"
  },
  "Cifs": {
    "SessionIdleSeconds": 60,
    "MaxSessionsPerKey": 4
  },
  "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.File" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "C:\\ProgramData\\Watashi\\logs\\server-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 14,
          "shared": true
        }
      }
    ]
  },
  "AllowedHosts": "*"
}
```

### appsettings の空欄ルール

| キー | 空欄でよいか | 説明 |
|---|---:|---|
| `ConnectionStrings:Default` | いいえ | SQLite DB の保存先です。IIS App Pool が書き込める場所にします。 |
| `Jwt:Secret` | いいえ | Production では初期値のままだと起動拒否されます。 |
| `Encryption:MasterKey` | いいえ | CIFS 資格情報の暗号化に使います。32 バイト Base64 が必要です。 |
| `Auth:AllowHttpForAutoLogin` | いいえ | IIS 経由でも外側は HTTPS なので `false` のままでよいです。 |
| `Routing:UseMtls` | いいえ | この IIS 手順ではまず `false` 推奨です。 |
| `Routing:ClientCertificatePath` | はい | `UseMtls=false` なら空欄でよいです。 |
| `Routing:ClientCertificatePassword` | はい | `UseMtls=false` なら空欄でよいです。 |
| `Routing:SharedSecret` | Agent を使うならいいえ | Agent と同じ長いランダム値にします。Agent を使わない検証だけなら空欄でも起動はします。 |
| `Kestrel` | はい | IIS ホストでは不要です。HTTPS 証明書設定は IIS binding に持たせます。 |

古い設定から移行する場合、次のような設定は IIS 用 appsettings から削除します。

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": {
        "Subject": "watashi.internal",
        "Store": "WebHosting",
        "Location": "LocalMachine",
        "AllowInvalid": false
      }
    }
  }
}
```

`Certificate` を空の `{}` にするのではなく、`Kestrel` ごと消すのが分かりやすいです。

## 5. IIS App Pool を作る

IIS Manager を開きます。

```powershell
inetmgr
```

GUI 手順:

1. 左ツリーのサーバー名を開く
2. `Application Pools` を右クリック
3. `Add Application Pool...`
4. Name: `Watashi.Server`
5. .NET CLR version: `No Managed Code`
6. Managed pipeline mode: `Integrated`
7. OK

作成後、App Pool の `Advanced Settings...` を開き、次を設定します。

| 項目 | 値 | 理由 |
|---|---|---|
| `Start Mode` | `AlwaysRunning` | 初回アクセス前に起動させるため |
| `Idle Time-out (minutes)` | `0` | アイドル停止で HostedService が止まるのを避けるため |
| `Identity` | `ApplicationPoolIdentity` | 標準の安全な実行ユーザー |

PowerShell で作る場合:

```powershell
Import-Module WebAdministration

New-WebAppPool -Name "Watashi.Server"
Set-ItemProperty "IIS:\AppPools\Watashi.Server" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\Watashi.Server" -Name startMode -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\Watashi.Server" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
```

## 6. フォルダ権限を設定する

IIS の App Pool は既定では `IIS AppPool\<AppPool名>` という仮想アカウントで動きます。ここでは App Pool 名を `Watashi.Server` にしたので、`IIS AppPool\Watashi.Server` に権限を付けます。

DB とログ用フォルダを作ります。

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\Watashi
New-Item -ItemType Directory -Force C:\ProgramData\Watashi\logs
```

publish フォルダは読み取り、ProgramData は読み書きできるようにします。

```powershell
icacls "C:\Sites\Watashi.Server" /grant "IIS AppPool\Watashi.Server:(OI)(CI)RX"
icacls "C:\ProgramData\Watashi" /grant "IIS AppPool\Watashi.Server:(OI)(CI)M"
```

`(OI)(CI)M` は、配下のファイル/フォルダに変更権限を付ける指定です。SQLite DB とログを書き込むために必要です。

## 7. IIS サイトを作る

GUI 手順:

1. IIS Manager で `Sites` を右クリック
2. `Add Website...`
3. Site name: `Watashi.Server`
4. Application pool: `Watashi.Server`
5. Physical path: `C:\Sites\Watashi.Server`
6. Binding はまず HTTP で作る
   - Type: `http`
   - IP address: `All Unassigned`
   - Port: `80`
   - Host name: `watashi.internal`
7. OK

作成後、サイトの `Advanced Settings...` を開き、次を設定します。

| 項目 | 値 |
|---|---|
| `Preload Enabled` | `True` |

PowerShell で作る場合:

```powershell
New-Website `
  -Name "Watashi.Server" `
  -ApplicationPool "Watashi.Server" `
  -PhysicalPath "C:\Sites\Watashi.Server" `
  -Port 80 `
  -HostHeader "watashi.internal"

Set-ItemProperty "IIS:\Sites\Watashi.Server" -Name applicationDefaults.preloadEnabled -Value $true
```

この時点で、サーバー自身から次を確認します。

```powershell
Invoke-RestMethod http://watashi.internal/health
```

この HTTP 確認は、IIS サイトが起動するかを見るための一時確認です。Watashi.Server は通常ログイン API も HTTP で受けられるため、この HTTP binding をそのまま LAN に公開したままにしないでください。HTTPS 設定後に HTTP binding を削除するか、HTTP から HTTPS へリダイレクトする設定にします。

DNS がまだ無い場合は、一時的にサーバー自身の `hosts` に書いて確認します。

```powershell
notepad C:\Windows\System32\drivers\etc\hosts
```

例:

```text
127.0.0.1 watashi.internal
```

## 8. HTTPS binding を作る

IIS で HTTPS を受けるには、IIS サイトに HTTPS binding を追加します。

すでに IIS で選べる証明書がある場合は、この章の GUI 手順で binding を作ります。まだ証明書が無い場合は、この章はいったん飛ばして [9. Win-ACME で証明書を取る](#9-win-acme-で証明書を取る) に進んでください。Win-ACME の IIS installation で HTTPS binding を作成または更新できます。

GUI 手順:

1. IIS Manager で `Watashi.Server` サイトを選ぶ
2. 右側の `Bindings...` を開く
3. `Add...`
4. Type: `https`
5. IP address: `All Unassigned`
6. Port: `443` または `8443`
7. Host name: `watashi.internal`
8. `Require Server Name Indication` を有効化
9. SSL certificate: 対象ホスト名の証明書を選ぶ
10. OK

通常は `443` が分かりやすいです。`8443` を使う場合、クライアントの ServerUrl は `https://watashi.internal:8443` にします。

確認:

```powershell
curl.exe https://watashi.internal/health
```

`8443` の場合:

```powershell
curl.exe https://watashi.internal:8443/health
```

HTTPS が動いたら、平文 HTTP を無効化します。もっとも単純なのは HTTP binding を削除する方法です。

GUI 手順:

1. IIS Manager で `Watashi.Server` サイトを選ぶ
2. `Bindings...` を開く
3. `http / 80 / watashi.internal` を選ぶ
4. `Remove`
5. OK

PowerShell で行う場合:

```powershell
Remove-WebBinding `
  -Name "Watashi.Server" `
  -Protocol "http" `
  -Port 80 `
  -HostHeader "watashi.internal"
```

HTTP から HTTPS へリダイレクトしたい場合は、IIS の HTTP Redirect / URL Rewrite などで `http://watashi.internal/*` を `https://watashi.internal/*` へ転送します。どちらの場合も、`http://watashi.internal/api/auth/login` のような通常 API が平文で使える状態を残さないでください。

注意: ACME HTTP-01 検証を使う場合、証明書の更新時に port 80 で `/.well-known/acme-challenge/` へ到達できる必要があります。HTTP binding を削除する運用にするなら DNS-01 検証を使う、更新時だけ一時的に HTTP を開ける、または ACME challenge だけ例外的に通すリダイレクト設定にしてください。

## 9. Win-ACME で証明書を取る

Win-ACME を使う場合は、IIS サイトを作った後に実行すると分かりやすいです。

1. [win-acme](https://www.win-acme.com/) をダウンロードする
2. `C:\Program Files\win-acme` など、消さない場所に展開する
3. 管理者 PowerShell で起動する

```powershell
cd "C:\Program Files\win-acme"
.\wacs.exe
```

基本の流れ:

1. `N` Create new certificate を選ぶ
2. IIS のサイト一覧から `Watashi.Server` を選ぶ
3. 対象ホストとして `watashi.internal` 相当のホスト名を選ぶ
4. Installation は IIS binding を選ぶ
5. 完了後、IIS の HTTPS binding に証明書が入ったことを確認する

この流れなら、手動で証明書をインポートしたり、Watashi.Server の `appsettings.json` に証明書設定を書いたりする必要はありません。

Win-ACME は初回成功後、自動更新用のタスクスケジューラを作ります。IIS binding 構成では、更新時に IIS binding も新しい証明書へ更新されます。

確認:

```powershell
Get-ScheduledTask |
  Where-Object { $_.TaskName -like "*win-acme*" -or $_.TaskPath -like "*win-acme*" } |
  Select-Object TaskName, TaskPath, State
```

この IIS 入口構成では、Watashi.Server が証明書を直接保持しないため、Kestrel 直受け構成で必要だった `Restart-Service Watashi.Server` の証明書更新フックは基本不要です。IIS 側で TLS が終端されるためです。

ただし、アプリ更新や設定変更時は IIS App Pool の再起動が必要です。

```powershell
Restart-WebAppPool Watashi.Server
```

## 10. ファイアウォールと DNS

公開するポートだけ開けます。

443 を使う場合:

```powershell
New-NetFirewallRule `
  -DisplayName "Watashi HTTPS 443" `
  -Direction Inbound `
  -Action Allow `
  -Protocol TCP `
  -LocalPort 443
```

8443 を使う場合:

```powershell
New-NetFirewallRule `
  -DisplayName "Watashi HTTPS 8443" `
  -Direction Inbound `
  -Action Allow `
  -Protocol TCP `
  -LocalPort 8443
```

DNS では、クライアント PC から `watashi.internal` が IIS サーバーの IP に解決されるようにします。

```powershell
nslookup watashi.internal
```

## 11. Client 側の接続 URL

IIS の HTTPS binding が 443 の場合:

```text
https://watashi.internal
```

IIS の HTTPS binding が 8443 の場合:

```text
https://watashi.internal:8443
```

Watashi.Client の配布物に同梱する `deployment.json` の `serverUrl` にこの URL を入れます (発行前に編集、利用者は変更不可)。

例:

```json
{
  "serverUrl": "https://watashi.internal",
  "updateManifestUrl": "https://watashi.internal/install/Watashi.Client.application",
  "enableDragDrop": true
}
```

`Auth:AllowHttpForAutoLogin` は `false` のままで構いません。IIS/ASP.NET Core Module 経由のホストでは、外側が HTTPS ならアプリ側でも HTTPS として扱われます。

## 11.5 Windows 統合認証を有効にする (初回パスワード設定)

同梱 `appsettings.json` ではこの機能を `Mode=IIS` で有効にしています。IIS 側の認証設定は [IIS Windows 認証セットアップスクリプト](configure-iis-windows-auth.ps1) で適用でき、変更箇所・実行・確認・復元は [HTML ガイド](IIS-WINDOWS-AUTH-SETUP.html) にまとめています。機能を使わない場合は `Mode=None` にし、従来どおり管理者が「🔑 初期PW発行」を実行します。

有効にすると、利用者が Watashi に GID を入力した時点で Windows のログオン情報による本人確認が行われ、**本人が自分で初回パスワードを決められる**ようになります。管理者が初期パスワードを配布する手順がなくなります。

前提: 「Windows のログオンユーザー名 = 社内 GID = Watashi のユーザー名」という運用ルールが成立していること。

### 11.5.1 IIS の Windows 認証機能を入れる

```powershell
Install-WindowsFeature Web-Windows-Auth
```

### 11.5.2 IIS は両認証を有効にし、要求範囲はアプリで限定する

IIS のサイト／アプリでは、**匿名認証と Windows 認証を両方とも有効**にします。匿名認証を有効にしておくため、ポータル・`/install/` (ClickOnce)・`/manual/` はドメイン非参加 PC からも開けます。

Windows 認証を必須にする範囲は Watashi.Server の `WindowsSetup` 認可ポリシーが `/api/auth/win/*` だけに限定します。通常 API は従来どおり匿名または JWT の認可規則を使います。

```powershell
Import-Module WebAdministration

$site = "Watashi.Server"

# 匿名アクセスを維持したまま、アプリから Windows 認証を要求できるようにする
Set-WebConfigurationProperty -PSPath "IIS:\" -Location $site `
  -Filter "/system.webServer/security/authentication/anonymousAuthentication" -Name enabled -Value $true
Set-WebConfigurationProperty -PSPath "IIS:\" -Location $site `
  -Filter "/system.webServer/security/authentication/windowsAuthentication" -Name enabled -Value $true
```

⚠️ **IIS に `Watashi.Server/api/auth/win` の場所別Locationを作成しないでください。** 公開時の `web.config` は `aspNetCore` ハンドラーをアプリのLocation内に設定します。別の場所別Locationを仮想APIパスに作ると、環境によってそのパスで `aspNetCore` が選ばれず、IISが `StaticFile` ハンドラーで `publish\api\auth\win\...` という物理ファイルを探して `404.0` を返します。

旧版スクリプトを実行済みの環境では、最新版を再実行してください。旧版の認証セクションを消し、空になった場所別Locationを安全に削除します。認証以外の設定が同じLocationに残っている場合は、スクリプトは勝手に削除せず停止します。

> この設定を `web.config` に書く方法もありますが、`system.webServer/security/authentication` は既定でサーバーレベルにロックされているため、そのままだと `500.19` になります。上記のように applicationHost.config 側 (`-PSPath "IIS:\" -Location $site`) に書けばロック解除は不要です。

### 11.5.3 appsettings.json に設定を足す

```json
"Auth": {
  "AllowHttpForAutoLogin": false,
  "LoginPerMinutePerIp": 10,
  "WindowsAuth": {
    "Mode": "IIS",
    "DomainMatch": "IgnoreDomain",
    "AllowedDomains": [],
    "EnableDiagnostics": true,
    "SetupPerMinutePerIp": 30
  }
}
```

| キー | 既定 | 説明 |
|---|---|---|
| `Mode` | `IIS` | `None` = 機能オフ。IIS ホストなら `IIS`、Kestrel 直受けなら `Negotiate`。設定自体が無い場合のコード既定値は `None` |
| `AllowHttp` | `false` | HTTP でも初回設定を許すか。ローカル開発専用。本番では `false` のまま |
| `DomainMatch` | `IgnoreDomain` | `IgnoreDomain` = ドメイン部を見ず GID だけで照合。`AllowList` = 許可ドメインからのみ受け付ける |
| `AllowedDomains` | `[]` | `AllowList` のときに許可するドメイン (NetBIOS 名・DNS 名どちらでも)。空のままだと全て拒否されます |
| `EnableDiagnostics` | `true` | `GET /api/auth/win/whoami` を有効にする。導入確認が済み、不要なら `false` に戻す |
| `SetupPerMinutePerIp` | `30` | 初回設定エンドポイントの IP あたり毎分許可数 |

複数ドメインが混在する環境で、別ドメインの同名アカウントによる乗っ取りを防ぎたい場合は `AllowList` にします。

> `AllowList` は「受け付けるドメイン」を絞る設定であり、Watashi ユーザーごとにドメインを割り当てる設定ではありません。
> 複数の独立したアカウントドメインを同時に許可する場合は、GID が全許可ドメインを通して一意であることを確認してください。
> 同じ GID が複数の許可ドメインに存在し得る構成では Windows 初回設定を使わず、管理者による初期 PW 発行経路を使用してください。

```json
"DomainMatch": "AllowList",
"AllowedDomains": [ "CORP", "corp.example.com" ]
```

設定後、アプリプールを再起動します。

```powershell
Restart-WebAppPool Watashi.Server
```

### 11.5.4 Kerberos の SPN

IIS のホストヘッダ (`watashi.internal`) がサーバーのマシン名と異なる場合、Kerberos が成立するにはその名前の SPN が必要です。無い場合は NTLM にフォールバックします (動作はしますが Kerberos より弱い)。

```powershell
# 現状確認
setspn -Q HTTP/watashi.internal

# 無ければマシンアカウントに登録 (ドメイン管理者権限が必要)
setspn -S HTTP/watashi.internal <サーバーのコンピューター名>
```

App Pool をドメインユーザー ID で動かしている場合は、マシンアカウントではなくそのユーザーに登録し、サイトの `useAppPoolCredentials` を `true` にします。

### 11.5.5 受け入れ確認

導入後、以下を順に確認してください。**6 が最重要です。**

| # | 確認 | 期待 |
|---|---|---|
| 1 | ドメイン非参加 PC から `https://watashi.internal/install/publish.htm` を開く | 認証を求められず表示される |
| 2 | `curl.exe -s -o NUL -w "%{http_code}" https://watashi.internal/health` | `200` |
| 3 | `curl.exe -s -i https://watashi.internal/api/auth/win/whoami` | `401` + `WWW-Authenticate: Negotiate` |
| 4 | `curl.exe -s --negotiate -u : https://watashi.internal/api/auth/win/whoami` | 自分の `DOMAIN\GID` が返り、`normalizedName` が GID、`domainAllowed` が `true` |
| 5 | 通常ログイン (`POST /api/auth/login`) | 従来どおり成功する |
| 6 | **JWT を付けずに** `curl.exe -s -o NUL -w "%{http_code}" --negotiate -u : https://watashi.internal/api/hosts` | **`401`**。`200` なら通常 API の認可が緩んでいるため、Watashi.Server の認証設定を見直す |
| 7 | テスト用ユーザーを 1 件作り、本人の PC から初回設定を通す | 「初回パスワードの設定」画面が出て、設定後そのままログインできる |

確認が済んだら `EnableDiagnostics` を `false` に戻してアプリプールを再起動します。

### 11.5.6 使えない構成

- **ARR などの単純なリバースプロキシ経由**: Negotiate は接続単位の認証なのでプロキシを越えられません。この手順の「IIS の ASP.NET Core Module でホストする」方式を使ってください。
- **HTTP/2**: Negotiate は HTTP/2 では成立しません。Watashi.Client は HTTP/1.1 で接続するため通常は問題ありません。

## 12. 動作確認

サーバー自身で確認:

```powershell
curl.exe https://watashi.internal/health
```

別 PC から確認:

```powershell
curl.exe https://watashi.internal/health
```

期待値:

```json
{"status":"ok","at":"..."}
```

ログ確認:

```powershell
Get-ChildItem C:\ProgramData\Watashi\logs
Get-Content C:\ProgramData\Watashi\logs\server-*.log -Tail 100
```

IIS 側のログ:

```text
C:\inetpub\logs\LogFiles
```

Windows Event Viewer も確認対象です。

```powershell
eventvwr.msc
```

## 13. アプリを更新する手順

新しいバージョンを publish したら、次の流れで入れ替えます。

```powershell
$settings = "C:\Sites\Watashi.Server\appsettings.json"
$backup = "C:\Sites\Watashi.Server\appsettings.json.$(Get-Date -Format yyyyMMddHHmmss).bak"
Copy-Item $settings $backup -ErrorAction Stop

Stop-WebAppPool Watashi.Server

dotnet publish .\src\Watashi.Server\Watashi.Server.csproj `
  -c Release `
  -o C:\Sites\Watashi.Server

Copy-Item $backup $settings -Force

Start-WebAppPool Watashi.Server
```

重要: `dotnet publish -o C:\Sites\Watashi.Server` は、プロジェクト側の `appsettings.json` で本番設定を上書きする可能性があります。必ず publish 前にバックアップし、publish 後に本番用 `appsettings.json` を戻してください。より安全にするなら、一度別フォルダへ publish してから必要なファイルだけを配備します。

## 14. よくあるトラブル

### 500.30 / 502.5 が出る

ASP.NET Core アプリが起動に失敗しています。

確認:

```powershell
Get-Content C:\ProgramData\Watashi\logs\server-*.log -Tail 100
```

ログが出ていない場合は、IIS App Pool が `C:\ProgramData\Watashi` に書けていない可能性があります。

```powershell
icacls "C:\ProgramData\Watashi"
```

### `Jwt:Secret` が初期値のままで起動しない

Production では `CHANGE-ME` から始まる初期値だと起動拒否されます。`Jwt:Secret` をランダム値に変更してください。

### `Encryption:MasterKey` でエラーになる

`Encryption:MasterKey` は Base64 で、デコード後 32 バイトである必要があります。

確認:

```powershell
$key = "<appsettings.json の Encryption:MasterKey>"
$bytes = [Convert]::FromBase64String($key)
$bytes.Length
```

`32` と表示されれば OK です。

### 自動ログインや「この PC を記憶」が 403 になる

IIS ホスト方式なら通常は `Request.IsHttps` が HTTPS として扱われます。403 になる場合は、IIS ではなく別のリバースプロキシ経由にしていないか確認してください。

このドキュメントの方式は IIS の ASP.NET Core Module でホストする方式です。ARR などで単純なリバースプロキシにする方式とは別です。

### 証明書更新後に Watashi.Server を再起動する必要があるか

この IIS 入口構成では通常不要です。証明書は IIS binding に紐づくため、Watashi.Server は証明書を読みません。

ただし、Kestrel 直受け構成に戻して `Kestrel:Endpoints:Https:Certificate` を使う場合は、証明書更新後に Watashi.Server の再起動が必要です。

### `https://watashi.internal` が証明書エラーになる

次を確認します。

- IIS binding の Host name が `watashi.internal` になっている
- 証明書の SAN に `watashi.internal` が入っている
- クライアント PC が証明書の発行 CA を信頼している
- Let's Encrypt を使う場合、公的に有効なドメイン名を使っている

### App Pool が止まる / 初回アクセスが遅い

次を確認します。

- App Pool の `Start Mode` が `AlwaysRunning`
- App Pool の `Idle Time-out` が `0`
- Site の `Preload Enabled` が `True`
- `Web-AppInit` がインストール済み

## 参考リンク

- [Host ASP.NET Core on Windows with IIS](https://learn.microsoft.com/aspnet/core/host-and-deploy/iis/)
- [ASP.NET Core Module with IIS](https://learn.microsoft.com/aspnet/core/host-and-deploy/aspnet-core-module)
- [.NET 8 downloads](https://dotnet.microsoft.com/download/dotnet/8.0)
- [win-acme automatic renewal](https://www.win-acme.com/manual/automatic-renewal)
- [win-acme IIS installation plugin](https://www.win-acme.com/reference/plugins/installation/iis)

## 独立したメンテナンス案内サイト

API停止中にも案内する場合は、APIと別のIISサイト・アプリケーションプールに静的ページとJSONを配置します。ホスト再起動を含む場合は別ホストに分離します。Serverの公開先設定と書き込み／読み取り権限、ClientのmaintenanceStatusUrlと再発行、状態切替・更新・切り戻しは[メンテナンス導入・運用手順](MAINTENANCE-ROLLOUT-PLAN.html)を参照してください。同梱スクリプトはIIS・DNS・証明書・ACLを自動変更しません。

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

Watashi.Client の接続設定、または ClickOnce 配布用の `watashi-config.json` にはこの URL を入れます。

例:

```json
{
  "serverUrl": "https://watashi.internal"
}
```

`Auth:AllowHttpForAutoLogin` は `false` のままで構いません。IIS/ASP.NET Core Module 経由のホストでは、外側が HTTPS ならアプリ側でも HTTPS として扱われます。

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
Stop-WebAppPool Watashi.Server

dotnet publish .\src\Watashi.Server\Watashi.Server.csproj `
  -c Release `
  -o C:\Sites\Watashi.Server

Start-WebAppPool Watashi.Server
```

重要: `appsettings.json` を publish で上書きしないように注意してください。事前にバックアップしておくと安全です。

```powershell
Copy-Item C:\Sites\Watashi.Server\appsettings.json C:\Sites\Watashi.Server\appsettings.json.bak
```

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

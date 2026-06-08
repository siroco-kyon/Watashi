# Watashi 環境構築ガイド

開発機での 5 分セットアップから、本番デプロイまでを通しで説明します。

- [前提条件](#前提条件)
- [① 5 分で動かす (開発機での動作確認)](#-5-分で動かす-開発機での動作確認)
- [② ネットワーク構成](#-ネットワーク構成) ← Client↔Server↔Agent の HTTPS/HTTP 組み合わせ
- [③ 本番デプロイ](#-本番デプロイ)
  - [③-1 中央サーバー](#-1-中央サーバー-watashiserver)
  - [③-2 配布サーバー (ClickOnce)](#-2-配布サーバー-clickonce--iis)
  - [③-3 エージェント (踏み台)](#-3-エージェント-踏み台)
- [④ 初期データ登録](#-初期データ登録)
- [⑤ 本番投入チェックリスト](#-本番投入チェックリスト)
- [⑥ 運用 (バックアップ / ログ削除 / 監視)](#-運用)
- [トラブルシューティング](#トラブルシューティング)

---

## 前提条件

| 項目 | 要件 |
|---|---|
| OS (Server / Agent) | Windows Server 2019 以降（Windows Service として常駐） |
| OS (Client) | Windows 10 / 11 |
| .NET ランタイム | 8.0 (Server / Agent / Client いずれも `net8.0`、self-contained 発行ならランタイム不要) |
| ネットワーク | 閉域網 (社内 LAN / VPN 内) を想定 |
| 同時接続 | 50 ユーザー程度を想定 |
| 権限 | サービス登録には管理者権限の PowerShell が必要 |

---

## ① 5 分で動かす (開発機での動作確認)

CIFS サーバーが無くてもログイン / 認証 / DB 自動生成までは確認できます。

```powershell
# 1) 中央サーバー起動 (PowerShell でも cmd でも可)
cd src\Watashi.Server
dotnet run
# → http://127.0.0.1:18080 で起動
# → ASPNETCORE_ENVIRONMENT=Development は launchSettings.json で自動設定されるので、env 変数を手動指定する必要なし
# (もし手動で指定したい場合は: PowerShell なら $env:ASPNETCORE_ENVIRONMENT="Development"、cmd なら set ASPNETCORE_ENVIRONMENT=Development)
```

別ウィンドウで:

```powershell
# 2) 疎通確認 (ブラウザで http://127.0.0.1:18080/ を開くとエンドポイント一覧が見える)
Invoke-RestMethod http://127.0.0.1:18080/health
# → {"status":"ok","at":"..."}

# 3) ログイン (admin / Admin123!@#)
$body = @{ username = 'admin'; password = 'Admin123!@#' } | ConvertTo-Json
$login = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18080/api/auth/login `
    -ContentType 'application/json' -Body $body
$login.mustChangePassword   # → True (初回はパスワード変更が必要)
$login.accessToken          # → JWT
```

> **PowerShell の注意**: `curl` は `Invoke-WebRequest` のエイリアスなので `curl -X POST -H ...` のような Unix 構文は通らない。
> Unix 系の例を使いたい場合は `curl.exe` を明示（Windows 10 以降に同梱されている本物の curl が呼ばれる）:
> ```powershell
> curl.exe -X POST http://127.0.0.1:18080/api/auth/login `
>     -H "Content-Type: application/json" `
>     -d '{\"username\":\"admin\",\"password\":\"Admin123!@#\"}'
> ```

```powershell
# 4) WPF クライアント起動
cd ..\Watashi.Client
# 接続先は同梱 deployment.json で固定 (既定 https://watashi.internal)。
# dev では serverUrl を http://127.0.0.1:18080 に書き換えてから起動する。
dotnet run
# → ログイン: admin / Admin123!@# → パスワード変更画面 → メイン画面
```

> 開発時のポート (18080) は `src\Watashi.Server\appsettings.Development.json` の `Kestrel:Endpoints:Http:Url` で設定されている。
> 同ファイルには HTTPS endpoint (`https://localhost:18443`) も併設してあるので、dev-cert を信頼すれば HTTPS でも検証可能。手順は **[DEVELOPMENT.md#https-開発環境のセットアップ](DEVELOPMENT.md#https-開発環境のセットアップ)** 参照。
> 本番は `appsettings.json` 側で `Https:Url=https://0.0.0.0:8443` を使用する想定。

ホストが未登録ならリモートペインは空。
ホスト登録手順は [ADMIN-GUIDE.md](ADMIN-GUIDE.md) を参照（実 CIFS サーバーが必要）。

DB ファイルは:
- 開発: `src\Watashi.Server\watashi-dev.db`
- 本番: `C:\ProgramData\Watashi\watashi.db`

---

## ② ネットワーク構成

通信経路は 2 区間あり、それぞれ独立に HTTP/HTTPS を選べます。

```
[Client] ── 区間 A ── [Server] ── 区間 B ── [Agent] ── SMB ── [CIFS]
              ↑                     ↑
        Client が選択         Node ごとに選択（mTLS は推奨）
```

### サポートする 3 つのモード

| モード | 区間 A (Client↔Server) | 区間 B (Server↔Agent) | mTLS | 想定環境 |
|---|---|---|---|---|
| **A: 全 HTTPS + mTLS (推奨)** | HTTPS | HTTPS | ON | 本番、踏み台が別セキュリティゾーン |
| **B: 混在** | HTTPS | HTTP + 共有秘密 | OFF | Server と Agent が同一信頼ゾーン |
| **C: 全 HTTP** | HTTP | HTTP | OFF | 検証 / 内部ラボ専用 |

> 機密データ（CIFS 資格情報、ファイル内容）がネットワークを流れるため、Client↔Server は HTTPS 必須。
> Server↔Agent が社内 LAN 内で完結する場合は HTTP でも実用上問題ないが、PCI / 個人情報など規制対象は mTLS にすること。
> モード B でも **`Routing:SharedSecret`** を設定すれば、Server と Agent は X-Watashi-Secret ヘッダで相互に相手を識別できる。

### モード A: 全 HTTPS + mTLS

**Server `appsettings.json`**:
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "server.pfx", "Password": "..." }
      }
    }
  },
  "Routing": {
    "UseMtls": true,
    "ClientCertificatePath": "central-client.pfx",
    "ClientCertificatePassword": "..."
  }
}
```

> `UseMtls=true` のときはサーバ Kestrel が `ClientCertificateMode.AllowCertificate` で起動し、
> `/api/internal/*` は `Agent` ポリシーで証明書サムプリント検証が必須になる。
> サムプリントは `ExecutionNode.ClientCertificateThumbprint` と照合される。

**Agent `appsettings.json`**:
```json
{
  "Agent": {
    "AgentId": "bastion-a",
    "CentralUrl": "https://central.internal:8443",
    "MaxConcurrency": 20
  },
  "Certificate": {
    "Path": "agent.pfx",
    "Password": "..."
  },
  "Auth": {
    "CentralCertificateThumbprint": "<中央サーバが Agent へ提示するクライアント証明書 Thumbprint>",
    "SharedSecret": ""
  },
  "Routing": { "UseMtls": true },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "agent.pfx", "Password": "..." }
      }
    }
  }
}
```

> Agent inbound は `/agent/*` が `CentralOrSharedSecret` ポリシーで保護されており、
> 中央クライアント証明書の Thumbprint が `Auth:CentralCertificateThumbprint` と一致すれば許可される。

**中央 DB の ExecutionNodes**:
```
Endpoint = https://bastion-a:8443
ClientCertificateThumbprint = <Agent が提示するクライアント証明書 Thumbprint>
```

**Client の接続先 (同梱 `deployment.json`、発行前に編集)**:
```jsonc
// exe と同じ場所。利用者は変更不可
{ "serverUrl": "https://watashi.internal:8443", "enableDragDrop": true }
```

### モード B: 混在 (Client↔Server は HTTPS、Server↔Agent は HTTP)

**現実的な落としどころ**。中央と踏み台が同一データセンタ / 同一信頼ゾーンで、クライアント PC は事務 LAN / VPN 越し。

**Server `appsettings.json`** (mTLS 無効):
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "server.pfx", "Password": "..." }
      }
    }
  },
  "Routing": {
    "UseMtls": false,
    "SharedSecret": "<32 バイト以上のランダム値、Agent と同じ値 (生成方法は本ページ「Jwt:Secret / Encryption:MasterKey の生成方法」参照)>"
  }
}
```

**Agent `appsettings.json`**:
```json
{
  "Agent": {
    "AgentId": "bastion-a",
    "CentralUrl": "https://central.internal:8443",
    "MaxConcurrency": 20
  },
  "Auth": {
    "CentralCertificateThumbprint": "",
    "SharedSecret": "<Server と同じ値>"
  },
  "Routing": { "UseMtls": false },
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://0.0.0.0:8081" } }
  }
}
```

> `Auth:SharedSecret` を両側で設定すると、Agent は `X-Watashi-Secret` ヘッダで中央サーバを認証する。
> 中央サーバも Agent からのハートビート / ログ同期を同じ共有秘密で認証する。
> mTLS が使えない場合の最低限の保護。

**中央 DB の ExecutionNodes**:
```
Endpoint = http://bastion-a:8081
ClientCertificateThumbprint = (空でよい)
```

### モード C: 全 HTTP

検証・ラボでのみ使う。本番では使わないこと。
Server `appsettings.json` から `Https` セクションを削除し `Http` のみ残す。Agent もすべて HTTP。Client は HTTP で接続。

**注意:** HTTP モードでも **クライアントの「このPCを記憶する」(信頼デバイス) は利用可能** だが、デバイストークン交換が平文で流れる。Production の `appsettings.json` で `Auth:AllowHttpForAutoLogin` を `true` にしないと、サーバーが HTTP の auto-login を 401 で拒否する。Dev は既に `true`。

### 双方向は許可不要

中央サーバーは Agent への HTTP/HTTPS 接続を **発信側** として行う。
踏み台ファイアウォールに Agent ポート（例: 8081 / 8443）を中央サーバー IP からのみ許可すれば足りる。
Agent → 中央 はハートビート / ログ送信に限定。

### ポート番号の変更 (任意のポートに変更可)

Server / Agent ともに **`Kestrel:Endpoints` の URL の `:port` を変えるだけ** でポート変更可能。

**Server** (`appsettings.json` / `appsettings.Development.json`):
```json
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://0.0.0.0:8080" },   // ← HTTP ポート
    "Https": { "Url": "https://0.0.0.0:8443" }   // ← HTTPS ポート
  }
}
```

**Agent** (`appsettings.json`):
```json
"Kestrel": {
  "Endpoints": {
    "Http": { "Url": "http://0.0.0.0:8081" }     // ← Agent inbound ポート
  }
}
```

**よくあるケース**:
- **80 / 443 が IIS や Skype 等で使われている** → 8080 / 8443 / 18080 / 18443 など空きポートに変更
- **同一マシンで複数 Watashi インスタンス起動** → 各 appsettings.json で別ポートに割当
- **社内ファイアウォールが特定範囲のみ許可** → その範囲内のポートに合わせる

**確認方法** (起動前):
```powershell
netstat -ano | findstr :8080      # 該当ポートが使用中か
Get-NetTCPConnection -LocalPort 8080 -ErrorAction SilentlyContinue
```

**起動に失敗するパターン**:
```
crit: Microsoft.AspNetCore.Server.Kestrel[0]
      Unable to start Kestrel.
System.IO.IOException: Failed to bind to address http://0.0.0.0:80: address already in use.
```
→ 上記のとおり別ポートに変更してください。

**ExecutionNode.Endpoint も合わせて更新**: 中央 DB の Agent ノードの Endpoint (`http://bastion-a:8081` 等) に Agent のポートを反映させること。

---

## ③ 本番デプロイ

### ③-1 中央サーバー (Watashi.Server)

**ビルド (CI/開発機で実施)**:
```powershell
dotnet publish src\Watashi.Server\Watashi.Server.csproj `
    -c Release -r win-x64 --self-contained `
    -o D:\publish\WatashiServer
```

**サーバーでの設定** (`appsettings.json`):

```jsonc
{
  "ConnectionStrings": {
    // ローカル SSD 必須。ネットワーク共有は不可
    "Default": "Data Source=C:\\ProgramData\\Watashi\\watashi.db;Cache=Shared;Foreign Keys=True;"
  },
  "Jwt": {
    // 32 バイト以上のランダム値に必ず変更。Production で "CHANGE-ME" のままだと起動拒否
    "Secret": "<生成方法は下記参照、48 バイトの Base64 推奨>",
    "Issuer": "Watashi",
    "Audience": "Watashi",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 30
  },
  "Encryption": {
    // 32 バイトの Base64 (= 44 文字)。CIFS パスワード暗号化に使用
    // 紛失すると全 CIFS 資格情報が復号不能になるので厳重管理
    "MasterKey": "<生成方法は下記参照>"
  },
  "Auth": {
    "AllowHttpForAutoLogin": false,
    // IP 単位のログインレート制限 (回/分)
    "LoginPerMinutePerIp": 10
  },
  "Cifs": {
    // SMB セッションプールの挙動
    "SessionIdleSeconds": 60,
    "MaxSessionsPerKey": 4
  },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "C:\\Apps\\Watashi\\server.pfx", "Password": "..." }
      }
    }
  },
  // モード A の場合のみ
  "Routing": {
    "UseMtls": true,
    "ClientCertificatePath": "C:\\Apps\\Watashi\\central-client.pfx",
    "ClientCertificatePassword": "...",
    "SharedSecret": ""
  }
}
```

> **HTTPS 証明書の指定方法は 2 通り**: 上記は PFX ファイル指定。社内 PKI から配布された証明書 (=IIS のバインドで選んでいる証明書) を **ファイル化せずそのまま使う** こともでき、Windows 上では一般にこちらの方が運用が楽です。具体的な手順 (証明書ストア参照モードへの appsettings.json 書き換え、秘密キーへのアクセス権付与、エクスポート可否の判定、証明書更新時の流れ) は **[deploy/CERTIFICATE.md](../deploy/CERTIFICATE.md)** を参照。

> 起動時検証: `Jwt:Secret` が `CHANGE-ME` で始まり `ASPNETCORE_ENVIRONMENT=Production` の場合は例外で起動拒否。
> 同様に `Encryption:MasterKey` も `REPLACE-WITH` プレフィックス検出。Dev/Stg では警告のみ。
> なお `Encryption:MasterKey` は **Base64 でデコードして 32 バイトになる値** が必須。プレースホルダ以外でも、Base64 として無効な文字列や長さ不足だと、CIFS ホスト登録時 (EncryptionService の初回解決時) に `Encryption:MasterKey は Base64 でエンコードされた値である必要があります` で 500 エラーになる。

#### Server appsettings.json キー一覧

| キー | 必須 | 設定する値 | 補足 |
|---|---|---|---|
| `ConnectionStrings:Default` | 必須 | SQLite DB の保存先 | 例: `C:\ProgramData\Watashi\watashi.db`。サービスアカウントが親フォルダを作成/読み書きできること |
| `Jwt:Secret` | 必須 | 32 バイト以上のランダム文字列 | Production で `CHANGE-ME` のままだと起動拒否 |
| `Jwt:Issuer` / `Jwt:Audience` | 推奨 | 通常は `Watashi` | Client と Server の JWT 検証用。通常変更不要 |
| `Jwt:AccessTokenMinutes` | 推奨 | アクセストークン分数 | 短いほど漏えい時の影響は小さい。既定 15 |
| `Jwt:RefreshTokenDays` | 推奨 | 再ログイン不要期間 | 既定 30 |
| `Encryption:MasterKey` | 必須 | 32 バイト Base64 | CIFS パスワード暗号化用。紛失すると既存ホスト資格情報を復号できない |
| `Auth:AllowHttpForAutoLogin` | 任意 | `false` 推奨 | HTTP 接続で「このPCを記憶する」を許可するか。Production は false |
| `Auth:LoginPerMinutePerIp` | 任意 | 1 分あたり試行数 | 同一 NAT で誤検知する場合だけ増やす |
| `Routing:UseMtls` | 構成依存 | `true` / `false` | Server↔Agent を mTLS で相互認証するなら true |
| `Routing:ClientCertificatePath` | mTLS 時必須 | 中央サーバが Agent へ提示する PFX | Server → Agent の呼び出しに使うクライアント証明書 |
| `Routing:ClientCertificatePassword` | mTLS 時必須 | 上記 PFX のパスワード | 環境変数上書きも可 |
| `Routing:SharedSecret` | 共有秘密時必須 | 32 バイト以上のランダム値 | Server と Agent の両方に同じ値を設定。mTLS 本番では空推奨 |
| `Cifs:SessionIdleSeconds` | 任意 | SMB セッション再利用秒数 | 長くすると再接続は減るが、セッション保持時間が伸びる |
| `Cifs:MaxSessionsPerKey` | 任意 | CIFS 接続キーごとの最大セッション数 | 同時転送が多い環境で増やす |
| `Kestrel:Endpoints` | 必須 | Listen URL と証明書 | HTTP/HTTPS ポート、HTTPS 証明書を定義 |
| `Serilog:WriteTo` | 推奨 | Console / File | 既定で `C:\ProgramData\Watashi\logs\server-.log` に日次ローテーション |

**Jwt:Secret / Encryption:MasterKey の生成方法 (Windows ネイティブ)**:

```powershell
# Windows PowerShell 5.1 / 7 どちらでも動く
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

# Jwt:Secret 用 (48 バイト推奨)
$bytes = New-Object byte[] 48
$rng.GetBytes($bytes)
[Convert]::ToBase64String($bytes)

# Encryption:MasterKey 用 (32 バイト固定)
$bytes = New-Object byte[] 32
$rng.GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```

PowerShell 7+ (`pwsh.exe`) なら 1 行:
```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Git for Windows が入っていれば openssl も使える:
```powershell
& "C:\Program Files\Git\usr\bin\openssl.exe" rand -base64 32
```

> **★ MasterKey の保管**: 32 バイト Base64 (44 文字、末尾 `=` 1 個) を生成したら、**パスワードマネージャや金庫で別途バックアップ**。DB バックアップとは別場所に保管すること。鍵を紛失すると DB の CIFS 接続パスワードが全部復号不能になり、ホスト登録のやり直しになる。鍵を変更したい場合も既存暗号データは復号できなくなるので、運用開始後の変更は要計画。
>
> ファイル流出リスクを下げるなら、appsettings.json には書かず環境変数 `WATASHI_MASTER_KEY` で渡せる:
> ```powershell
> setx WATASHI_MASTER_KEY "<生成した値>" /M
> ```

**Windows サービス化（同梱スクリプト使用）**:
```powershell
# 管理者 PowerShell で実行
.\deploy\install-server-service.ps1 -PublishDir D:\publish\WatashiServer

# 既定の動作:
# - C:\Program Files\Watashi\Server に配置
# - サービス名 Watashi.Server、自動起動、LocalSystem アカウント
# - 異常終了時の自動再起動 (5s → 30s → 60s)
```

専用サービスアカウントを使う場合:
```powershell
.\deploy\install-server-service.ps1 `
    -PublishDir D:\publish\WatashiServer `
    -ServiceAccount "DOMAIN\svc-watashi"
# パスワードプロンプトは sc.exe の制約上現状未対応。設定後 services.msc で資格情報を入れること
```

アンインストール:
```powershell
.\deploy\uninstall-service.ps1 -ServiceName Watashi.Server
```

初回起動で `C:\ProgramData\Watashi\watashi.db` が自動生成され、admin (`admin` / `Admin123!@#`) でログイン可能になる。
**最初のログインで必ずパスワードを変更すること**。

### ③-2 配布サーバー (ClickOnce / IIS)

クライアントは ClickOnce で配布します。

1. `src\Watashi.Client\Properties\PublishProfiles\ClickOnceProfile.pubxml` を編集:
   - `<PublishUrl>`: 発行先 (例: `\\fileserver\share\Watashi\`)
   - `<InstallUrl>`: ユーザーが開く URL (例: `https://watashi.internal/install/`)
   - `<ManifestCertificateThumbprint>`: Code Signing 証明書のサムプリント

2. **接続先サーバと機能を `src\Watashi.Client\deployment.json` に設定** (発行前に必ず編集):
   ```jsonc
   {
     "serverUrl": "https://watashi.internal:8443",  // 接続する中央サーバ
     "enableDragDrop": true                          // ドラッグ＆ドロップ転送の可否
   }
   ```
   - このファイルは発行物に同梱され、起動時にこの値で接続先・機能が**固定**される。利用者 (クライアント) からは変更できない
   - `%LocalAppData%\Watashi\settings.json` より優先される

3. 発行:
   ```powershell
   dotnet publish src\Watashi.Client\Watashi.Client.csproj `
       -c Release -p:PublishProfile=ClickOnceProfile
   ```

4. IIS 側で MIME 設定 → [deploy/IIS-MIME.md](../deploy/IIS-MIME.md) 参照

5. クライアント PC で `https://watashi.internal/install/Watashi.Client.application` を開く → インストール開始

6. 初回起動でそのまま **ログイン画面** が表示される (接続先は deployment.json で確定済みのため、利用者が入力する項目はない)。ログイン画面下の「接続テスト」で疎通確認のみ可能

7. 以降、起動時にバージョンチェック → 更新があれば自動でダウンロード

> **接続先や D&D を変更したいとき**:
> `deployment.json` を編集して **再発行** する。ClickOnce はマニフェストでファイルのハッシュを検証するため、発行後に配布物の deployment.json を直接書き換えるとインストール/更新に失敗する (必ず再発行・再署名すること)。
> これにより「クライアントから接続先を変えさせない」運用が保証される。

### ③-3 エージェント (踏み台)

**ビルド**:
```powershell
dotnet publish src\Watashi.Agent\Watashi.Agent.csproj `
    -c Release -r win-x64 --self-contained `
    -o D:\publish\WatashiAgent
```

**踏み台サーバーで `appsettings.json` を編集** してから、Windows Service として登録:

```powershell
# 管理者 PowerShell
.\deploy\install-agent-service.ps1 -PublishDir D:\publish\WatashiAgent
```

`deploy/install-agent.ps1`（従来スクリプト、appsettings 生成 + 旧式 sc.exe 登録）も残してありますが、新規はサービス専用の `install-agent-service.ps1` を推奨。

**中央側で ExecutionNode 登録** (管理画面 → 実行ノードタブ):
- Name: `bastion-a` (Agent の AgentId と一致させる ← ハートビート紐付けに使用)
- NodeType: `Agent`
- Endpoint: `http://bastion-a:8081` (モード B) / `https://bastion-a:8443` (モード A)
- ClientCertificateThumbprint: モード A のみ、Agent が提示する証明書の Thumbprint
- MaxConcurrency: 20

ハートビートが届けば `HealthStatus` が `Healthy` に切り替わる（15 秒間隔チェック、90 秒未着で `Unhealthy`）。

#### Agent appsettings.json キー一覧

| キー | 必須 | 設定する値 | 補足 |
|---|---|---|---|
| `Agent:AgentId` | 必須 | Agent の一意名 | 中央の `ExecutionNodes.Name` と完全一致させる。例: `bastion-a` |
| `Agent:CentralUrl` | 必須 | 中央 Server の URL | 例: `https://central.internal:8443` |
| `Agent:MaxConcurrency` | 任意 | Agent 側の同時処理上限 | 起動時の初期値。以後は中央の ExecutionNode.MaxConcurrency が heartbeat 応答で反映される |
| `ConnectionStrings:Buffer` | 必須 | Agent ローカル SQLite | 中央へログ同期できない時の一時バッファ。例: `C:\ProgramData\WatashiAgent\agent_buffer.db` |
| `Certificate:Path` | mTLS 時必須 | Agent が中央へ提示する PFX | Agent → Server の heartbeat / ログ同期で使うクライアント証明書 |
| `Certificate:Password` | mTLS 時必須 | 上記 PFX のパスワード | HTTPS の Kestrel 証明書と同じ PFX を使うことも可能だが、本番は用途別に分けるのが望ましい |
| `Auth:CentralCertificateThumbprint` | mTLS 時必須 | 中央が Agent へ提示するクライアント証明書の Thumbprint | Server → Agent の `/agent/*` 呼び出しを Agent 側で検証する |
| `Auth:SharedSecret` | 共有秘密時必須 | Server の `Routing:SharedSecret` と同じ値 | Server → Agent と Agent → Server の両方向で `X-Watashi-Secret` として使う |
| `Routing:UseMtls` | 構成依存 | `true` / `false` | true の場合、Agent は中央からの mTLS クライアント証明書を受け付ける |
| `Kestrel:Endpoints` | 必須 | Agent が待ち受ける URL | HTTP なら `http://0.0.0.0:8081`、HTTPS なら証明書設定も入れる |
| `Cifs:SessionIdleSeconds` / `Cifs:MaxSessionsPerKey` | 任意 | SMB セッションプール設定 | Agent 経由の CIFS 接続に適用 |
| `Serilog:WriteTo` | 推奨 | Console / File | 既定で `C:\ProgramData\WatashiAgent\logs\agent-.log` に日次ローテーション |

HTTP + 共有秘密モードでは `Certificate:Path` / `Certificate:Password` は空でよいです。Agent の待受も `Kestrel:Endpoints:Http` を使うため、Agent 側に HTTPS サーバ証明書は不要です。証明書が必要になるのは Client↔Server を HTTPS にする中央 Server 側、または `Routing:UseMtls=true` で mTLS を使う場合だけです。`Routing:UseMtls=false` で `Auth:SharedSecret` が空の場合、Agent は起動時に設定エラーとして停止します。

旧 `deploy/install-agent.ps1` を使って HTTP 共有秘密モードでインストールする場合は、必ず `-SharedSecret` を指定してください。未指定だと Agent は証明書も `X-Watashi-Secret` も送れず、heartbeat と Server→Agent の転送リクエストが認証に失敗します。

#### 設定例 1: Agent 1台 (HTTP + 共有秘密)

構成:

```
Client --HTTPS--> Server --HTTP--> Agent A --SMB--> CIFS
```

この例では Client↔Server は HTTPS、Server↔Agent は閉域網内 HTTP とし、Agent 認証は共有秘密で行います。Server と Agent の `SharedSecret` は同じ値にします。

**Server `appsettings.json` 抜粋**:

```jsonc
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": {
          "Subject": "watashi.internal",
          "Store": "My",
          "Location": "LocalMachine",
          "AllowInvalid": false
        }
      }
    }
  },
  "Routing": {
    "UseMtls": false,
    "ClientCertificatePath": "",
    "ClientCertificatePassword": "",
    "SharedSecret": "BASE64-OR-LONG-RANDOM-SECRET-SAME-AS-AGENT"
  }
}
```

**Agent A `appsettings.json` 抜粋**:

```jsonc
{
  "Agent": {
    "AgentId": "agent-a",
    "CentralUrl": "https://watashi.internal:8443",
    "MaxConcurrency": 20
  },
  "ConnectionStrings": {
    "Buffer": "Data Source=C:\\ProgramData\\WatashiAgent\\agent_buffer.db;Cache=Shared;Foreign Keys=True;"
  },
  "Certificate": {
    "Path": "",
    "Password": ""
  },
  "Auth": {
    "CentralCertificateThumbprint": "",
    "SharedSecret": "BASE64-OR-LONG-RANDOM-SECRET-SAME-AS-SERVER"
  },
  "Routing": {
    "UseMtls": false
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8081" }
    }
  }
}
```

**管理画面 → 実行ノード**:

| 項目 | 値 |
|---|---|
| 名前 | `agent-a` |
| 種別 | `Agent` |
| Endpoint | `http://agent-a:8081` |
| 経由 Agent | 未設定 |
| MaxConcurrency | `20` |

**管理画面 → ホスト**:

| 項目 | 値 |
|---|---|
| 表示名 | `fileserver01` |
| ホスト名/IP | CIFS サーバの FQDN/IP |
| ポート | `445` |
| CIFS ユーザー / パスワード | CIFS 接続用のサービスアカウント |
| 実行ノード | `agent-a` |

この設定では Server が `http://agent-a:8081/agent/files/*` に依頼し、Agent A が CIFS へ SMB 接続します。

#### 設定例 2: 1段チェーン (HTTP + 共有秘密)

構成:

```
Client --HTTPS--> Server --HTTP--> Agent A --HTTP--> Agent B --SMB--> CIFS
```

使う場面:
- Server は Agent A にだけ到達できる
- Agent A は Agent B に到達できる
- Agent B は CIFS サーバに SMB 接続できる
- Agent B は中央 Server に直接 heartbeat できなくてもよい

制限:
- 対応するチェーンは 1段のみ (`Server → Agent A → Agent B → CIFS`)
- Agent A/B 間は HTTP + 共有秘密のみ対応
- `Server → Agent A → Agent B → Agent C → CIFS` は非対応
- Agent A と Agent B の `Auth:SharedSecret` は Server の `Routing:SharedSecret` と同じ値にする

**Server `appsettings.json` 抜粋**:

```jsonc
{
  "Routing": {
    "UseMtls": false,
    "ClientCertificatePath": "",
    "ClientCertificatePassword": "",
    "SharedSecret": "BASE64-OR-LONG-RANDOM-SECRET-SAME-FOR-A-AND-B"
  }
}
```

**Agent A `appsettings.json` 抜粋**:

```jsonc
{
  "Agent": {
    "AgentId": "agent-a",
    "CentralUrl": "https://watashi.internal:8443",
    "MaxConcurrency": 20
  },
  "Certificate": {
    "Path": "",
    "Password": ""
  },
  "Auth": {
    "CentralCertificateThumbprint": "",
    "SharedSecret": "BASE64-OR-LONG-RANDOM-SECRET-SAME-FOR-A-AND-B"
  },
  "Routing": { "UseMtls": false },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8081" }
    }
  }
}
```

**Agent B `appsettings.json` 抜粋**:

```jsonc
{
  "Agent": {
    "AgentId": "agent-b",
    // Agent B が中央へ直接到達できない場合は空でもよい。Heartbeat / LogSync はスキップされる。
    "CentralUrl": "",
    "MaxConcurrency": 20
  },
  "Certificate": {
    "Path": "",
    "Password": ""
  },
  "Auth": {
    "CentralCertificateThumbprint": "",
    "SharedSecret": "BASE64-OR-LONG-RANDOM-SECRET-SAME-FOR-A-AND-B"
  },
  "Routing": { "UseMtls": false },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8081" }
    }
  }
}
```

**管理画面 → 実行ノード**:

| 順 | 名前 | 種別 | Endpoint | 経由 Agent |
|---|---|---|---|---|
| 1 | `agent-a` | `Agent` | `http://agent-a:8081` | 未設定 |
| 2 | `agent-b` | `Agent` | `http://agent-b:8081` | `agent-a` |

**管理画面 → ホスト**:

| 項目 | 値 |
|---|---|
| 実行ノード | `agent-b` |

ホストには **最終的に CIFS へ SMB 接続する Agent B** を選びます。Server は `agent-b` に `経由 Agent=agent-a` が設定されていることを見て、実際の HTTP リクエストを Agent A に送り、Agent A が Agent B へ転送します。

**ファイアウォール**:

| 経路 | 必要 |
|---|---|
| Client → Server | HTTPS 8443 など |
| Server → Agent A | HTTP 8081 |
| Agent A → Agent B | HTTP 8081 |
| Agent B → CIFS | SMB 445 |
| Agent B → Server | 任意。許可できるなら heartbeat 用に HTTPS 8443 |

Agent B が Server へ直接到達できない場合、管理画面の Health は `Unknown` のままになることがあります。この状態でも `agent-b` が有効で、`agent-a → agent-b` が HTTP 到達可能ならファイル操作は実行できます。到達不可の場合は操作時に 503 または Agent HTTP エラーになります。

#### Agent の証明書とは

Watashi で「Agent の証明書」と呼ぶものは、HTTPS 用とクライアント認証用があり、さらに中央側にも Agent へ提示するクライアント証明書があります。混同しやすいので、設定先で区別してください。

HTTP + 共有秘密モードでは、下表の Agent 関連証明書は使いません。Agent A/B の待受 Endpoint は `http://...`、認証は `Auth:SharedSecret` / `Routing:SharedSecret` で行います。

| 用途 | 設定先 | 何を守るか |
|---|---|---|
| Agent の HTTPS サーバ証明書 | Agent `Kestrel:Endpoints:Https:Certificate` | Server が Agent の HTTPS endpoint に接続するときの TLS |
| Agent クライアント証明書 | Agent `Certificate:Path` / Server `ExecutionNode.ClientCertificateThumbprint` | Agent が中央 Server の `/api/internal/*` に接続するとき、どの Agent かを証明する |
| 中央クライアント証明書 | Server `Routing:ClientCertificatePath` / Agent `Auth:CentralCertificateThumbprint` | Server が Agent の `/agent/*` に接続するとき、中央 Server からの呼び出しかを Agent が検証する |

検証環境では同じ PFX を複数用途に流用しても動きますが、本番では「HTTPS サーバ証明書」と「クライアント認証証明書」を分ける方が更新・失効・監査が楽です。mTLS を使わないモード B では、これらのクライアント証明書の代わりに `Routing:SharedSecret` / `Auth:SharedSecret` を使います。

#### 複数 Agent / 複数踏み台

複数の踏み台サーバーに Agent を配置できます。各 Agent で `Agent:AgentId` を一意にし、中央の管理画面で同じ名前の ExecutionNode を登録してください。CIFS ホスト登録時にどの ExecutionNode から接続するかを選ぶため、ネットワークセグメントごとに Agent を分けられます。

`Server → Agent A → Agent B → CIFS` の 1段チェーンも使えます。Agent B の ExecutionNode に `経由 Agent=Agent A` を設定してください。2段以上のチェーンと mTLS チェーンは非対応です。

---

## ④ 初期データ登録

ログイン後、管理者として以下を順に登録:

1. **ノード**: Direct (Local) は seed 済み。Agent を追加するならここで登録
2. **ホスト**: CIFS ファイルサーバーの情報 (アドレス + 資格情報 + どのノードから接続するか)
3. **共有**: ホストに紐づく SMB 共有名
4. **テンプレート**: 「読取のみ」「読取+書込」「フルアクセス」は seed 済み。必要なら追加
5. **ユーザー**: 一般ユーザーを追加 (作成時は初回 `MustChangePassword=true`)
6. **ユーザー権限**: ユーザー × 共有 × サブパス × テンプレートで権限付与

詳細手順は [ADMIN-GUIDE.md](ADMIN-GUIDE.md) を参照。

---

## ⑤ 本番投入チェックリスト

| ✓ | 項目 |
|---|---|
| ☐ | `Jwt:Secret` を本番値 (32 バイト以上のランダム) に差し替え（プレースホルダのままだと Production で起動拒否） |
| ☐ | `Encryption:MasterKey` を本番値 (32 バイト Base64) に差し替え + バックアップ確保 |
| ☐ | Server HTTPS 証明書を社内 CA 発行のものに ([deploy/CERTIFICATE.md](../deploy/CERTIFICATE.md) でストア参照 / ファイル指定どちらかを選ぶ) |
| ☐ | (ストア参照方式の場合) 秘密キーへのサービスアカウントの Read 権限を付与 |
| ☐ | クライアント PC に社内 CA ルート証明書を配布 |
| ☐ | admin の初期パスワード変更 |
| ☐ | (モード A の場合) Agent クライアント証明書を発行して `ExecutionNode.ClientCertificateThumbprint` に登録 |
| ☐ | (モード A の場合) Agent 側 `Auth:CentralCertificateThumbprint` に中央が Agent へ提示するクライアント証明書サムプリント設定 |
| ☐ | (モード B の場合) `Routing:SharedSecret` を両側に同じ値で設定 |
| ☐ | `Auth:LoginPerMinutePerIp` を環境に応じて調整 (デフォルト 10) |
| ☐ | `Cifs:MaxSessionsPerKey` を環境に応じて調整 (デフォルト 4) |
| ☐ | ClickOnce 用 Code Signing 証明書のサムプリントを `ClickOnceProfile.pubxml` に |
| ☐ | IIS で `.application` `.manifest` `.deploy` の MIME を登録 |
| ☐ | `install-server-service.ps1` でサービス登録、自動再起動の確認 |
| ☐ | `install-agent-service.ps1` で各踏み台にサービス登録 |
| ☐ | 日次 DB バックアップタスク登録 (`SqliteConnection.BackupDatabase` ベース) |
| ☐ | 日次 AuditLogs 削除タスク登録 (1 年経過分) |
| ☐ | Server / Agent のログファイルローテーション (Serilog `rollingInterval=Day`) |
| ☐ | 監視 (Server / Agent の `/health` を死活監視) |
| ☐ | ファイアウォール: 必要な経路のみ開放 |

---

## ⑥ 運用

### バックアップ (日次)

```powershell
# C:\Apps\Watashi\backup.ps1
$src  = "C:\ProgramData\Watashi\watashi.db"
$dest = "D:\Backup\Watashi\watashi-$(Get-Date -Format yyyyMMdd).db"
$asm = [Reflection.Assembly]::LoadFile((Get-ChildItem "C:\Program Files\Watashi\Server\Microsoft.Data.Sqlite.dll").FullName)
$conSrc  = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$src")
$conDst  = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dest")
$conSrc.Open(); $conDst.Open()
$conSrc.BackupDatabase($conDst)
$conSrc.Close(); $conDst.Close()

# 古い世代の削除 (7 日分のみ保持)
Get-ChildItem D:\Backup\Watashi\watashi-*.db |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item
```

タスクスケジューラに登録:
```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi DB Backup" `
    /TR "powershell.exe -File C:\Apps\Watashi\backup.ps1" /ST 02:00 /RL HIGHEST
```

### ログ削除 (日次)

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi Log Cleanup" `
    /TR "sqlite3.exe C:\ProgramData\Watashi\watashi.db \"DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-1 year');\"" `
    /ST 03:00
```

月次で VACUUM:
```powershell
schtasks.exe /Create /SC MONTHLY /TN "Watashi VACUUM" `
    /TR "sqlite3.exe C:\ProgramData\Watashi\watashi.db \"VACUUM;\"" /ST 04:00
```

### 死活監視

5 分間隔で:
```bash
curl --fail --max-time 10 https://watashi.internal:8443/health
curl --fail --max-time 10 http://bastion-a:8081/health
```

`HealthStatus=Unhealthy` が長時間続く Agent ノードは管理画面 → ノードタブで確認。

### サービス状態確認

```powershell
Get-Service Watashi.Server, Watashi.Agent | Format-Table
# Status / Name / DisplayName
# Running   Watashi.Server   Watashi Central Server
# Running   Watashi.Agent    Watashi Agent
```

### 異常時の手動再起動

```powershell
Restart-Service Watashi.Server
# あるいは
sc.exe stop Watashi.Server
sc.exe start Watashi.Server
```

---

## トラブルシューティング

### サーバー起動時にコケる
- **「Jwt:Secret がデフォルトのプレースホルダ値のままです」**: Production 環境では実値必須。Dev/Stg なら警告だけで起動継続
- **「Jwt:Secret が設定されていません」**: `appsettings.json` または環境変数 `WATASHI_MASTER_KEY` を確認
- **「Encryption:MasterKey は 32 バイト...」**: Base64 文字列が正しく 32 バイトにデコードされるか確認
- DB ファイルパスの親フォルダが作れない: 起動ユーザー（サービスアカウント）の権限を確認

### ホスト登録 (CIFS 登録) で HTTP 500
- 例外メッセージが `Encryption:MasterKey は Base64 でエンコードされた値である必要があります` の場合、`appsettings.json` の `Encryption:MasterKey` がプレースホルダのまま (`REPLACE-WITH-...`) か、Base64 ではない値が入っている
- EncryptionService は Singleton + 初回利用時解決のため、Watashi.Server の起動自体は通り、CIFS パスワード暗号化が初めて走るホスト登録時に失敗する仕様
- 上の「Jwt:Secret / Encryption:MasterKey の生成方法」で 32 バイト Base64 を生成して差し替え → サーバ再起動
- Base64 として正しい値か確認:
  ```powershell
  $key = "<appsettings.json に入れている値>"
  try { $b = [Convert]::FromBase64String($key); "OK: $($b.Length) bytes" } catch { "NG: $($_.Exception.Message)" }
  ```
  → `OK: 32 bytes` でないと受理されない

### Windows Service が起動しない
```powershell
# Event Viewer の Application ログを確認
Get-EventLog -LogName Application -Source "Watashi.Server" -Newest 20
# あるいは直接実行してエラー確認
& "C:\Program Files\Watashi\Server\Watashi.Server.exe"
```

### ログインできない
- ロックされている (連続失敗、既定 15 回): 管理画面でロック解除、または DB を直接更新
  ```sql
  UPDATE Users SET IsLocked=0, FailedLoginCount=0 WHERE Username='alice';
  ```
- パスワード期限切れ: `MustChangePassword=true` になり、レスポンスにフラグが付く。クライアントは強制変更画面へ
- **「429 Too Many Requests」**: ログイン試行が 1 分あたり 10 回を超えた。`Auth:LoginPerMinutePerIp` を緩めるか時間を空ける

### 自動ログインが効かない
- 接続が HTTP: HTTPS 必須なので Client 設定で HTTPS に変更
- Credential Manager から消えている: ログイン画面で再度「このPCを記憶する」をチェック
- 管理者がデバイスを失効済み: 管理画面 → 信頼デバイスで確認

### リフレッシュ失敗 (token_reuse_detected)
- 失効済みリフレッシュトークンが提示された場合の応答。ファミリー全体が失効するため、ユーザーは再ログインが必要
- 原因: 同じトークンを別端末から使った、ログアウト後にトークンが再生された、など
- 影響: 該当ユーザーの全 active セッションが切断されるため、頻発する場合は配布ログ確認

### Agent が Unhealthy のまま
- Agent サービスが起動しているか: `Get-Service Watashi.Agent`
- 中央への heartbeat が届いているか: Agent 側ログ確認
- ファイアウォール: Agent → 中央への送信が許可されているか
- 中央 DB の `ExecutionNodes.Name` と Agent 側 `Agent:AgentId` が一致しているか（大文字小文字含む）
- (mTLS モード) `ExecutionNode.ClientCertificateThumbprint` と Agent が提示する証明書サムプリントが一致しているか

### Agent から 401/403 が返る
- (mTLS モード) Agent 側 `Auth:CentralCertificateThumbprint` が中央が Agent へ提示するクライアント証明書と一致しているか確認
- (HTTP モード) `Auth:SharedSecret` を両側で同じ値に設定したか
- いずれも未設定だと匿名アクセスは拒否される

### ファイル一覧が空 / 403
- ユーザー権限が登録されているか (管理画面 → ユーザー権限)
- AllowedPath とリクエストパスの関係 (パスブラウザで実在ディレクトリを選んでいるか)
- CIFS 資格情報が正しいか (管理画面 → ホスト → 接続テスト)

### 操作が遅い / SMB ハンドシェイクが頻発
- `Cifs:SessionIdleSeconds` を伸ばす（デフォルト 60 → 120）。プールアイドル時間が短すぎると再接続が増える
- `Cifs:MaxSessionsPerKey` を増やす（デフォルト 4 → 8）。同時操作が多い環境向け

### "リネームでは親ディレクトリを変更できません"
- 仕様。同一フォルダ内でのリネームのみ許可。フォルダ間の移動は禁止 (DELETE 権限の抜け穴対策)

### ストリーミング転送中にキャンセルしたい
- 現状クライアントから明示的なキャンセルボタンは未実装。アプリ終了で接続切断される。

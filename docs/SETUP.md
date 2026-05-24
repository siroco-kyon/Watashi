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
dotnet run
# → 初回起動: 接続設定で http://127.0.0.1:18080 を入力
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
> モード B でも **`Routing:SharedSecret`** を設定すれば Agent は X-Watashi-Secret ヘッダで中央を識別できる。

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
    "CentralCertificateThumbprint": "<中央サーバが提示するクライアント証明書 Thumbprint>",
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

**Client の接続設定**:
```
サーバー URL: https://watashi.internal:8443
プロトコル:  HTTPS
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
    "SharedSecret": "<openssl rand -base64 32 で生成、Agent と同じ値>"
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
    "Secret": "<openssl rand -base64 48 で生成>",
    "Issuer": "Watashi",
    "Audience": "Watashi",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 30
  },
  "Encryption": {
    // 32 バイトの Base64 (= 44 文字)。CIFS パスワード暗号化に使用
    // 紛失すると全 CIFS 資格情報が復号不能になるので厳重管理
    "MasterKey": "<openssl rand -base64 32 で生成>"
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

> 起動時検証: `Jwt:Secret` が `CHANGE-ME` で始まり `ASPNETCORE_ENVIRONMENT=Production` の場合は例外で起動拒否。
> 同様に `Encryption:MasterKey` も `REPLACE-WITH` プレフィックス検出。Dev/Stg では警告のみ。

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

2. 発行:
   ```powershell
   dotnet publish src\Watashi.Client\Watashi.Client.csproj `
       -c Release -p:PublishProfile=ClickOnceProfile
   ```

3. IIS 側で MIME 設定 → [deploy/IIS-MIME.md](../deploy/IIS-MIME.md) 参照

4. クライアント PC で `https://watashi.internal/install/Watashi.Client.application` を開く → インストール開始

5. 以降、起動時にバージョンチェック → 更新があれば自動でダウンロード

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
| ☐ | Server HTTPS 証明書を社内 CA 発行のものに |
| ☐ | クライアント PC に社内 CA ルート証明書を配布 |
| ☐ | admin の初期パスワード変更 |
| ☐ | (モード A の場合) Agent クライアント証明書を発行して `ExecutionNode.ClientCertificateThumbprint` に登録 |
| ☐ | (モード A の場合) Agent 側 `Auth:CentralCertificateThumbprint` に中央サーバ証明書サムプリント設定 |
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

### Windows Service が起動しない
```powershell
# Event Viewer の Application ログを確認
Get-EventLog -LogName Application -Source "Watashi.Server" -Newest 20
# あるいは直接実行してエラー確認
& "C:\Program Files\Watashi\Server\Watashi.Server.exe"
```

### ログインできない
- ロックされている (5 連続失敗): 管理画面でロック解除、または DB を直接更新
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
- (mTLS モード) Agent 側 `Auth:CentralCertificateThumbprint` が中央サーバの実際の証明書と一致しているか確認
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

# Watashi 環境構築ガイド

開発機での 5 分セットアップから、本番デプロイまでを通しで説明します。

- [前提条件](#前提条件)
- [① 5 分で動かす (開発機での動作確認)](#-5-分で動かす-開発機での動作確認)
- [② ネットワーク構成](#ネットワーク構成) ← Client↔Server↔Agent の HTTPS/HTTP 組み合わせ
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
| OS (Server / Agent) | Windows Server 2019 以降 (Linux でも .NET 8 ランタイムがあれば動作) |
| OS (Client) | Windows 10 / 11 |
| .NET ランタイム | 8.0 (Server / Agent / Client いずれも `net8.0`) |
| ネットワーク | 閉域網 (社内 LAN / VPN 内) を想定 |
| 同時接続 | 50 ユーザー程度を想定 |

ビルド機には .NET 8 SDK が必要。本番サーバーは self-contained 発行ならランタイム不要。

---

## ① 5 分で動かす (開発機での動作確認)

CIFS サーバーが無くてもログイン / 認証 / DB 自動生成までは確認できます。

```powershell
# 1) 中央サーバー起動
cd src\Watashi.Server
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

別ウィンドウで:

```powershell
# 2) 疎通確認
curl http://localhost:8080/health
# → {"status":"ok","at":"..."}

# 3) ログイン (admin / Admin123!@#)
curl -X POST http://localhost:8080/api/auth/login `
     -H "Content-Type: application/json" `
     -d '{"username":"admin","password":"Admin123!@#"}'
# → mustChangePassword: true なので、初回はパスワード変更が必要
```

```powershell
# 4) WPF クライアント起動
cd ..\Watashi.Client
dotnet run
# → 初回起動: 接続設定で http://localhost:8080 を入力
# → ログイン: admin / Admin123!@# → パスワード変更画面 → メイン画面
```

このタイミングでは「ホスト」が未登録なので、リモートペインは空。
ホストを登録するには [ADMIN-GUIDE.md](ADMIN-GUIDE.md) を参照、ただし本物の CIFS サーバーが必要。

DB ファイルは:
- 開発: `src\Watashi.Server\watashi-dev.db`
- 本番: `C:\ProgramData\Watashi\watashi.db`

---

## ② ネットワーク構成

通信経路は 2 区間あり、それぞれ独立に HTTP/HTTPS を選べます。

```
[Client] ── 区間 A ── [Server] ── 区間 B ── [Agent] ── SMB ── [CIFS]
              ↑                     ↑
        Client が選択         Node ごとに選択
```

### サポートする 3 つのモード

| モード | 区間 A (Client↔Server) | 区間 B (Server↔Agent) | mTLS | 想定環境 |
|---|---|---|---|---|
| **A: 全 HTTPS + mTLS (推奨)** | HTTPS | HTTPS | ON | 本番、踏み台が別セキュリティゾーン |
| **B: 混在** | HTTPS | HTTP | OFF | Server と Agent が同一信頼ゾーン (推奨環境) |
| **C: 全 HTTP** | HTTP | HTTP | OFF | 検証 / 内部ラボ専用 |

> 機密データ (CIFS 資格情報、ファイル内容) がネットワークを流れるため、Client↔Server は HTTPS を強く推奨。
> Server↔Agent は社内 LAN 内で完結する場合 (例: 同一 DC, 同一 VLAN) は HTTP でも実用上問題ないが、業界規制 (PCI / 個人情報) がある場合は HTTPS + mTLS にすること。

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

**Agent `appsettings.json`**:
```json
{
  "Agent": {
    "ListenUrl": "https://0.0.0.0:8443",
    "CentralUrl": "https://central.internal:8443"
  },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": { "Path": "agent.pfx", "Password": "..." },
        "ClientCertificateMode": "RequireCertificate"
      }
    }
  }
}
```

**中央 DB の ExecutionNodes**:
```
Endpoint = https://bastion-a:8443
ClientCertificateThumbprint = <Agent が提示するクライアント証明書の Thumbprint>
```

**Client の接続設定**:
```
サーバー URL: https://watashi.internal:8443
プロトコル:  HTTPS
```

### モード B: 混在 (Client↔Server は HTTPS、Server↔Agent は HTTP)

**こちらが現実的な落としどころ**。
中央と踏み台が同一データセンタ / 同一信頼ゾーンで、クライアント PC は事務 LAN / VPN 越しでアクセスするケース。

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
    "UseMtls": false
  }
}
```

**Agent `appsettings.json`**:
```json
{
  "Agent": {
    "ListenUrl": "http://0.0.0.0:8081",
    "CentralUrl": "https://central.internal:8443"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8081" }
    }
  }
}
```

**中央 DB の ExecutionNodes**:
```
Endpoint = http://bastion-a:8081
ClientCertificateThumbprint = (空でよい)
```

**Client の接続設定**:
```
サーバー URL: https://watashi.internal:8443
プロトコル:  HTTPS
```

### モード C: 全 HTTP

検証・ラボでのみ使う。本番では使わないこと。
Server `appsettings.json` から `Https` セクションを削除し `Http` のみ残す。
Agent もすべて HTTP。Client は HTTP で接続。

**注意:** HTTP モードでは自動ログイン (信頼デバイス) は使用不可。
ログイン画面のチェックボックスがグレーアウトする。

### 双方向は許可不要

中央サーバーは Agent への HTTP/HTTPS 接続を **発信側** として行う。
踏み台ファイアウォールに Agent ポート (例: 8081 / 8443) を中央サーバー IP からのみ許可すれば足りる。
Agent → 中央 はハートビート / ログ送信に限定。

---

## ③ 本番デプロイ

### ③-1 中央サーバー (Watashi.Server)

**ビルド (CI/開発機で実施)**:
```powershell
dotnet publish src\Watashi.Server\Watashi.Server.csproj `
    -c Release -r win-x64 --self-contained `
    -o C:\Build\WatashiServer
```

`C:\Build\WatashiServer` を本番サーバーに配置。

**サーバーでの設定** (`appsettings.json`):

```jsonc
{
  "ConnectionStrings": {
    // ローカル SSD 必須。ネットワーク共有は不可
    "Default": "Data Source=C:\\ProgramData\\Watashi\\watashi.db;Cache=Shared;Foreign Keys=True;"
  },
  "Jwt": {
    // 32 バイト以上のランダム値に必ず変更
    "Secret": "<openssl rand -base64 48 で生成した値など>",
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
    "ClientCertificatePassword": "..."
  }
}
```

**Windows サービス化**:
```powershell
sc.exe create WatashiServer binPath= "C:\Apps\WatashiServer\Watashi.Server.exe" start= auto
sc.exe description WatashiServer "Watashi Central Server"
sc.exe failure WatashiServer reset= 86400 actions= restart/5000/restart/10000/restart/30000
Start-Service WatashiServer
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

中央と Agent をモード A (mTLS) で繋ぐ場合と、モード B (HTTP) で繋ぐ場合で証明書回りが変わる。

**ビルド**:
```powershell
dotnet publish src\Watashi.Agent\Watashi.Agent.csproj `
    -c Release -r win-x64 --self-contained `
    -o C:\Build\WatashiAgent
```

**踏み台サーバーへの配置 (PowerShell インストーラ使用)**:

```powershell
# モード B (HTTP) の場合
.\deploy\install-agent.ps1 `
    -SourceDir C:\Build\WatashiAgent `
    -AgentId bastion-a `
    -CentralUrl https://central.internal:8443 `
    -ListenUrl http://0.0.0.0:8081 `
    -MaxConcurrency 20

# モード A (HTTPS + mTLS) の場合
.\deploy\install-agent.ps1 `
    -SourceDir C:\Build\WatashiAgent `
    -AgentId bastion-a `
    -CentralUrl https://central.internal:8443 `
    -ListenUrl https://0.0.0.0:8443 `
    -CertificatePath C:\certs\bastion-a.pfx `
    -CertificatePassword "..."
```

インストーラの実体は [deploy/install-agent.ps1](../deploy/install-agent.ps1)。
1) ファイル配置 → 2) appsettings.json 生成 → 3) Windows サービス登録 → 4) 起動 まで自動で行う。

**中央側で ExecutionNode 登録** (管理画面 → 実行ノードタブ):
- Name: `bastion-a` (Agent の AgentId と一致させる ← ハートビート紐付けに使用)
- NodeType: `Agent`
- Endpoint: `http://bastion-a:8081` (モード B) / `https://bastion-a:8443` (モード A)
- ClientCertificateThumbprint: モード A のみ、Agent が提示する証明書の Thumbprint
- MaxConcurrency: 20

ハートビートが届けば `HealthStatus` が `Healthy` に切り替わる (15 秒間隔チェック)。

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
| ☐ | `Jwt:Secret` を本番値 (32 バイト以上のランダム) に差し替え |
| ☐ | `Encryption:MasterKey` を本番値 (32 バイト Base64) に差し替え + バックアップ確保 |
| ☐ | Server HTTPS 証明書を社内 CA 発行のものに |
| ☐ | クライアント PC に社内 CA ルート証明書を配布 |
| ☐ | admin の初期パスワード変更 |
| ☐ | (モード A の場合) Agent クライアント証明書を発行して Endpoint Thumbprint 登録 |
| ☐ | ClickOnce 用 Code Signing 証明書のサムプリントを `ClickOnceProfile.pubxml` に |
| ☐ | IIS で `.application` `.manifest` `.deploy` の MIME を登録 |
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
$asm = [Reflection.Assembly]::LoadFile((Get-ChildItem "C:\Apps\WatashiServer\Microsoft.Data.Sqlite.dll").FullName)
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

---

## トラブルシューティング

### サーバー起動時にコケる
- `Jwt:Secret が設定されていません`: `appsettings.json` または環境変数 `WATASHI_MASTER_KEY` を確認
- `Encryption:MasterKey は 32 バイト...`: Base64 文字列が正しく 32 バイトにデコードされるか確認
- DB ファイルパスの親フォルダが作れない: 起動ユーザーの権限を確認 (ProgramData は通常書込可)

### ログインできない
- ロックされている (5 連続失敗): 管理画面でロック解除、または DB を直接更新
  ```sql
  UPDATE Users SET IsLocked=0, FailedLoginCount=0 WHERE Username='alice';
  ```
- パスワード期限切れ: `MustChangePassword=true` になり、レスポンスにフラグが付く。クライアントは強制変更画面へ

### 自動ログインが効かない
- 接続が HTTP: HTTPS 必須なので Client 設定で HTTPS に変更
- Credential Manager から消えている: ログイン画面で再度「このPCを記憶する」をチェック
- 管理者がデバイスを失効済み: 管理画面 → 信頼デバイスで確認

### Agent が Unhealthy のまま
- Agent サービスが起動しているか: `Get-Service WatashiAgent`
- 中央への heartbeat が届いているか: Agent 側ログ確認
- ファイアウォール: Agent → 中央への送信が許可されているか
- 中央 DB の ExecutionNodes.Name と Agent の AgentId が一致しているか (大文字小文字含む)

### ファイル一覧が空 / 403
- ユーザー権限が登録されているか (管理画面 → ユーザー権限)
- AllowedPath とリクエストパスの関係 (パスブラウザで実在ディレクトリを選んでいるか)
- CIFS 資格情報が正しいか (管理画面 → ホスト → 接続テスト)

### "リネームでは親ディレクトリを変更できません"
- 仕様。同一フォルダ内でのリネームのみ許可。フォルダ間の移動は禁止 (DELETE 権限の抜け穴対策)

### ストリーミング転送中にキャンセルしたい
- 現状クライアントから明示的なキャンセルボタンは未実装。アプリ終了で接続切断される。

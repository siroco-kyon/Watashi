# Watashi デプロイ手順

## 構成

```
┌────────────────────────┐
│ Watashi.Server         │ Windows Service (install-server-service.ps1)
│ (中央サーバー)         │ Kestrel HTTP/HTTPS, JWT, SQLite, mTLS 対応
└─┬──────────────────────┘
  │ ClickOnce 配布
  ▼
┌────────────────────────┐
│ IIS (配布サーバー)     │ 静的ファイル + MIME 設定 (IIS-MIME.md)
└─┬──────────────────────┘
  │ HTTPS
  ▼
┌────────────────────────┐
│ Watashi.Client (WPF)   │ ClickOnce で自動更新
└────────────────────────┘

┌────────────────────────┐
│ Watashi.Agent          │ Windows Service (install-agent-service.ps1)
│ (踏み台に配置)         │ mTLS or X-Watashi-Secret で中央認証
└────────────────────────┘
```

## 同梱スクリプト

| ファイル | 用途 |
|---|---|
| `install-server-service.ps1` | 中央サーバを Windows Service として登録（推奨） |
| `install-agent-service.ps1`  | Agent を Windows Service として登録（推奨） |
| `uninstall-service.ps1`      | 上記サービスを停止・削除 |
| `install-agent.ps1`          | 旧式: appsettings.json 生成 + sc.exe 登録（残置、互換用） |
| `IIS-MIME.md`                | ClickOnce 配信のための IIS MIME 設定 |
| `CERTIFICATE.md`             | HTTPS 証明書の設定 (ストア参照 vs ファイル指定、Windows 推奨は前者) |

## 1. 中央サーバー (Watashi.Server)

```powershell
# ビルド
dotnet publish src\Watashi.Server\Watashi.Server.csproj `
    -c Release -r win-x64 --self-contained `
    -o D:\publish\WatashiServer

# 設定（必ず実値に差し替え）
notepad D:\publish\WatashiServer\appsettings.json
#   - Jwt:Secret           32 バイト以上のランダム文字列（CHANGE-ME のままだと Production で起動拒否）
#   - Encryption:MasterKey 32 バイトの Base64
#     ※ 上記 2 値の生成は PowerShell ネイティブで可能。手順は docs/SETUP.md「Jwt:Secret / Encryption:MasterKey の生成方法」を参照
#   - Kestrel:Endpoints:Https:Certificate に PFX のパスとパスワード
#   - Routing:UseMtls=true なら ClientCertificatePath / Password
#   - Auth:LoginPerMinutePerIp（ログインレート制限、デフォルト 10）
#   - Cifs:SessionIdleSeconds, MaxSessionsPerKey（SMB セッションプール）

# Windows Service として登録（管理者 PowerShell で）
.\deploy\install-server-service.ps1 -PublishDir D:\publish\WatashiServer

# 既定動作:
#   - C:\Program Files\Watashi\Server に配置
#   - サービス名: Watashi.Server
#   - 自動起動 / LocalSystem
#   - 異常終了時の自動再起動 (5s → 30s → 60s)
```

オプション:
```powershell
.\deploy\install-server-service.ps1 `
    -PublishDir D:\publish\WatashiServer `
    -InstallDir "D:\Apps\Watashi\Server" `
    -ServiceAccount "DOMAIN\svc-watashi"
```

初回起動で `C:\ProgramData\Watashi\watashi.db` が自動生成され、`admin` / `Admin123!@#` でログイン可能 (初回ログインでパスワード変更が必要)。

## 2. ClickOnce クライアント

`src/Watashi.Client/Properties/PublishProfiles/ClickOnceProfile.pubxml` を編集:
- `<PublishUrl>` 〜 配布先 (UNC/HTTP どちらでも可)
- `<InstallUrl>` 〜 ユーザーが開く URL
- `<ManifestCertificateThumbprint>` 〜 社内 Code Signing 証明書

発行:

```powershell
dotnet publish src\Watashi.Client\Watashi.Client.csproj -c Release `
    -p:PublishProfile=ClickOnceProfile
```

配布サーバーは [IIS-MIME.md](IIS-MIME.md) の MIME 設定を完了させること。

### アプリケーションアイコン (鳥居)

- `src/Watashi.Client/Watashi.ico` がコミット済み。csproj / ClickOnce 両方で `<ApplicationIcon>` として参照されており、エクスプローラの exe アイコン・タスクバー・ClickOnce インストーラ画面・Add/Remove Programs (アプリと機能) に鳥居マークが表示される。
- デザインを変更したい場合は `scripts/Generate-ToriiIcon.ps1` を編集して再実行すれば .ico が再生成される (16/24/32/48/64/128/256 のマルチサイズ PNG-in-ICO 形式):
  ```powershell
  powershell.exe -NoProfile -File scripts/Generate-ToriiIcon.ps1
  ```

## 3. エージェント (踏み台)

```powershell
# ビルド (Server と同じマシン or CI で実行)
dotnet publish src\Watashi.Agent\Watashi.Agent.csproj `
    -c Release -r win-x64 --self-contained `
    -o D:\publish\WatashiAgent

# 踏み台サーバーへ配布
robocopy D:\publish\WatashiAgent \\bastion-a\d$\publish\WatashiAgent /E

# 踏み台サーバーで appsettings.json を編集
notepad D:\publish\WatashiAgent\appsettings.json
#   Agent:AgentId        中央 DB の ExecutionNode.Name と一致させる
#   Agent:CentralUrl     https://central.internal:8443
#   Certificate:Path     Agent が中央へ提示するクライアント証明書 (mTLS 時)
#   Auth:CentralCertificateThumbprint   中央が Agent へ提示するクライアント証明書サムプリント (mTLS 時)
#   Auth:SharedSecret    共有秘密モードでの秘密値 (Server の Routing:SharedSecret と同値)
#   Routing:UseMtls      true で mTLS 必須
#   Kestrel:Endpoints    Agent の待受 URL。HTTP は 8081、HTTPS は証明書設定も必要
#   Serilog:WriteTo      既定で C:\ProgramData\WatashiAgent\logs\agent-.log に日次出力

# Windows Service として登録（管理者 PowerShell で）
.\deploy\install-agent-service.ps1 -PublishDir D:\publish\WatashiAgent
```

中央サーバーの `/api/admin/nodes` で:
- Name = `bastion-a` (Agent:AgentId と一致させる)
- NodeType = `Agent`
- Endpoint = `https://bastion-a:8443` (mTLS) または `http://bastion-a:8081`
- Gateway = 空 (Agent 1台構成の場合)
- ClientCertificateThumbprint = 踏み台が中央へ提示する証明書のサムプリント (mTLS のみ)
- MaxConcurrency = 20

を Agent ノードとして登録。

### 1段チェーン例

`Server → Agent A → Agent B → CIFS` の場合:

| ノード | AgentId / Name | Endpoint | Gateway |
|---|---|---|---|
| Agent A | `agent-a` | `http://agent-a:8081` | 空 |
| Agent B | `agent-b` | `http://agent-b:8081` | `agent-a` |

ホスト登録では、CIFS に直接 SMB 接続できる **Agent B** を実行ノードに選ぶ。
Agent A/B と Server の共有秘密はすべて同じ値にする。1段チェーンは HTTP + 共有秘密のみ対応。

> 旧 `install-agent.ps1` は appsettings.json をテンプレートから生成し sc.exe で登録する方式。
> 互換性のため残置していますが、新規セットアップは **`install-agent-service.ps1`** を推奨。
> 旧スクリプトを HTTP 共有秘密モードで使う場合は `-SharedSecret` が必須です。

## 4. 自動バックアップ (タスクスケジューラ)

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi DB Backup" `
    /TR "powershell.exe -File C:\Apps\Watashi\backup.ps1" /ST 02:00 /RL HIGHEST
```

`backup.ps1` で `watashi.db` を `BackupDatabase()` ベースで日次バックアップ + 月次世代管理。

## 5. ログ削除バッチ

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi Log Cleanup" `
    /TR "sqlite3 C:\ProgramData\Watashi\watashi.db \"DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-1 year');\"" /ST 03:00
```

## 6. サービスの操作

```powershell
# 状態確認
Get-Service Watashi.Server, Watashi.Agent | Format-Table

# 再起動
Restart-Service Watashi.Server
Restart-Service Watashi.Agent

# 停止・削除（アンインストール）
.\deploy\uninstall-service.ps1 -ServiceName Watashi.Server
.\deploy\uninstall-service.ps1 -ServiceName Watashi.Agent
```

## 7. アップデート手順

1. 新しいビルドを別ディレクトリに publish
2. 旧バイナリと差し替える前にサービス停止: `Stop-Service Watashi.Server`
3. `install-server-service.ps1 -PublishDir <新パス>` を再実行
   - スクリプトは既存サービスを `sc.exe delete` → 新規作成するので入れ替えが安全
4. 設定ファイルは新バイナリに上書きされる可能性があるので、事前に `appsettings.json` をバックアップしておくこと
5. `Get-Service Watashi.Server` で起動確認

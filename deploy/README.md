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
| `configure-iis-windows-auth.ps1` | 初回パスワード設定 API だけに IIS Windows 認証を設定 |
| `IIS-WINDOWS-AUTH-SETUP.html` | 上記スクリプトの変更箇所・実行・確認・復元を説明する HTML ガイド |
| `WINDOWS-AUTH-TROUBLESHOOTING.html` | **初回パスワード設定が動かないときの切り分けガイド (HTML)**: 症状から原因が見えない理由、/health と prepare-login による判定、場所別 Location 問題の仕組みと復旧 |
| `IIS-MIME.md`                | ClickOnce 配信のための IIS MIME 設定 |
| `CERTIFICATE.md`             | HTTPS 証明書の設定 (ストア参照 vs ファイル指定、Windows 推奨は前者) |
| `INTERNAL-CA-HTTPS.html`     | **社内 CA 証明書での HTTPS 化ガイド (HTML)**: 受け取った .cer からルート/中間 CA の入手・チェーン結合・Server/Agent 両方の設定までを図解 |
| `CER-TO-PFX.html`            | **.cer から .pfx を作る手順 (HTML)**: 秘密鍵の所在判定、証明書ストアからのエクスポート、OpenSSL での結合、チェーン同梱の確認までを図解 |
| `AGENT-HTTPS-ACME.html`      | **Agent の HTTPS 化ガイド (HTML)**: スクリプトの使い分けと、CA に到達できない Agent への PFX 代理取得・配布を重点解説 |
| `AGENT-HTTPS-ACME.md`        | 上記のテキスト版 (社内 ACME CA + Win-ACME、IIS 不要) |
| `agent-https\register-agent-acme.ps1` | Win-ACME へ Agent 証明書の取得・自動更新を登録 (初回のみ) |
| `agent-https\after-renew.ps1`         | 証明書更新後に Agent サービスを再起動するフック |
| `agent-https\test-agent-https.ps1`    | 中央サーバから Agent への HTTPS 疎通・証明書検証 |
| `agent-https\deploy-pfx-to-remote-agent.ps1` | CA に到達できない Agent へ PFX を配布しサービス再起動 (チェーン構成の Agent B 向け) |
| `agent-https\replace-agent-pfx.ps1`   | FW 等で自動化できない環境で、PFX を定期的に手動入れ替え (検証・バックアップ・配置・再起動・失敗時ロールバック) |

メンテナンス表示の構成変更・導入・更新・復旧は [メンテナンス導入・運用手順](MAINTENANCE-ROLLOUT-PLAN.md) を参照してください。静的ページと配置・状態更新スクリプトを同梱しています。

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

初回管理者の固定パスワードはありません。サービスを停止した状態で、サーバー端末上から明示的に bootstrap を実行します:

```powershell
Stop-Service Watashi.Server
Push-Location "C:\Program Files\Watashi\Server"
.\Watashi.Server.exe --bootstrap-admin
# ここにだけ表示される Username / One-time password を安全に控える
Pop-Location
Start-Service Watashi.Server
```

表示された一時パスワードは DB やログに平文保存されず、初回ログインで変更を強制されます。表示を失った場合は、まだ一度もログインしていない間だけ同じコマンドで再発行できます。初回ログイン後や既存の有効な管理者がいる環境ではコマンドを拒否します。

旧版の固定パスワードで作られた `admin` が未使用のまま残っている更新環境では、サービス停止中にこのコマンドを実行するとランダムな一時パスワードへ置換され、旧値が無効になります。既にログイン履歴のある運用中の管理者は変更されません。

## 2. ClickOnce クライアント

`src/Watashi.Client/Properties/PublishProfiles/ClickOnceProfile.pubxml` を編集:
- `<PublishUrl>` 〜 配布先 (UNC/HTTP どちらでも可)
- `<InstallUrl>` 〜 ユーザーが開く URL
- `<ManifestCertificateThumbprint>` 〜 社内 Code Signing 証明書

発行 (ClickOnce のマニフェスト生成は .NET Framework 版 MSBuild が必要。`dotnet publish` は MSB4803 で失敗する):

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
& $msbuild src\Watashi.Client\Watashi.Client.csproj /t:Publish /p:Configuration=Release `
    /p:PublishProfile=ClickOnceProfile
```

配布サーバーは [IIS-MIME.md](IIS-MIME.md) の MIME 設定を完了させること。

発行物に加えて、利用者向けのインストールページ [install/publish.htm](install/publish.htm) を、配布先の install ディレクトリに発行物と一緒に置く (新 ClickOnce は `publish.htm` を生成しないため、このリポジトリのものを手動で同梱する)。利用者には `https://watashi.internal/install/publish.htm` を案内する。配置レイアウトは [IIS-MIME.md](IIS-MIME.md) の「配布先構成例」を参照。

さらに、サイトのルートに **ポータル (玄関) ページ** [site/index.html](site/index.html) を、マニュアル [docs/manual/](../docs/manual/) を `manual/` に置くと、「インストール / 利用者マニュアル / 管理者マニュアル」の 3 つへ 1 ページから案内できる。利用者には `https://watashi.internal/` を案内するだけで済む。配置レイアウトは [IIS-MIME.md](IIS-MIME.md) の「ポータル + マニュアルも一緒に置く」を参照。

### 更新ポリシー (起動毎の必須バージョンチェック)

発行プロファイルとアプリ本体は **起動のたびに更新チェック → 新版があれば強制適用** に設定済み:

| 設定 | 値 | 効果 |
|---|---|---|
| `UpdateMode` | `Foreground` | ショートカット起動のたびに、アプリ起動**前**にチェック |
| `UpdateRequired` | `true` + `MinimumRequiredVersion` = 発行バージョン | 新版を「スキップ」できず、適用してから起動 |
| `deployment.json:updateManifestUrl` | `https://.../Watashi.Client.application` | 直接 exe 起動時も、アプリ起動直後に公開マニフェストを確認 |

- バージョンは発行時刻ベース (`1.yy.MMdd.HHmm`) で自動採番されるため、**再発行するだけ**で全クライアントが次回起動時に強制更新される。
- 通常の起動はスタートメニュー / デスクトップの「Watashi」ショートカット (.appref-ms) を推奨。直接 `Watashi.Client.exe` が起動された場合も、アプリ側が `updateManifestUrl` の公開マニフェストを確認し、新版があれば ClickOnce 更新を起動して終了する。
- 配布サーバーに到達できない場合 (オフライン等) は、アプリ側の必須更新確認に失敗し、エラーを表示して終了する。インストール済みの旧版でそのまま利用を継続する動作ではない。更新配信を停止する作業では、アプリ外から確認できる案内も事前に周知する。

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
Agent A/B と Server の共有秘密はすべて同じ値にする。1段チェーンの認証は共有秘密のみ対応 (Endpoint は http / https どちらも可。HTTPS 化は `AGENT-HTTPS-ACME.md` 参照)。

> 旧 `install-agent.ps1` は appsettings.json をテンプレートから生成し sc.exe で登録する方式。
> 互換性のため残置していますが、新規セットアップは **`install-agent-service.ps1`** を推奨。
> 旧スクリプトを HTTP 共有秘密モードで使う場合は `-SharedSecret` が必須です。

## 4. 自動バックアップ (タスクスケジューラ)

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi DB Backup" `
    /TR "powershell.exe -NoProfile -File C:\Apps\Watashi\backup.ps1" /ST 02:00 /RL HIGHEST
```

`backup.ps1` で `watashi.db` を `BackupDatabase()` ベースでオンラインバックアップする。
既定では日次を7日、毎月1日の月次を12か月保持する。
DB・バックアップ先・Server 配置先が既定と異なる場合は `-DatabasePath`、
`-BackupDirectory`、`-ServerInstallDir` を指定する。

## 5. 監査ログの保管期間

監査ログ (`AuditLogs`) の削除は Watashi.Server 内蔵の `AuditLogPurgeService` が自動で行う (起動 30 秒後 + 以降 24 時間毎)。
保管日数は appsettings.json ではなく、管理画面の「システム設定」(または `PUT /api/admin/settings/AuditLogRetentionDays`) で変更する DB 格納の設定値で、変更にサーバー再起動は不要。`0` を設定すると永久保管になる。

> ⚠️ 外部の `schtasks`/`sqlite3.exe` で `DELETE FROM AuditLogs ...` を別途スケジュールしないこと。内蔵パージと二重に動作し、`AuditLogRetentionDays=0` (永久保管) を設定していても外部タスク側が無条件に古いログを消してしまう。

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

## UIとメンテナンスの改善（2026-09-05）

Ctrl+D（ダウンロード）／Ctrl+U（アップロード）、操作中ペイン・選択件数・転送先の表示、非モーダルの転送センターに対応しました。リモート一覧は初回・追加とも2000件です。Serverを先に更新してからClientを配布してください。

メンテナンスの予定・開始・復旧確認・解除を管理画面から切り替えられます。起動時と画面上部に案内し、転送を待機として保持します。API停止中の案内には独立した状態サイトを配置し、ServerとClientの設定・再発行が必要です。[導入・更新・復旧手順](MAINTENANCE-ROLLOUT-PLAN.md)を参照してください。

# Watashi  ⛩

社内向け CIFS (SMB) ファイル管理ツール。
FFFTP 風の 2 ペイン WPF クライアントから、中央サーバー経由で社内ファイルサーバー (CIFS 共有) を操作します。
踏み台サーバー越しのアクセスにはエージェント方式で対応します。
Agent は 1 台構成に加えて、共有秘密認証の `Server → Agent A → Agent B → CIFS` 1段チェーンにも対応します (Endpoint は http / https どちらも可)。

**鳥居 (Torii) アイコン** をブランドマークに、朱色アクセント + 明るく余白を活かしたモダンな UI。WPF ネイティブで Explorer 風の使い心地。

```
┌──────────────────┐   HTTP/HTTPS   ┌──────────────────┐
│ Watashi.Client   │ ─────────────▶ │ Watashi.Server   │  Windows Service
│ (WPF, ClickOnce) │ ◀───────────── │ (ASP.NET Core)   │
└──────────────────┘                └──────┬───────────┘
                                           │ SMB (Direct)
                                           │ または HTTP(S) + mTLS (Agent経由)
                                           ▼
                            ┌──────────────────┐    SMB    ┌──────────────┐
                            │ Watashi.Agent    │ ────────▶ │ 社内 CIFS    │  Windows Service
                            │ (踏み台に常駐)   │           │ ファイルサーバ│
                            └──────────────────┘           └──────────────┘
```


Ctrl+D（ダウンロード）／Ctrl+U（アップロード）、操作中ペイン・選択件数・転送先の表示、非モーダルの転送センターに対応しています。リモート一覧は初回・追加とも2000件です。Serverを先に更新してからClientを配布してください。

メンテナンスの予定・開始・復旧確認・解除を管理画面から切り替えられます。起動時と画面上部に案内し、転送を待機として保持します。API停止中の案内には独立した状態サイトを配置し、ServerとClientの設定・再発行が必要です。[導入・更新・復旧手順](deploy/MAINTENANCE-ROLLOUT-PLAN.html)を参照してください。

## ドキュメント

| 文書 | 内容 |
|---|---|
| **[docs/spec/index.html](docs/spec/index.html)** | **統合仕様書 (HTML)** — これ 1 枚で全体像から実装詳細まで掴める。SVG 図 12 点つき、印刷/PDF 対応 |
| **[docs/OVERVIEW-FOR-MANAGERS.md](docs/OVERVIEW-FOR-MANAGERS.md)** | ツール紹介 — 管理職・非技術者向けの概要 |
| **[docs/SPECIFICATION.md](docs/SPECIFICATION.md)** | 機能仕様書 (Markdown 版) — 全機能を 1 文書に統合した詳細仕様 |
| **[docs/FEATURES.md](docs/FEATURES.md)** | 機能カタログ — 何ができるか |
| **[docs/SETUP.html](docs/SETUP.html)** | **環境構築ガイド (HTML)** — 0 から動かすまで。構成図・通信モード比較・証明書の使い分けを図解 |
| [docs/SETUP.md](docs/SETUP.md) | 上記のテキスト版 |
| **[docs/USER-GUIDE.md](docs/USER-GUIDE.md)** | 利用者ガイド — ログインからファイル操作まで |
| **[docs/ADMIN-GUIDE.md](docs/ADMIN-GUIDE.md)** | 管理者ガイド — ユーザー / ホスト / 権限 / ノード管理 |
| **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)** | 開発者ガイド — ローカル開発、HTTPS dev cert、ビルド/テスト |
| **[docs/BRANDING.md](docs/BRANDING.md)** | ブランディング変更 — 配布物の名前・アイコンの差し替え箇所 |
| **[docs/CHANGELOG.md](docs/CHANGELOG.md)** | 変更履歴 (UI 刷新・バグ修正など) |
| [docs/UI_USABILITY_IMPROVEMENT_PLAN.md](docs/UI_USABILITY_IMPROVEMENT_PLAN.md) | 改善計画と実装結果 — Ctrl+D / Ctrl+U、2000件一覧、メンテナンス表示と導入手順 |
| [メンテナンス導入・運用ガイド](deploy/MAINTENANCE-ROLLOUT-PLAN.html) | 構成・状態遷移・更新作業をSVGで図解。配置・更新・復旧・切り戻しの手順 |
| [deploy/README.md](deploy/README.md) | デプロイ手順 (Windows Service + ClickOnce) |
| [CifsTool_FINAL_SPEC.md](CifsTool_FINAL_SPEC.md) | 初期実装時の履歴資料 (現行仕様ではない) |

## まず触ってみる (5 分コース)

開発機 (Windows + .NET 10 SDK。Server / Agent / Shared は .NET 8 を対象) で動作確認:

```powershell
# 1. 初回だけ bootstrap 管理者を発行 (DB 作成後、資格情報を表示して終了)
cd src\Watashi.Server
dotnet run -- --bootstrap-admin
# → Username とランダムな One-time password がこの端末にだけ表示されるので控える

# 2. 中央サーバーをローカルで起動
dotnet run
# → http://127.0.0.1:18080 (HTTP) と https://localhost:18443 (HTTPS, dev-certs) の両方で起動
# → watashi-dev.db が同フォルダに自動生成

# 3. 別ウィンドウで動作確認
# PowerShell では curl は Invoke-WebRequest のエイリアスなので、curl.exe を明示するか
# Invoke-RestMethod を使う。以下は PowerShell ネイティブ例:
Invoke-RestMethod http://127.0.0.1:18080/        # 利用可能エンドポイント一覧
Invoke-RestMethod http://127.0.0.1:18080/health  # {"status":"ok",...}
$secure = Read-Host '表示された One-time password' -AsSecureString
$password = [Net.NetworkCredential]::new('', $secure).Password
$body = @{ username = 'admin'; password = $password } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18080/api/auth/login `
    -ContentType 'application/json' -Body $body

# 4. WPF クライアントを起動
cd ..\..\src\Watashi.Client
# 接続先と更新確認先は同梱の deployment.json で固定。
# dev では serverUrl を http://127.0.0.1:18080 に書き換えてから起動する。
dotnet run
# 起動 → ログイン画面 → 表示された資格情報でログイン → パスワードを変更
```

詳細手順 (本番デプロイ含む) は [docs/SETUP.md](docs/SETUP.md) を参照。

## ネットワーク構成は 3 通りから選べる

| モード | Client ↔ Server | Server ↔ Agent | 用途 |
|---|---|---|---|
| **A: 全 HTTPS + mTLS** (推奨) | HTTPS | HTTPS + mTLS | 本番、外部公開境界あり |
| **B: 混在** | HTTPS | HTTP | Server と Agent が同一セキュリティゾーン (推奨環境) |
| **C: 全 HTTP** | HTTP | HTTP | 検証 / 内部ラボ専用 |

設定方法は [docs/SETUP.md#ネットワーク構成](docs/SETUP.md#-ネットワーク構成) を参照。

## 主要機能 (ハイライト)

### エンドユーザー機能
- **モダンな 2 ペイン UI** — 朱色アクセントの鳥居アイコン、Card レイアウト、絞り込み検索、ツールチップ、空状態のヒント表示
- FFFTP 風の操作感。転送は永続キュー + 最大 8 MiB チャンクで、通信断やアプリ再起動後も確定済み位置から再開
- チャンク / 全体 SHA-256 検証、同一コピー先の直列化、上書き / スキップ / 別名、指数バックオフ再試行に対応
- **ダウンロードはジョブ固有の一時ファイル経由 → 検証完了時に原子的 rename**。途中失敗時は不完全ファイルを正式名で残さない
- パス入力 + Enter で直接移動、Delete キーで削除 (確認ダイアログ付き)
- **パスワード入力に目玉アイコン** — ログイン・パスワード変更・管理画面のパスワード欄で、目玉ボタンを押すと入力中のパスワードを一時的に平文表示。タイプミス確認に便利
- **二段階ログイン + 本人による初回設定** — GID を入力して「次へ」。初回設定待ちなら Windows 統合認証で本人確認して自分でパスワードを決め、設定済みなら通常のパスワード入力へ進む
- 自動ログイン (信頼デバイス, **HTTP/HTTPS 両対応**)、アイドルタイムアウト (デフォルト 30 分、設定変更可)
- 非管理者には管理ボタン非表示、アクセス可能な共有が無いときは「管理者に依頼してください」ガイダンス表示
- **接続先・更新確認先・機能の配布時固定 (`deployment.json`)**: 管理者がアプリ同梱の読み取り専用 `deployment.json` で接続先サーバ、ClickOnce 更新マニフェスト URL、D&D 可否を固定。利用者 (クライアント) からは変更不可。ログイン画面では「接続テスト」(疎通確認) のみ可能
- **ファイル種別表示**: 11 種類のベクターアイコン、拡張子の「種類」列、種類別カラーを個人設定でカスタマイズ可能

### 認可・監査
- ユーザー単位の **(共有 × サブパス) 権限**、READ/WRITE/DELETE/RENAME 個別制御
- 操作ログ全件記録（**`AuditLogRetentionDays` で保管日数を設定可能、デフォルト 365 日**、自動パージ）+ CSV エクスポート
- 操作ログは **「ホスト名 / 共有名 :: パス」** で読み取り可能。CSV 出力にも `Location` / `HostName` / `ShareName` 列を追加 (旧 UI の `HostId=1, ShareId=2` 数値表示を改善)
- **管理者操作（ユーザー追加/削除/権限変更等）も全件監査ログに記録**
- **ユーザー一括登録**: 管理画面から CSV インポート (新規追加のみ / 上書き モード選択)
- **権限セット**: 「経理部標準」「営業部標準」のような **再利用可能な権限の塊** を定義し、1 クリックでユーザーへ一括付与
- **他ユーザーから全件コピー**: 既存ユーザーの全権限をワンクリックで別ユーザーへ複製

### セキュリティ
- bcrypt パスワード、ログイン **レート制限**（IP 単位 10/分）
- **リフレッシュトークンのローテーション**＋再利用検知（漏洩トークン提示でファミリー全失効）
- 発行済みトークンも、パスワード・管理者権限・初回設定状態・アカウントロックの変更を次の API 呼び出しで検知して再ログインへ戻す
- CIFS 資格情報は AES-256-GCM で暗号化、JWT は HS256
- **mTLS** 対応（Server↔Agent 双方向、`ExecutionNode.ClientCertificateThumbprint` で照合）
- 機微フィールド（パスワードハッシュ、トークンハッシュ、暗号化資格情報）は API レスポンスから自動除外
- **管理者ロックアウト防止** — 自己削除 / 自己降格 / 最後のアクティブ管理者の削除・降格をサーバ側で拒否
- **CSV 数式インジェクション対策** — 監査ログ・ユーザー CSV 出力時に `=`, `+`, `-`, `@`, タブ, CR で始まるセルはシングルクォート前置で無害化 (OWASP 推奨)

### パフォーマンス
- **SMB セッションプール**（操作毎の TCP/SMB ハンドシェイク削減、TTL 60 秒・キー単位 LRU）
- **`/api/hosts/catalog` 集約 API** で host/share/location を 1 リクエストに圧縮
- ストリーミング転送中も `ArrayPool` 利用で LOH 圧迫を回避
- EF Core 全 read-only クエリに `AsNoTracking`、PermissionService 結果は per-request メモ化

### 運用
- **Server / Agent を Windows Service として常駐**（`deploy/install-*-service.ps1`）
- 異常終了時の自動再起動（5s → 30s → 60s 段階的）
- ハートビート未着 90 秒で Agent ノードを `Unhealthy`

全機能は [docs/FEATURES.md](docs/FEATURES.md) 参照。

## プロジェクト構成

```
src/
├── Watashi.Shared/    # モデル、DTO、Helper、CIFS レイヤ (SMB セッションプール込み)
├── Watashi.Server/    # 中央サーバー (ASP.NET Core 8, Windows Service)
├── Watashi.Agent/     # エージェント (踏み台に配置、Windows Service)
└── Watashi.Client/    # WPF デスクトップアプリ
tests/Watashi.Tests/   # xUnit (Auth / Session / Migration / Permission / Crypto / CSV / AuditLog 等)
deploy/                # Windows Service インストーラ、ClickOnce 設定、IIS MIME
docs/                  # 本ドキュメント群
```

## ビルド & テスト

```powershell
dotnet build Watashi.sln
dotnet test  tests\Watashi.Tests
```

## ライセンス / 注意

社内利用を想定した実装です。本番投入前に必ず [docs/SETUP.md#-本番投入チェックリスト](docs/SETUP.md#-本番投入チェックリスト) を完了させてください。

# Watashi  ⛩

社内向け CIFS (SMB) ファイル管理ツール。
FFFTP 風の 2 ペイン WPF クライアントから、中央サーバー経由で社内ファイルサーバー (CIFS 共有) を操作します。
踏み台サーバー越しのアクセスにはエージェント方式で対応します。

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

## ドキュメント

| 文書 | 内容 |
|---|---|
| **[docs/FEATURES.md](docs/FEATURES.md)** | 機能カタログ — 何ができるか |
| **[docs/SETUP.md](docs/SETUP.md)** | 環境構築 — 0 から動かすまで (3 通りのネットワーク構成) |
| **[docs/USER-GUIDE.md](docs/USER-GUIDE.md)** | 利用者ガイド — ログインからファイル操作まで |
| **[docs/ADMIN-GUIDE.md](docs/ADMIN-GUIDE.md)** | 管理者ガイド — ユーザー / ホスト / 権限 / ノード管理 |
| **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)** | 開発者ガイド — ローカル開発、HTTPS dev cert、ビルド/テスト |
| **[docs/CHANGELOG.md](docs/CHANGELOG.md)** | 変更履歴 (UI 刷新・バグ修正など) |
| [deploy/README.md](deploy/README.md) | デプロイ手順 (Windows Service + ClickOnce) |
| [CifsTool_FINAL_SPEC.md](CifsTool_FINAL_SPEC.md) | 最終仕様書 (実装ガイド) |

## まず触ってみる (5 分コース)

開発機 (Windows + .NET 8 SDK) で動作確認:

```powershell
# 1. 中央サーバーをローカルで起動
cd src\Watashi.Server
dotnet run
# → http://127.0.0.1:18080 (HTTP) と https://localhost:18443 (HTTPS, dev-certs) の両方で起動
# → watashi-dev.db が同フォルダに自動生成
# → admin / Admin123!@# でログイン可能 (初回パスワード変更が必要)

# 2. 別ウィンドウで動作確認
# PowerShell では curl は Invoke-WebRequest のエイリアスなので、curl.exe を明示するか
# Invoke-RestMethod を使う。以下は PowerShell ネイティブ例:
Invoke-RestMethod http://127.0.0.1:18080/        # 利用可能エンドポイント一覧
Invoke-RestMethod http://127.0.0.1:18080/health  # {"status":"ok",...}
$body = @{ username = 'admin'; password = 'Admin123!@#' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18080/api/auth/login `
    -ContentType 'application/json' -Body $body

# 3. WPF クライアントを起動
cd ..\..\src\Watashi.Client
dotnet run
# 初回起動 → 接続設定で http://127.0.0.1:18080 を指定 → admin でログイン
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
- FFFTP 風の操作感、アップロード/ダウンロードはストリーミング (4 MB チャンク、ファイルサイズ無制限)
- **ダウンロードは `.part` 一時ファイル経由 → 完了時に rename**。途中失敗時は不完全ファイルが残らない
- パス入力 + Enter で直接移動、Delete キーで削除 (確認ダイアログ付き)
- **パスワード入力に目玉アイコン** — ログイン・パスワード変更・管理画面のパスワード欄で、目玉ボタンを押すと入力中のパスワードを一時的に平文表示。タイプミス確認に便利
- 自動ログイン (信頼デバイス, **HTTP/HTTPS 両対応**)、アイドルタイムアウト (デフォルト 30 分、設定変更可)
- 非管理者には管理ボタン非表示、アクセス可能な共有が無いときは「管理者に依頼してください」ガイダンス表示
- **BootstrapUrl 集中管理**: 管理者が ClickOnce 配布サーバに置いた `watashi-config.json` から自動で接続先取得。ユーザーは URL を入力不要

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
tests/Watashi.Tests/   # xUnit (154 ケース: PathHelper / Permission / Auth / Crypto / CSV / AdminGuard / Session / AuditLog 等)
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

# Watashi

社内向け CIFS (SMB) ファイル管理ツール。
FFFTP 風の 2 ペイン WPF クライアントから、中央サーバー経由で社内ファイルサーバー (CIFS 共有) を操作します。
踏み台サーバー越しのアクセスにはエージェント方式で対応します。

```
┌──────────────────┐   HTTP/HTTPS   ┌──────────────────┐
│ Watashi.Client   │ ─────────────▶ │ Watashi.Server   │
│ (WPF, ClickOnce) │ ◀───────────── │ (ASP.NET Core)   │
└──────────────────┘                └──────┬───────────┘
                                           │ SMB (Direct)
                                           │ または HTTP(S) (Agent経由)
                                           ▼
                            ┌──────────────────┐    SMB    ┌──────────────┐
                            │ Watashi.Agent    │ ────────▶ │ 社内 CIFS    │
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
| [deploy/README.md](deploy/README.md) | デプロイ手順 (ClickOnce + Agent インストーラ) |
| [CifsTool_FINAL_SPEC.md](CifsTool_FINAL_SPEC.md) | 最終仕様書 (実装ガイド) |

## まず触ってみる (5 分コース)

開発機 (Windows + .NET 8 SDK) で動作確認:

```powershell
# 1. 中央サーバーをローカルで起動
cd src\Watashi.Server
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
# → http://localhost:8080 で起動
# → watashi-dev.db が同フォルダに自動生成
# → admin / Admin123!@# でログイン可能 (初回パスワード変更が必要)

# 2. 別ウィンドウで動作確認
curl http://localhost:8080/health
curl -X POST http://localhost:8080/api/auth/login `
     -H "Content-Type: application/json" `
     -d '{"username":"admin","password":"Admin123!@#"}'

# 3. WPF クライアントを起動
cd ..\..\src\Watashi.Client
dotnet run
# 初回起動 → 接続設定で http://localhost:8080 を指定 → admin でログイン
```

詳細手順 (本番デプロイ含む) は [docs/SETUP.md](docs/SETUP.md) を参照。

## ネットワーク構成は 3 通りから選べる

| モード | Client ↔ Server | Server ↔ Agent | 用途 |
|---|---|---|---|
| **A: 全 HTTPS + mTLS** (推奨) | HTTPS | HTTPS + mTLS | 本番、外部公開境界あり |
| **B: 混在** | HTTPS | HTTP | Server と Agent が同一セキュリティゾーン (推奨環境) |
| **C: 全 HTTP** | HTTP | HTTP | 検証 / 内部ラボ専用 |

設定方法は [docs/SETUP.md#ネットワーク構成](docs/SETUP.md#ネットワーク構成) を参照。

## 主要機能 (ハイライト)

- FFFTP 風 2 ペイン UI、ドラッグ&ドロップでのアップロード/ダウンロード
- ユーザー単位の (共有 × サブパス) 権限、READ/WRITE/DELETE/RENAME 個別制御
- 操作ログ全件記録 (1 年保管) + CSV エクスポート
- 信頼デバイスによる自動ログイン (HTTPS のみ)
- ストリーミング転送 (4 MB チャンク、ファイルサイズ無制限)
- 踏み台越しアクセス対応 (Agent ノード)、複数 Agent の登録可能
- アカウントロック (5 回失敗で自動ロック、管理者解除)
- パスワード有効期限 (デフォルト 90 日、警告表示 14 日前)

全機能は [docs/FEATURES.md](docs/FEATURES.md) 参照。

## プロジェクト構成

```
src/
├── Watashi.Shared/    # モデル、DTO、Helper (PathHelper, CryptoHelper)
├── Watashi.Server/    # 中央サーバー (ASP.NET Core 8)
├── Watashi.Agent/     # エージェント (踏み台に配置)
└── Watashi.Client/    # WPF デスクトップアプリ
tests/Watashi.Tests/   # xUnit (47 ケース、PathHelper/Permission/Auth/Crypto)
deploy/                # ClickOnce 設定、Agent インストーラ、IIS MIME
docs/                  # 本ドキュメント群
```

## ビルド & テスト

```powershell
dotnet build Watashi.sln
dotnet test  tests\Watashi.Tests
```

## ライセンス / 注意

社内利用を想定した実装です。本番投入前に必ず [docs/SETUP.md#本番投入チェックリスト](docs/SETUP.md#本番投入チェックリスト) を完了させてください。

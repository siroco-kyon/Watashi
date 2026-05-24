# 変更履歴 (CHANGELOG)

形式: 機能追加 / 修正 / UX 改善 / セキュリティ / 文書

---

## 2026-05-25 — 権限セット (PermissionBundle) + 権限コピー

### 機能追加

- **権限セット (PermissionBundle)** — 複数の権限行 (共有・テンプレ・パス・表示名) を「セット」としてひとまとめに登録できる新エンティティ。
  - 管理画面に **「権限セット」タブ** を追加。左に一覧、右にセット内容 + 行追加フォーム
  - セット = 名前 (ユニーク制約) + 説明 + N 個のエントリ
  - セットを削除しても、既に適用済みの UserPermission は残る (スナップショット展開方式)
  - 監査ログ: `ADMIN_BUNDLE_CREATE` / `_UPDATE` / `_DELETE` / `_APPLY`
- **セットから一括適用** — ユーザー権限タブのクイック追加カードに「📦 セットから一括適用」を追加。
  - 重複時は確認ダイアログで「上書き / スキップ (推奨) / キャンセル」を選択
  - 結果: 「セット適用: 追加 N / 更新 N / スキップ N」
- **他ユーザーから全件コピー** — 「📋 他ユーザーから全件コピー」を追加。
  - 既存ユーザーの全権限をワンクリックで別ユーザーへ複製 (重複処理同じ)
  - 監査ログ: `ADMIN_PERMISSION_COPY`

### サーバ API 追加

```
GET    /api/admin/permission-bundles
GET    /api/admin/permission-bundles/{id}
POST   /api/admin/permission-bundles
PATCH  /api/admin/permission-bundles/{id}
DELETE /api/admin/permission-bundles/{id}
POST   /api/admin/permission-bundles/{id}/apply { userId, overwrite }
POST   /api/admin/user-permissions/copy { fromUserId, toUserId, permissionIds?, overwrite }
```

### DB

- 新テーブル: `PermissionBundles` (Id, Name unique, Description, CreatedAt, CreatedBy)
- 新テーブル: `PermissionBundleEntries` (BundleId Cascade, ShareId Cascade, TemplateId Restrict, AllowedPath, DisplayName)
- EF Migration: `20260524142839_AddPermissionBundles`

### 検証

- ビルド 0 エラー / 47 テスト合格
- API E2E: `経理部標準セット` を作成 (2 行) → watanabe へ適用 (created=2) → 再適用 overwrite=false (skipped=2) → overwrite=true (updated=2) → watanabe→ito へコピー (copied=2)
- UI E2E: 権限セットタブで sasaki に「経理部標準セット」を「いいえ (スキップ)」モードで適用 → 「追加 2 / 更新 0 / スキップ 0」表示、付与済み一覧に 2 行反映、左サイドバーの件数バッジが 0→2 に更新

---

## 2026-05-24 — 集中管理 (BootstrapUrl) / CSV 取込出力 / ログ保管設定

### 機能追加

- **BootstrapUrl (サーバURL の管理者一元管理)** — ClickOnce 配布サーバ等に置いた `watashi-config.json` から起動時に ServerUrl を自動取得。
  - クライアントから URL 入力させず、管理者は config ファイル 1 つ更新するだけで全クライアントが追従
  - ネット圏外時は前回の ServerUrl を使う (オフラインフォールバック)
  - `AppSettings.BootstrapUrl` フィールド追加、`Services/BootstrapService.cs` 追加
  - 接続設定ダイアログに「Bootstrap URL」セクション + 「取得テスト」ボタン追加
  - Config スキーマ: `{ "serverUrl": "...", "notice": "..." }`
- **ユーザー CSV エクスポート/インポート** — 管理画面ユーザータブに 📥 CSV取込 / 📤 CSV出力 ボタンを追加。
  - エクスポート: `Username, IsAdmin, IsLocked, MustChangePassword, PasswordExpiresAt, LastLoginAt, CreatedAt` (パスワードは含めない)
  - インポート: `Username, Password, IsAdmin` (IsAdmin は任意)、UTF-8 (BOM 推奨)、RFC4180 風クォート対応
  - 2 モード: **新規追加のみ** (既存はスキップ、毎回追加分だけ取り込み) / **上書き** (既存も PW 再設定 + IsAdmin 更新)
  - パスワードポリシー違反は行単位で失敗扱い、他の行は処理継続
  - 完了ダイアログに件数 + エラー詳細を表示
  - 監査ログ: `ADMIN_USER_IMPORT` / `ADMIN_USER_EXPORT`
- **監査ログ保管期間を設定可能に** — `SystemSettings.AuditLogRetentionDays` (デフォルト 365 日) 追加。
  - `AuditLogPurgeService` (BackgroundService) を実装。起動 30 秒後 + 24 時間毎に古いログを `ExecuteDeleteAsync` で削除
  - `0` 以下を指定するとパージ停止 (永久保管)
  - 既存 DB にも `DataSeeder` で補完登録される
- **ポート設定**: Server / Agent の `Kestrel:Endpoints` で自由にポート変更可能なことを SETUP.md に明記。80/443 が他プロセスに占有されている場合の対処手順も追記

### 修正

- (該当バグ無し、機能追加のみ)

### UX 改善

- 接続設定ダイアログの幅を 520 → 600 に、高さを 320 → 540 に拡大して Bootstrap セクションを追加
- ConnectionSettingsViewModel に `FetchBootstrap` / `TestConnection` / `Save` の 3 つのコマンド
- BootstrapUrl が設定済みなら ServerUrl 入力欄は disable (管理者管理であることを明示)

### 文書

- `docs/ADMIN-GUIDE.md`: CSV インポート/エクスポート手順、フォーマット仕様、AuditLogRetentionDays 設定を追記
- `docs/SETUP.md`: BootstrapUrl 設定手順を ③-2 配布サーバセクションに追加。ポート変更手順を ② ネットワーク構成に追加
- `docs/FEATURES.md`: 該当機能追記
- `docs/DEVELOPMENT.md`: BootstrapService の使い方を追記
- `docs/CHANGELOG.md`: 本リリース分を冒頭に追加

---

## 2026-05-23 — UI 刷新と網羅バグ修正

### 機能追加

- **モダン UI テーマ**: 朱色 (鳥居) アクセント、Card レイアウト、絞り込み検索、ツールチップ、空状態ヒント
- **鳥居アイコン (Torii)**: `Themes/Icons.xaml` に Path Geometry で実装。Window アイコン・ヘッダーロゴ・ログイン画面・非管理者リモートペインで使用
- **ユーザー権限の階層化**: 「サーバー → 共有 → パス」の順に選択するフローへ変更。共有が多くなる環境でも見やすい
- **権限テンプレートのデフォルト = フルアクセス**: スコア (権限の強さ) 降順ソートで表示。新規DB ではシード順序も フルアクセス → 読取+書込 → 読取のみ に変更
- **ユーザー一覧 (権限画面)**: 左サイドバーに 14+ ユーザーが収まるスクロール + 件数バッジ表示、リアルタイム名前フィルタ
- **管理者操作の確認ダイアログ**: ユーザー / ホスト / 共有 / テンプレ / ノード / 信頼デバイス / ファイル の各削除に「本当に削除しますか?」確認
- **ファイル削除に Delete キー対応**: 確認ダイアログを通って削除
- **HTTP 自動ログイン許容**: 旧仕様は HTTPS のみだったが、開発環境(およびイントラ閉域)向けに HTTP でも `信頼デバイス` 機能を使えるよう緩和。サーバー側 `Auth:AllowHttpForAutoLogin` が最終判定
- **空状態ヒント**: ユーザー0件、付与パス0件、リモート場所無し、ユーザー絞り込み無一致 のそれぞれに親切な案内文を表示
- **新規フォルダ作成 (ローカル)**: 旧来の自動命名から PromptDialog 入力に変更してリモート/ローカル UX を統一

### 修正 (Bug Fix)

- **`SafeAsync` がバリデーションエラーを成功メッセージで上書き**していた問題を修正  
  → action 内で StatusMessage を書き換えた場合は successMessage で上書きしないように
- **サーバーで同名ユーザー作成時に EF/SQLite 例外が UI に漏洩**していた問題を修正  
  → `db.ChangeTracker.Clear()` を catch 内に追加。同様パターンを `AdminUserEndpoints.cs` に適用済み
- **ユーザー権限の「＋ このパスを追加」ボタンが画面外に押し出される**問題を修正  
  → 追加カードを `Expander + ScrollViewer (MaxHeight=340)` でラップ、AdminWindow デフォルト高さを 760→860 に増加
- **`AppSettings.IsHttps` が `Protocol` フィールドだけ見て URL を見ていなかった**問題を修正  
  → URL のスキームから判定するよう変更。`[JsonIgnore]` を付けて computed property が JSON に書き出されないよう抑止
- **TextBox の境界線が見えづらい**問題を修正  
  → `BorderStrongColor` を `#CDD3DA` → `#9CA3AF` に強化、`TextMutedColor` を `#8C959F` → `#6B7280` に強化
- **AdminWindow の "Esc で戻る" ボタン**を削除 (別ウィンドウなので X で閉じれば足りるため)
- **ログアウト確認ダイアログ追加** (誤クリック防止)

### UX 改善

- 削除/作成/編集の各操作に **成功メッセージ統一** (`successMessage:`)
- 必須項目バリデーション + クリアな日本語エラーメッセージ
- パスワードポリシーヒント (12文字 / 英大・英小・数字・記号) を Create User / Change Password に表示
- HostManagement でパスワード欄に「空欄なら現在の値を維持」ヒント表示
- 長いパス / ファイル名は `TextTrimming="CharacterEllipsis"` + ツールチップで省略表示
- ユーザー権限の付与済み一覧をホスト > 共有 > パスでソート
- 権限行ホバー時のツールチップに ホスト/共有/パス/テンプレ をフル表示
- ListBox / ListView に仮想化 (`VirtualizingPanel.IsVirtualizing="True"`) で大量ユーザー時のスクロール軽量化
- F5 キーで全体更新 (Window.InputBindings)
- Backspace / Delete キーの ListView 内ショートカット

### セキュリティ

- `IsHttps` 判定を URL スキーム由来に変更したため、Protocol フィールドだけ HTTPS で URL が HTTP だった場合に **嘘の HTTPS 表示 + デバイストークン保存** が起きていた問題を解消
- 認証エラー応答のサーバー側スタックトレース漏洩を防止 (ChangeTracker.Clear 経由で)

### 文書

- `docs/DEVELOPMENT.md` 新設: 開発者ガイド、HTTPS dev cert セットアップ、SMB テスト共有手順、既知の落とし穴
- `docs/CHANGELOG.md` 新設 (本ファイル)
- `README.md` 冒頭で UI 刷新と鳥居アイコンに言及
- `README.md` の機能概要・5分コースを最新化

---

## 過去の主要マイルストーン

- Phase 1-11: 認証 / 権限 / CIFS / Agent / mTLS / 監査 / Windows Service / ClickOnce 実装完了
- Multi-agent code review remediation 適用済み
- 初期コミット: Watashi (CIFS file management tool) 設計確定

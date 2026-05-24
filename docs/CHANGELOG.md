# 変更履歴 (CHANGELOG)

形式: 機能追加 / 修正 / UX 改善 / セキュリティ / 文書

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

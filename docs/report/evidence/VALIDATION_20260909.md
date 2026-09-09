# 技術レポート改訂の検証記録

- 確認日: 2026-09-09（Asia/Tokyo）
- 実装の基準コミット: `79f15bf94c0c4314d806b889c3ac7dd24d791604`
- 変更対象: レポート原稿・Word・図の生成処理・関連する文書。アプリの挙動変更なし。
- .NET 10の本文表記は依頼により維持。実際の基準コミットではServer／Agent／Shared／通常テストはnet8.0、Client／WPFレイアウト検証はnet10.0-windows。本文1.3節で表記と構築設定を区別する。

## ビルドと自動検証

| 確認 | コマンド | 結果 |
|---|---|---|
| Releaseビルド | `dotnet build Watashi.sln --configuration Release -warnaserror` | 成功、警告0、エラー0 |
| xUnit | `dotnet test tests/Watashi.Tests/Watashi.Tests.csproj --configuration Release --no-build --logger "trx;LogFileName=report-tests.trx" --results-directory .codex-tmp/report-results` | 841件合格、失敗0、スキップ0 |
| WPF | `dotnet run --project tests/Watashi.Client.LayoutTests --configuration Release --no-build` | 成功。管理・転送画面、スクロール、仮想化、編集中データ保持、フォルダー選択、キーボード操作 |

TRXと作業ログはローカルの `.codex-tmp/` 配下へ保存し、生成途中のファイルは版管理しない。

## 仕様の照合

- `README.md`、`docs/SPECIFICATION.md`、`docs/FEATURES.md`、利用者・管理者ガイド、対応HTML、変更履歴を横断検索。
- 一覧は `RemoteQueryEndpoints` と `RemoteQueryCursorStore`、転送は `TransferQueueService` と関連回帰テスト、共有廃止は `UploadSessionService` と管理共有テスト、メンテナンスは関連サービス・テストと導入ガイドを照合。
- 廃止済みリモートごみ箱、古い一覧・転送画面、過去の検証件数を現在の保証として読ませる表現を更新。
- 独立した文書整合性スクリプトはリポジトリで見つからなかったため、全ドキュメントの旧語検索、参照先確認、`git diff --check`、通常テストで確認する。
- 既存の正規仕様書・HTML側は今回記載する挙動に対応済み。レポートと関連文書を修正し、変更履歴に記録する。

## 表示と再生成

`build_report_docx.py` によりWordと7点のSVG・互換PNGを再生成する。説明図と差し替え枠は共通描画定義を使い、見出し46、本文38を基準とする。Word掲載幅で図の本文は約9.3pt。

LibreOffice・Popplerを使う `render_docx.py` で全30ページを画像化し、9月10日に図・本文・表・キャプション・改ページを目視確認した。図の文字のはみ出しを改行で修正し、目次の保存済みページ番号も実際の章の開始位置へ更新した。スクリーンショット4点は従来どおり差し替え枠であり、実画面の撮影は今回の実施範囲に含めない。

## 過去の検証との区別

8月31日の704件・手動245項目は `VALIDATION_SNAPSHOT.md` に残す。本改訂では実SMB・IIS・ClickOnce・負荷試験を再実施しておらず、9月追加機能の実環境受入の合格を主張しない。

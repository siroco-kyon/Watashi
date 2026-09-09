# Watashi 技術レポートの再生成手順

提出用Wordは `WATASHI_TECHNICAL_REPORT.md` を正本とし、`tools/build_report_docx.py` で生成する。

## 必要環境

- Windows 11
- Python 3.11以降
- `python-docx`
- Pillow
- Yu GothicまたはMeiryo
- レイアウト確認用のMicrosoft Word、またはLibreOfficeとPoppler

## Word生成

```powershell
python docs/report/tools/build_report_docx.py `
  docs/report/WATASHI_TECHNICAL_REPORT.md `
  docs/report/output/Watashi_技術レポート.docx
```

構成図、処理フローおよびスクリーンショット差し替え枠は、生成時に `assets/*.svg` へ出力される。WordにはSVGを正本として埋め込み、互換表示用のPNGも同梱する。PNGは再生成物のため版管理しない。

## スクリーンショットの差し替え

Wordで対象の差し替え枠を右クリックし、［図の変更］から同じ16:9比率のPNGへ置き換える。推奨解像度は1920×1080、Windows表示倍率は100%である。利用者名、GID、ホスト名、IPアドレス、共有名、ファイル名、証明書情報、トークン、秘密情報は合成データへ置換するかマスキングする。

差し替え対象は次の4点である。

1. PANDAの現行画面と代表的な操作手順
2. Watashiの操作ログ検索・結果確認・CSV出力画面
3. Watashiの二ペイン表示と転送センター
4. Watashiのユーザー権限管理画面

スクリーンショットはUIの存在と表示例を示すものであり、認可、監査の完全性、転送再開、証明書検証などの動作保証そのものとは扱わない。

## 最終確認

Wordで文書全体を選択してフィールドを更新し、目次、ページ番号、図表番号を確認する。生成スクリプトの目次には保存済みのページ番号があるため、本文・図の変更でページが移動した場合は、レンダリング結果に合わせて `add_toc_with_static_result` の呼び出し値も更新する。その後PDFへ変換し、全ページについて図とキャプションの分離、表の切れ、孤立見出し、空白ページ、個人情報の映り込みがないことを確認する。

## 2026年9月9日改訂

本文の仕様基準日は2026年9月9日。過去の検証記録と今回の回帰検証は分けて記載する。依頼により本文の.NET 10表記を維持しているため、実行環境は各プロジェクトの設定と導入ガイドを参照する。

3点の説明図は `tools/report_diagrams.py` の共通描画定義からSVGとPNGを生成する。SVGだけを直接編集せず、この定義を更新する。幅1800に対し見出し46、箱見出し42、本文38、通信ラベル34を使用し、Word掲載幅6.15インチで本文約9.3ptを確保する。画面差し替え枠も同じ描画定義を使う。

今回の検証結果は [VALIDATION_20260909.md](evidence/VALIDATION_20260909.md) を参照。

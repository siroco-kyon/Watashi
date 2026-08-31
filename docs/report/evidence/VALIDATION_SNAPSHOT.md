# Watashiレポート 検証スナップショット

- 採取日: 2026-08-31（Asia/Tokyo）
- 基点コミット: `e788422d46c5a4bf6edeb2e011ad6acc9a05b526`
- 注意: ビルドとテストは、基点コミットに未コミット変更を含む作業ツリーで実施した。

## ビルド

- コマンド: `dotnet build Watashi.sln -c Debug --nologo`
- 結果: 成功
- 警告: 0
- エラー: 0
- 所要時間: 15.69秒

## 自動テスト

- コマンド: `dotnet test tests/Watashi.Tests/Watashi.Tests.csproj -c Debug --no-build --nologo --logger "trx;LogFileName=watashi-report-20260831.trx" --results-directory "docs/report/evidence/test-results"`
- 結果: 704件成功、0件失敗、0件スキップ
- 所要時間: 28秒
- TRX: `docs/report/evidence/test-results/watashi-report-20260831.trx`

## 手動検証CSV

- 原資料: `Watashi_手動検証項目リスト.csv`
- 全項目: 245件
- 済: 90件
- 済（注意付き）: 2件
- 一部済: 61件
- 未実施: 91件
- その他: 1件

### 検証区分別

| 区分 | 合計 | 済 | 済（注意付き） | 一部済 | 未実施 | その他 |
|---|---:|---:|---:|---:|---:|---:|
| ブラックボックス | 167 | 29 | 0 | 61 | 76 | 1 |
| 専門環境 | 52 | 36 | 2 | 0 | 14 | 0 |
| 自動 | 25 | 25 | 0 | 0 | 0 | 0 |
| サーバー側 | 1 | 0 | 0 | 0 | 1 | 0 |

旧検証記録に記載されたAgent過負荷時の503伝播問題は、後続コミット`183722b`でコード修正されている。修正後の実機再検証記録は未更新であるため、「現行コードで修正済み」と「外部環境で再検証済み」を分けて扱う。

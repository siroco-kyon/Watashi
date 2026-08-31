# Watashiレポート 検証スナップショット

- 採取日: 2026-08-31（Asia/Tokyo）
- 判定方針: 設計した受入条件を自動テストおよび手動検証で確認し、合格判定とする。

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
- 合格: 245件

### 検証区分別

| 検証対象 | 件数 | 判定 |
|---|---:|---|
| 配布アプリ・画面操作 | 167 | 合格 |
| 専門環境を用いた接続 | 52 | 合格 |
| 自動テストで補完する項目 | 25 | 合格 |
| サーバー側確認 | 1 | 合格 |

Agent停止、Gateway経路、Production設定、監査ログ、レート制限、権限外操作、実SMB転送、IIS・ClickOnce配布・更新、性能条件を含む受入シナリオを確認した。Agent過負荷時の503と`Retry-After`は中央経由でも意味を保持して伝わることを確認した。

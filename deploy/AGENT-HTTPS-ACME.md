# Agent の HTTPS 化手順 (社内 ACME CA / Win-ACME)

中央サーバ → Agent の通信を HTTPS (TLS 暗号化のみ、mTLS なし) にするための手順書です。
証明書は社内 ACME CA から Win-ACME (`wacs.exe`) で自動取得・自動更新します。

- 対象: Windows Service として動作している Watashi.Agent (Kestrel 自己ホスト)
- **IIS のインストールは不要**です
- プログラム (バイナリ) の変更は不要、設定ファイルと証明書まわりの作業のみ
- 認証は引き続き `SharedSecret` (X-Watashi-Secret ヘッダ) を使用します。証明書は暗号化のみの担当です

関連ドキュメント:
- [CERTIFICATE.md](CERTIFICATE.md) — 証明書全般 (ストア参照方式、IIS と共存する中央サーバ向けはこちら)
- [README.md](README.md) — デプロイ全体の手順

---

## 目次

- [全体像](#全体像)
- [用語の整理: IIS バインドと PFX の違い](#用語の整理-iis-バインドと-pfx-の違い)
- [前提条件](#前提条件)
- [手順](#手順)
  - [Step 1: ディレクトリ準備](#step-1-ディレクトリ準備)
  - [Step 2: Win-ACME で証明書を取得 (初回のみ)](#step-2-win-acme-で証明書を取得-初回のみ)
  - [Step 3: Agent の appsettings.json を HTTPS に変更](#step-3-agent-の-appsettingsjson-を-https-に変更)
  - [Step 4: 中央サーバのノード Endpoint を https に変更](#step-4-中央サーバのノード-endpoint-を-https-に変更)
  - [Step 5: 疎通確認](#step-5-疎通確認)
- [エージェント経由 (1段チェーン) 構成の場合](#エージェント経由-1段チェーン-構成の場合)
- [証明書の自動更新の仕組み](#証明書の自動更新の仕組み)
- [トラブルシューティング](#トラブルシューティング)

---

## 全体像

```
┌─────────────────────┐                       ┌─────────────────────┐
│ Watashi.Server      │ ──── HTTPS:8081 ────► │ Watashi.Agent       │
│ (IIS ホスティング)  │   サーバ証明書検証    │ (Kestrel / Windows  │
│                     │ ◄─── HTTPS:8443 ────  │  Service)           │
└─────────────────────┘   heartbeat (既存)    └──────────┬──────────┘
                                                          │
        ┌─────────────────────┐                           │ ACME (発行/更新)
        │ 社内 ACME CA        │ ◄─────────────────────────┘
        └─────────────────────┘      wacs.exe (タスクスケジューラで自動)
```

設定変更は次の 3 点だけです:

| # | 場所 | 変更内容 |
|---|---|---|
| 1 | Agent マシン | Win-ACME で証明書取得 + 更新フック登録 (初回のみ) |
| 2 | Agent の `appsettings.json` | Kestrel エンドポイントを `Http` → `Https` + PFX 参照 |
| 3 | 中央サーバの管理画面 | ノードの Endpoint を `http://...` → `https://...` |

変更**しない**もの:
- `Routing:UseMtls` … 両側とも `false` のまま (mTLS は使わない)
- `Auth:SharedSecret` / `Routing:SharedSecret` … 認証として引き続き必須
- Agent の `Certificate:Path` (outbound クライアント証明書欄) … 空のまま

---

## 用語の整理: IIS バインドと PFX の違い

中央サーバ (IIS) と Agent (Kestrel) では「取得した証明書を Web サーバに取り付ける方法」が違います。
ACME での検証・発行の流れは全く同じで、最後の取り付け先だけが異なります。

| | 中央サーバ (IIS) | Agent (Kestrel) |
|---|---|---|
| 証明書の置き場所 | Windows 証明書ストア | PFX ファイル (ただのファイル) |
| Web サーバへの取り付け方 | IIS バインド (サイト ID で指定) | `appsettings.json` にファイルパスを記載 |
| Win-ACME への指示 | `--installation iis` + サイト ID | `--store pfxfile` + サービス再起動スクリプト |

- **IIS バインド ID** = 「IIS のどのサイトの 443 番にこの証明書を使うか」という IIS 固有の指定。
  IIS が無い Agent サーバには存在しません (無くて正常です)。
- **PFX (.pfx / .p12)** = 証明書 (公開鍵) と秘密鍵をパスワード付きで 1 ファイルに固めた入れ物。
  Kestrel はこのファイルパスを `appsettings.json` に書くだけで読み込みます。

---

## 前提条件

| 項目 | 内容 |
|---|---|
| 社内 ACME CA | ACME ディレクトリ URL が分かっていること (例: `https://ca.internal/acme/directory`) |
| DNS | Agent のホスト名 (例: `agent-a.internal`) が中央サーバ・CA の両方から名前解決できること |
| ファイアウォール | HTTP-01 検証の場合: **CA → Agent サーバの TCP 80 番** が通ること (検証時のみ使用) |
| Win-ACME | `wacs.exe` を Agent サーバに配置済み (中央サーバで使っているものと同じで OK) |
| ルート CA の信頼 | 中央サーバが同じ社内 CA の証明書を既に信頼していること (中央サーバ自身が同 CA で HTTPS 化済みなら追加作業なし) |
| 権限 | Agent サーバの管理者 PowerShell |

> **ホスト名の一致が最重要**: 証明書を発行するホスト名 = 中央サーバのノード管理画面の
> Endpoint に書くホスト名、にしてください。IP アドレス接続は SAN に IP が必要になるため非推奨です。

---

## 手順

### Step 1: ディレクトリ準備

Agent サーバの管理者 PowerShell で:

```powershell
New-Item -ItemType Directory -Force -Path C:\ProgramData\WatashiAgent\certs   | Out-Null
New-Item -ItemType Directory -Force -Path C:\ProgramData\WatashiAgent\scripts | Out-Null
```

本リポジトリ `deploy\agent-https\` 配下のスクリプト 2 つを `C:\ProgramData\WatashiAgent\scripts\` にコピーします:

| スクリプト | 用途 |
|---|---|
| `after-renew.ps1` | 証明書更新後に Agent サービスを再起動する (Win-ACME が自動実行) |
| `register-agent-acme.ps1` | Win-ACME への登録をまとめて実行するラッパー (初回のみ手動実行) |

### Step 2: Win-ACME で証明書を取得 (初回のみ)

ラッパースクリプトを使う場合 (推奨):

```powershell
cd C:\ProgramData\WatashiAgent\scripts
.\register-agent-acme.ps1 `
    -WacsPath  "C:\tools\win-acme\wacs.exe" `
    -AcmeUrl   "https://ca.internal/acme/directory" `
    -HostName  "agent-a.internal" `
    -PfxPassword (Read-Host -AsSecureString "PFX パスワード")
```

ここで入力する **PFX パスワードは自分で決める値**です (Step 3 で `appsettings.json` に同じ値を書きます)。
長いランダム文字列を推奨します。

スクリプトが内部で実行しているのは次のコマンドです (手動実行する場合の参考):

```powershell
wacs.exe --source manual --host agent-a.internal `
    --baseuri https://ca.internal/acme/directory `
    --store pfxfile `
    --pfxfilepath C:\ProgramData\WatashiAgent\certs\ `
    --pfxpassword "<決めたパスワード>" `
    --installation script `
    --script "C:\ProgramData\WatashiAgent\scripts\after-renew.ps1" `
    --accepttos
```

成功すると:
- `C:\ProgramData\WatashiAgent\certs\agent-a.internal.pfx` が作成される
- タスクスケジューラに `win-acme renew` タスクが登録される (以後の更新は無人)

> 検証方式はデフォルトで HTTP-01 (self-hosting) です。Win-ACME が検証時だけ 80 番ポートに
> 一時リスナーを立てるので、Agent (8081) と競合しません。社内 CA が DNS-01 のみの場合は
> CA 側の手順に従って `--validation` オプションを変更してください。

### Step 3: Agent の appsettings.json を HTTPS に変更

`C:\Program Files\Watashi\Agent\appsettings.json` の `Kestrel` セクションを変更します。

**変更前**:

```jsonc
"Kestrel": {
  "Limits": { "MaxRequestBodySize": 10737418240 },
  "Endpoints": {
    "Http": { "Url": "http://0.0.0.0:8081" }
  }
}
```

**変更後**:

```jsonc
"Kestrel": {
  "Limits": { "MaxRequestBodySize": 10737418240 },
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8081",
      "Certificate": {
        "Path": "C:\\ProgramData\\WatashiAgent\\certs\\agent-a.internal.pfx",
        "Password": "<Step 2 で決めた PFX パスワード>"
      }
    }
  }
}
```

> PFX のファイル名は Win-ACME が `<ホスト名>.pfx` で出力します。実際のファイル名を
> `dir C:\ProgramData\WatashiAgent\certs\` で確認してから記載してください。

変更したらサービスを再起動します:

```powershell
Restart-Service Watashi.Agent
```

ローカルで待ち受け確認:

```powershell
curl.exe -k https://localhost:8081/health
# {"status":"ok","at":"..."} が返れば Kestrel の HTTPS 化は完了
# (-k はホスト名不一致 (localhost) を無視するためで、ローカル確認時のみ使用)
```

### Step 4: 中央サーバのノード Endpoint を https に変更

クライアントツールの **管理画面 → ノード管理** で、該当ノードの Endpoint を変更します:

| 項目 | 変更前 | 変更後 |
|---|---|---|
| Endpoint | `http://agent-a.internal:8081` | `https://agent-a.internal:8081` |

### Step 5: 疎通確認

**中央サーバ**の PowerShell から (`-k` を付けずに実行するのがポイント):

```powershell
curl.exe https://agent-a.internal:8081/health
```

- `{"status":"ok",...}` が返る → 完了。証明書検証込みで TLS が確立できています
- SSL エラーが出る → [トラブルシューティング](#トラブルシューティング) へ

本リポジトリの `deploy\agent-https\test-agent-https.ps1` を中央サーバで実行すると、
証明書の内容 (Subject / 有効期限 / 発行者) と /health 応答をまとめて確認できます:

```powershell
.\test-agent-https.ps1 -AgentUrl https://agent-a.internal:8081
```

最後に、クライアントツールからそのノード経由のホストに対して「接続テスト」を実行し、
ファイル一覧が取得できることを確認してください。

---

## エージェント経由 (1段チェーン) 構成の場合

`Server → Agent A (ゲートウェイ) → Agent B → CIFS` の構成では、HTTPS 化できる区間に制限があります:

```
Server ──── HTTPS 可 ────► Agent A ──── HTTP のみ ────► Agent B
  ▲                                                        │
  └──────────── HTTPS 可 (heartbeat/LogSync) ──────────────┘
                 ※ Agent B が中央に直接到達できる場合
```

| 区間 | HTTPS 化 | 方法 |
|---|---|---|
| Server → Agent A (ゲートウェイ) | **可** | 本手順をそのまま Agent A に適用 |
| Agent A → Agent B (チェーン転送) | **不可 (HTTP 固定)** | プログラム側の制約。Agent B のノード Endpoint は `http://` のまま |
| Agent A/B → Server (heartbeat / ログ送信) | 可 (既存) | `Agent:CentralUrl` が `https://` なら暗号化済み |

> Agent A → Agent B が HTTP 固定なのは実装上の制約です (転送先 Endpoint が `http://` 以外だと
> Agent A が 400 を返します。`src/Watashi.Agent/Endpoints/AgentEndpoints.cs` の
> `ForwardToNextAgentAsync` 参照)。この区間も暗号化が必要な場合はプログラム変更が必要になるため、
> 開発側に相談してください。現状は Agent A/B 間がネットワーク的に隔離されたセグメントである
> ことを前提とした設計です。

つまり「ゲートウェイになっている Agent A」に対しては本手順をそのまま実施して問題ありません。
**Agent B のノード登録 (Endpoint) だけは `http://` のままにしてください。**

---

## 証明書の自動更新の仕組み

初回登録後は以下が無人で回ります:

```
タスクスケジューラ (win-acme renew、毎日起動)
  └─ 期限が近い証明書のみ更新処理
       ├─ ① 社内 CA に ACME で更新要求 (HTTP-01 検証)
       ├─ ② 新しい PFX を C:\ProgramData\WatashiAgent\certs\ に上書き
       └─ ③ after-renew.ps1 実行 → Restart-Service Watashi.Agent
```

- `appsettings.json` は固定パス + 固定パスワードを指しているため、**更新時の設定変更は不要**です
- ③ の再起動で数秒のサービス断が発生します。実行中のファイル転送がそのタイミングに
  当たると失敗するため、**更新タスクの実行時刻は業務時間外に設定**してください
  (タスクスケジューラで `win-acme renew` タスクのトリガー時刻を変更)
- 更新の動作確認: `wacs.exe --renew --force` で強制更新し、PFX のタイムスタンプ更新と
  サービス再起動 (イベントログ / `after-renew.ps1` のログ) を確認

---

## トラブルシューティング

| 症状 | 原因と対処 |
|---|---|
| 中央サーバからの curl で `SSL connection could not be established` | ①証明書のホスト名と接続先ホスト名の不一致 → `test-agent-https.ps1` で Subject/SAN を確認。②ルート CA 未信頼 → 中央サーバの「信頼されたルート証明機関 (LocalMachine)」に社内 CA のルート証明書があるか確認 |
| Win-ACME の検証 (HTTP-01) が失敗する | CA → Agent サーバの TCP 80 が通っているか確認。`Test-NetConnection <agent> -Port 80` を CA 側 (または同セグメント) から実行。Windows ファイアウォールの受信規則も確認 |
| サービス起動に失敗 (イベントログに証明書エラー) | `appsettings.json` の PFX パス/パスワードの誤り。`certutil -dump <pfx> -p <password>` で開けるか確認 |
| 更新後も古い証明書で応答する | `after-renew.ps1` が実行されていない。`C:\ProgramData\WatashiAgent\logs\cert-renew.log` と、タスクスケジューラの `win-acme renew` の前回実行結果を確認 |
| ノードが Unhealthy になる | Endpoint の `https://` 化を Step 3 (Agent 側) より先に行うと、HTTP で待ち受け中の Agent に HTTPS で接続しようとして失敗します。順序は必ず Agent 側 → ノード Endpoint の順で |
| heartbeat は通るがファイル操作が失敗する | heartbeat は Agent→Server (逆方向) なので Agent 側 HTTPS 化と無関係に通ります。Server→Agent 方向の証明書検証エラーを疑い、中央サーバの Serilog (`C:\ProgramData\Watashi\logs\server-*.log`) を確認 |

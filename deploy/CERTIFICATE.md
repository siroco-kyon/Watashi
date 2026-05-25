# HTTPS 証明書の設定方法

Windows Server の IIS と違い、Watashi.Server / Watashi.Agent は **Kestrel** という ASP.NET Core のクロスプラットフォーム Web サーバを使っています。Kestrel は証明書を「ファイル」または「Windows 証明書ストア」のどちらからでも読み込めますが、Windows 上で運用する場合は**ストアから直接読む方式が圧倒的に楽**です。

このドキュメントは「社内 CA から配布された証明書が既に IIS に入っていて、Watashi でも同じものを使いたい」という現場でよくあるケースを対象に、2 つの方式を比較しつつ手順をまとめます。

- [どちらの方式を選ぶか](#どちらの方式を選ぶか)
- [方式 A: 証明書ストアから直読み (推奨)](#方式-a-証明書ストアから直読み-推奨)
- [Win-ACME (Let's Encrypt) で取得した証明書を使う場合](#win-acme-lets-encrypt-で取得した証明書を使う場合)
- [方式 B: PFX ファイルを配置する](#方式-b-pfx-ファイルを配置する)
- [証明書がエクスポート可能か判定する](#証明書がエクスポート可能か判定する)
- [証明書更新時の手順](#証明書更新時の手順)
- [トラブルシューティング](#トラブルシューティング)

---

## どちらの方式を選ぶか

```
社内 CA から配布された証明書 → 既に Windows 証明書ストアに入っている？
       │
       ├─ YES (IIS の証明書選択で出てくる)
       │     │
       │     └─→ 方式 A (ストア参照) を強く推奨
       │
       └─ NO (PFX ファイルを単独でもらった、または自前で発行する)
             │
             ├─ ストアに入れてから方式 A を使うのが楽
             └─ そのまま方式 B でファイル指定もできる
```

| 比較 | 方式 A: ストア参照 | 方式 B: ファイル指定 |
|---|---|---|
| `appsettings.json` への秘密情報 | サムプリント/サブジェクトのみ | パス + **パスワード** |
| 証明書更新時の手間 | 新しいのをストアに入れ替えるだけ (設定変更不要) | 新 PFX を再配置 + appsettings 更新 |
| IIS と Watashi で同じ証明書を共有 | ◎ そのまま使える | △ ファイルコピーが必要 |
| エクスポート不可な証明書も使える | ◎ | × (エクスポート不可だと .pfx が作れない) |
| Windows 以外 (Linux/コンテナ) で動かす | × ストアが無い | ◎ |
| アクセス権設定の手間 | 秘密キーへの ACL 設定が 1 回必要 | ファイルとパスワードを安全に保管する手間 |

**社内 PKI から配布される証明書はエクスポート不可で配られることが多い**ため、現場では方式 A が現実的かつ唯一の選択肢になることが多いです。

**Win-ACME (`wacs.exe`) / Let's Encrypt で取得した証明書も方式 A**。証明書は `LocalMachine\My` ではなく `LocalMachine\WebHosting` ストアに入るため、`Store` の指定が違う + 自動更新時に Watashi.Server を再起動するフックを 1 個仕込むのが必要です → [Win-ACME 専用節](#win-acme-lets-encrypt-で取得した証明書を使う場合)。

---

## 方式 A: 証明書ストアから直読み (推奨)

### Step 1: 証明書のサブジェクト or サムプリントを確認

管理者 PowerShell で:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.HasPrivateKey } |
    Select-Object Subject, Thumbprint, NotAfter |
    Format-List
```

出力例:
```
Subject    : CN=watashi.internal, OU=IT, O=Acme Corp, C=JP
Thumbprint : 1A2B3C4D5E6F7890ABCDEF1234567890ABCDEF12
NotAfter   : 2027/03/15 23:59:59
```

`HasPrivateKey : True` の証明書だけが TLS で使えます。IIS のバインドで選んでいるのと同じ Subject のものを採用します。

> **`certlm.msc` (GUI) でも確認可**: 「個人」→「証明書」を開き、対象をダブルクリック → 「詳細」タブの「サブジェクト」「拇印 (Thumbprint)」を見る。

### Step 2: `appsettings.json` をストア参照モードに変更

Watashi.Server / Watashi.Agent どちらでも同じ書き方です。

**サービス導入後の設定ファイル位置**:
- Server: `C:\Program Files\Watashi\Server\appsettings.json`
- Agent: `C:\Program Files\Watashi\Agent\appsettings.json`

**Before (ファイル指定)**:
```jsonc
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": {
        "Path": "C:\\Apps\\Watashi\\server.pfx",
        "Password": "..."
      }
    }
  }
}
```

**After (ストア参照、Subject で指定)**:
```jsonc
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": {
        "Subject": "CN=watashi.internal",
        "Store": "My",
        "Location": "LocalMachine",
        "AllowInvalid": false
      }
    }
  }
}
```

**サムプリントで指定する場合**: より厳密で誤マッチを防げる。証明書更新時は新サムプリントで更新が必要。

```jsonc
"Certificate": {
  "Subject": "*",
  "Store": "My",
  "Location": "LocalMachine",
  "AllowInvalid": false
}
```
※ サムプリントを直接指定するキーは Kestrel 構成にないため、複数候補がある場合は Subject 部分一致で絞ります。1 ホスト 1 証明書なら Subject だけで充分です。

### 各キーの意味

| キー | 値 | 意味 |
|---|---|---|
| `Subject` | `CN=watashi.internal` | 証明書の Subject 部分一致 (CN= の値) |
| `Store` | `My` | "個人" ストア。手動インポートや AD CS 配布のデフォルト |
| `Location` | `LocalMachine` | ユーザーストアではなくマシン全体のストア |
| `AllowInvalid` | `false` | 期限切れ・未信頼を許可しない (本番は必ず false) |

`Store` には他に以下のような選択肢があります。Win-ACME 等で取得した証明書は `My` ではなく `WebHosting` に入る点に注意:

| ストア名 | 用途 | 典型的なインポート元 |
|---|---|---|
| `My` (= "個人") | サーバ証明書 / クライアント証明書 | 手動 PFX インポート、AD CS グループポリシー |
| `WebHosting` (= "Web ホスティング") | IIS 用に最適化された証明書ストア (IIS 8+) | **Win-ACME (wacs.exe) 既定**、IIS Manager の証明書要求機能 |
| `Root` | 信頼されたルート CA | OS 標準 + 社内 CA ルート |
| `TrustedPeople` | 信頼された発行者 | コード署名検証など |

「IIS のバインドで証明書が選べるのに、`Cert:\LocalMachine\My` を見ると見つからない」場合は、ほぼ間違いなく `WebHosting` ストアに入っています。次節を参照。

### Step 3: サービス起動アカウントに秘密キーへのアクセス権を付与

Watashi のサービスはデフォルト **LocalSystem** で動きます ([install-server-service.ps1](install-server-service.ps1) より)。LocalSystem は `LocalMachine\My` 内の秘密キーを大抵そのまま読めますが、社内 PKI 配布の証明書では明示的な権限付与が必要な場合があります。

**GUI 手順 (推奨、間違えにくい)**:
1. `certlm.msc` (ローカルコンピューターの証明書) を開く
2. 「個人」→「証明書」→ 対象を**右クリック** →「**すべてのタスク**」→「**秘密キーの管理**」
3. 「**追加**」→ 「**詳細設定**」→「**今すぐ検索**」
4. 一覧から付与対象を選択:
   - LocalSystem 起動なら `SYSTEM`
   - 専用サービスアカウント運用なら `NT SERVICE\Watashi.Server` や `DOMAIN\svc-watashi`
5. 「**読み取り**」のみチェック → OK

**PowerShell 手順 (バッチ向け)**:
```powershell
# 例: SYSTEM に対して指定サムプリントの証明書の秘密鍵へ Read 権限を付与
$thumb = "1A2B3C4D5E6F7890ABCDEF1234567890ABCDEF12"
$cert = Get-ChildItem Cert:\LocalMachine\My\$thumb
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyName = $rsa.Key.UniqueName
$keyPath = "$env:ProgramData\Microsoft\Crypto\Keys\$keyName"
$acl = Get-Acl $keyPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "Read", "Allow")
$acl.AddAccessRule($rule)
Set-Acl $keyPath $acl
```

> 古い CryptoAPI (CAPI) 形式のキーは `$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\` 配下にあります。CNG (Cryptography Next Generation) は `Crypto\Keys\` 配下。Windows Server 2016 以降は CNG が標準。

### Step 4: 再起動して動作確認

```powershell
Restart-Service Watashi.Server
# 接続テスト
curl.exe -k https://watashi.internal:8443/health
# → {"status":"ok","at":"..."}
```

`-k` は自己署名証明書テスト用。社内 CA 発行で正しく信頼チェーンが通っていれば `-k` 無しで成功するはずです。

エラーが出る場合は [トラブルシューティング](#トラブルシューティング) を参照。

---

## Win-ACME (Let's Encrypt) で取得した証明書を使う場合

Win-ACME (`wacs.exe`) は Windows 向けの ACME クライアントで、Let's Encrypt や内部 ACME サーバ (Step CA など) から証明書を取得・自動更新するツール。**証明書は既定で `LocalMachine\WebHosting` ストアに入る**ため、方式 A の手順をそのまま使えます。ただし以下 2 つの追加対応が必要です。

### ① `Store` を `WebHosting` にする

```jsonc
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": {
        "Subject": "CN=watashi.internal",
        "Store": "WebHosting",          // ← My ではなく WebHosting
        "Location": "LocalMachine",
        "AllowInvalid": false
      }
    }
  }
}
```

サムプリント / サブジェクトの確認:
```powershell
Get-ChildItem Cert:\LocalMachine\WebHosting |
    Where-Object { $_.HasPrivateKey } |
    Select-Object Subject, Thumbprint, NotAfter | Format-List
```

秘密キーへの ACL 付与 (`certlm.msc` → 「Web ホスティング」→ 証明書を右クリック → 「すべてのタスク」→「秘密キーの管理」) は方式 A と同じ。サービス起動アカウント (`SYSTEM` または専用) に `読み取り` を追加。

### ② 更新時に Watashi.Server を再起動するフックを仕込む (★最重要)

Win-ACME は 60 日サイクルで証明書を自動更新します。更新が走ると:

- `WebHosting` ストアに**新しいサムプリントの証明書が追加**される
- IIS のバインドは自動で新サムプリントに張り替えられる
- **しかし Watashi.Server (Kestrel) はプロセス起動中、古い証明書を保持し続ける**

→ 更新後にサービスを再起動しないと、有効期限切れ証明書のまま運用されてしまう。以下のいずれかで対処します。

#### 方法 1: Win-ACME のインストールスクリプトフック (推奨)

再起動用スクリプトを 1 個用意:

```powershell
# C:\Apps\Watashi\post-renewal.ps1
$ErrorActionPreference = "Stop"
Write-Host "[wacs hook] Restarting Watashi.Server..."
Restart-Service Watashi.Server -Force
# Agent が同居していれば
# Restart-Service Watashi.Agent -Force -ErrorAction SilentlyContinue
Write-Host "[wacs hook] Done."
```

**新規 renewal の場合** — 取得時から script フックを組み込む:
```powershell
& "C:\Program Files\win-acme\wacs.exe" `
    --target iis `
    --host watashi.internal `
    --installation iis,script `
    --script "powershell.exe" `
    --scriptparameters "-NoProfile -ExecutionPolicy Bypass -File C:\Apps\Watashi\post-renewal.ps1"
```

**既存の renewal に後付けする場合** — 対話モードで編集:
```powershell
& "C:\Program Files\win-acme\wacs.exe"
#  M (Manage renewals) → 対象を選択 → I (Update installation steps)
#  → "iis" に加えて "script" も選択 → スクリプトパスを入力
```

もしくは直接 `%ProgramData%\win-acme\Acme-v02.api.letsencrypt.org\Renewals\<id>.renewal.json` の `InstallationPluginOptions` 配列を編集。

更新は Win-ACME が登録しているタスクスケジューラ「win-acme renew (acme-v02.api.letsencrypt.org)」が自動実行するので、以降はノータッチで OK。

#### 方法 2: 独立スケジュールタスクでサムプリント監視 (Win-ACME に触りたくない場合)

```powershell
# C:\Apps\Watashi\check-cert-renewal.ps1
$ErrorActionPreference = "Stop"
$stateFile = "C:\Apps\Watashi\last-cert-thumbprint.txt"
$cert = Get-ChildItem Cert:\LocalMachine\WebHosting |
    Where-Object { $_.Subject -like "*watashi.internal*" -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { Write-Error "Certificate not found in WebHosting store"; exit 1 }

$lastThumb = if (Test-Path $stateFile) { Get-Content $stateFile } else { "" }
if ($cert.Thumbprint -ne $lastThumb) {
    Write-Host "Certificate rotated: $lastThumb → $($cert.Thumbprint)"
    Restart-Service Watashi.Server -Force
    Set-Content $stateFile -Value $cert.Thumbprint
} else {
    Write-Host "No certificate change (current: $($cert.Thumbprint))"
}
```

毎日 1 回タスクスケジューラから実行 (Win-ACME 既定実行時刻 09:00 より後にする):
```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi Cert Renewal Watcher" `
    /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Apps\Watashi\check-cert-renewal.ps1" `
    /ST 04:00 /RL HIGHEST
```

### ③ Win-ACME の証明書取得対象に Watashi.Server のホスト名が含まれているか確認

Win-ACME を IIS と組み合わせて使う場合、対象ホスト名 (`--host` または対話メニューで指定) は **IIS バインドに登録されているもの**から選ぶのが普通。Watashi.Server を `watashi.internal:8443` で運用する場合は:

- IIS のバインドにも `watashi.internal` (任意のポート) を 1 つ登録しておく → Win-ACME が DNS 検証 / HTTP-01 検証を IIS 経由で完結できる
- もしくは Win-ACME の DNS-01 検証 (DNS プロバイダのプラグイン経由) を使えば IIS バインド不要

詳細は Win-ACME 公式 (https://www.win-acme.com/) を参照。

---

## 方式 B: PFX ファイルを配置する

エクスポート可能な証明書を持っていて、ファイルベースの運用にしたい場合 (Linux/コンテナ移植性、外部 CDN/HSM 等)。

### Step 1: 証明書ストアから PFX をエクスポート

GUI 手順:
1. `certlm.msc` を開く
2. 「個人」→「証明書」→ 対象を右クリック →「**すべてのタスク**」→「**エクスポート**」
3. 「**はい、秘密キーをエクスポートします**」を選択
   - **グレーアウトで選べない場合は方式 B は使えません** → 方式 A に切り替え
4. 形式: 「**Personal Information Exchange - PKCS #12 (.PFX)**」 を選択
   - 「証明書のパスにある証明書を可能であればすべて含める」にチェック (チェーン込みで配布)
5. パスワード (PFX を開く用) を設定 — **強い値**にすること
6. 出力先を指定して完了

PowerShell 手順:
```powershell
$pwd = ConvertTo-SecureString -String "<strong-password>" -Force -AsPlainText
$thumb = "1A2B3C4D5E6F7890ABCDEF1234567890ABCDEF12"
Export-PfxCertificate -Cert "Cert:\LocalMachine\My\$thumb" `
    -FilePath "C:\Apps\Watashi\server.pfx" `
    -Password $pwd `
    -ChainOption BuildChain
```

### Step 2: PFX をサーバの安全な場所に配置

例: `C:\Apps\Watashi\server.pfx`

- フォルダ ACL は **サービスアカウント + Administrators のみ Read** に絞る
- ネットワーク共有や OneDrive 同期フォルダには絶対置かない
- バックアップ時もパスワード保護されたコピーで管理

```powershell
# 例: SYSTEM と Administrators 以外を弾く
icacls "C:\Apps\Watashi\server.pfx" /inheritance:r /grant "SYSTEM:R" /grant "Administrators:R"
```

### Step 3: `appsettings.json` でファイル指定

```jsonc
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": {
        "Path": "C:\\Apps\\Watashi\\server.pfx",
        "Password": "<step 1 で設定したパスワード>"
      }
    }
  }
}
```

> **`appsettings.json` 自体にパスワードを書きたくない**場合は環境変数で上書き可能:
> ```powershell
> setx Kestrel__Endpoints__Https__Certificate__Password "<password>" /M
> ```
> こちらの方が安全 (ファイルに残らない、特定アカウントの環境変数として保持)。サービス再起動で反映。

### Step 4: 再起動して動作確認

```powershell
Restart-Service Watashi.Server
curl.exe https://watashi.internal:8443/health
```

---

## 証明書がエクスポート可能か判定する

社内 PKI から配布された証明書は通常 **エクスポート不可で配布される** (秘密鍵流出を防ぐため)。判定:

1. `certlm.msc` を開く
2. 「個人」→「証明書」→ 対象を右クリック →「すべてのタスク」→「エクスポート」
3. ウィザード 2 ページ目の選択肢:
   - 「**はい、秘密キーをエクスポートします**」が**選択可能** → エクスポート可。方式 B 利用可
   - 「はい、秘密キーをエクスポートします」が**グレーアウト** → エクスポート不可。**方式 A 一択**

PowerShell でも判定可能:
```powershell
$cert = Get-ChildItem Cert:\LocalMachine\My\<thumbprint>
$cert.PrivateKey.CspKeyContainerInfo.Exportable  # True なら可、False なら不可
# CNG 形式の場合:
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$rsa.Key.ExportPolicy  # AllowExport / AllowPlaintextExport 等を含むか確認
```

エクスポート不可で配布された証明書を後からエクスポート可能にする手段はありません。**社内 PKI 担当に「秘密鍵エクスポート可能で再発行してください」と依頼する**しかありませんが、運用ポリシー上それを断られるのが普通なので、その場合は方式 A で運用するのが正しい解です。

---

## 証明書更新時の手順

### 方式 A の場合

1. 新証明書を `certlm.msc` →「個人」にインポート (旧証明書はまだ残しておく)
2. 同じ Subject になっていることを確認 (`CN=watashi.internal` 等)
3. 秘密キーに対するサービスアカウントの読み取り権限を付与 (Step 3 と同じ手順)
4. `Restart-Service Watashi.Server` で再起動
   - Kestrel は次回起動時に Subject 一致 + 有効期限がより新しい証明書を自動選択
5. `curl https://watashi.internal:8443/health` で動作確認
6. 旧証明書を削除 (任意)

**`appsettings.json` の変更は一切不要**。これが方式 A の最大のメリット。

### 方式 B の場合

1. 新証明書を PFX でエクスポート (パスワード新規設定)
2. 既存 PFX をバックアップしてから上書き、または新ファイル名で配置
3. `appsettings.json` の `Path` (必要なら) / `Password` を更新
4. `Restart-Service Watashi.Server` で再起動
5. 動作確認

---

## トラブルシューティング

### 起動時 `Unable to configure HTTPS endpoint. No server certificate was specified...`

- 方式 A: `Subject` の値が証明書の実際の Subject と一致していない
  - `Get-ChildItem Cert:\LocalMachine\My | Select Subject` で完全一致を確認
  - 文字列の中に `Subject` の一部 (`CN=watashi.internal`) が含まれていれば前方一致でマッチする
- 方式 A: **`Store` の指定が違う**
  - IIS バインドでは選べるのに `LocalMachine\My` に見当たらない場合、Win-ACME や IIS の証明書要求機能で取得した証明書は `LocalMachine\WebHosting` に入っている。`Store` を `WebHosting` に変更
  - 全ストアを横断検索:
    ```powershell
    @("My","WebHosting","Root","TrustedPeople") | ForEach-Object {
        Write-Host "=== $_ ==="
        Get-ChildItem "Cert:\LocalMachine\$_" -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey } | Select Subject, Thumbprint
    }
    ```
- 方式 A: `Location` の組み合わせ間違い
  - `LocalMachine` が標準。`CurrentUser` ストアにある場合はサービスアカウントから見えない
- 方式 B: `Path` のファイルが存在しない / バックスラッシュのエスケープ忘れ (`\\` で書く)

### 起動時 `Access denied` / `The system cannot find the file specified`

- 秘密キーへのアクセス権限不足 (方式 A)
- → Step 3 の権限付与手順を実行 (`SYSTEM` または専用サービスアカウントに Read 権限)

```powershell
# 起動アカウントを確認
sc.exe qc Watashi.Server | findstr SERVICE_START_NAME
# → SERVICE_START_NAME : LocalSystem  ← この値に対して秘密鍵 ACL を付与する
```

### ブラウザで「証明書エラー」が出る

- クライアント PC の「信頼されたルート証明機関」に社内 CA 証明書が入っていない
- → GPO で社内 CA ルート証明書を配布、または `certmgr.msc` (ユーザー側) で手動インポート
- 自己署名証明書を使っている場合: 各クライアントに手動で信頼登録が必要 (本番では推奨しない)

### サムプリントは合ってるのに証明書が選ばれない

- 秘密鍵が紐付いていない証明書 (`HasPrivateKey: False`) は除外される
- 期限切れ証明書は `AllowInvalid: false` だと除外される
- 複数候補がある場合、Kestrel は **有効期限が一番遠い** ものを選ぶ。旧証明書を残したい場合は注意

```powershell
# 同じ Subject の証明書を全部リスト
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like "*watashi.internal*" } |
    Select Subject, Thumbprint, NotAfter, HasPrivateKey | Format-Table -AutoSize
```

### 起動はするが HTTPS で接続するとプロトコルエラー

- 古い TLS バージョンが要求されている (TLS 1.0/1.1 は無効) / 暗号スイートが合わない
- 一旦 `curl -v https://watashi.internal:8443/health` でハンドシェイクのログを確認
- Windows のスケジュールチャネル (SCHANNEL) ログも参照

---

## 参考リンク

- [Kestrel HTTPS のサポート](https://learn.microsoft.com/ja-jp/aspnet/core/fundamentals/servers/kestrel/endpoints) — `Certificate` セクションの公式仕様
- [docs/SETUP.md](../docs/SETUP.md) — 本デプロイ全体手順 (この証明書設定はその一部)
- [IIS-MIME.md](IIS-MIME.md) — ClickOnce 配布サーバ (IIS) 側の MIME 設定

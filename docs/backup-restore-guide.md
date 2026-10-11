# Kiten データのバックアップと復元

この手順は、Kiten本体が起動しない場合でも使える`Kiten.DataTool.exe`とPowerShellスクリプトを対象とする。初回方式は、Kitenを通常終了してから手動で実行する。ツールは対象チャンネルの単一起動mutexを処理中ずっと保持し、Kitenが起動中、別の保守処理が実行中、または未完了の復元がある場合はデータを書き換えず停止する。Kitenを強制終了しない。

実行用ZIPでは、`Maintenance`フォルダーに`Kiten.DataTool.exe`、`Backup-KitenData.ps1`、`Restore-KitenData.ps1`をまとめて含める。利用者の.NET SDKや`sqlite3`コマンドは不要である。追加のダブルクリック用補助ファイルを使うバックアップではPowerShell 7も不要である。PowerShellスクリプトでバックアップ・復元する場合はPowerShell 7以上が必要であり、以下のコマンドは`Maintenance`フォルダーで実行する。導入・更新・削除の全体手順は[導入・更新・削除ガイド](distribution-guide.md)を参照する。

Kitenの保存データがまだない初回のお試しでは、バックアップは必須ではない。大切なデータを保存した後・既存データを使って更新する前に作成する。毎回の起動前に作る必要はない。

## 1. 先に知っておくこと

- `stable`は常用版の`%LOCALAPPDATA%\TabBrowser`、`development`は開発版の`%LOCALAPPDATA%\TabBrowser-Dev`を対象にする。
- スクリプトは実行ファイルの展開場所からデータ保存先を推測しない。`-Channel`から既定rootを決め、`-DataRoot`を指定した場合もチャンネルに対応する末尾名と絶対パスを検証する。
- バックアップZIPは暗号化されない。メモ本文、URL、検索式、ローカルファイル参照などの個人情報を含み得るため、公開issueや共有フォルダーへ置かない。
- ZIP隣の同名`.sha256`ファイルも復元に必須である。SHA-256は破損の検出には使えるが、入手元が信頼できることまでは証明しない。
- バックアップ作成後の変更は、そのバックアップに含まれない。古い時点へ復元すると、その後の変更は戻らない。
- 復元はKitenの管理データだけを対象にする。Cookie、ログイン状態、Webサイト側データ、外部ファイル、ダウンロード済み実ファイルは戻らない。

## 2. 対象と除外

既定で次を一つのバックアップへ保存する。

- 設定とその正常backup
- ブックマーク、フォルダー、保存タブ情報とその正常backup
- Workspace、メモ、保存タブ／グループを含む`workspace.db`
- 通常セッション、Window記憶、分割表示
- 自動・保護・名前付きのタブセッション復元点
- コンテナのID、名前、色の定義
- YouTubeミニプレイヤーの保存状態

`session.named`は、利用者がWindow設定メニューから明示的に作成した名前付きタブセッション復元点を保存する。これらは自動ローテーションで削除されず、基本バックアップと復元の対象に含む。ただし、名前付き復元点はタブセッション用であり、Workspace DB、メモ、Cookie、認証情報の世代復元点ではない。

次の履歴は既定で含めず、必要なものだけ個別に指定できる。

- `-IncludeBrowsingHistory`: 閲覧履歴DB
- `-IncludeRecentItems`: 最近使った項目
- `-IncludeDownloadHistory`: アプリ内ダウンロード履歴。ダウンロード済みファイル本体は含まない

常に除外するもの:

- 通常／コンテナ／QuickLookのWebView2プロファイル、Cookie、認証情報、Web Storage
- `private`以下の一時データ
- 診断ログ、クラッシュ資料、作業用一時ファイル
- ダウンロード済み実ファイル、WorkspaceやFolderTabが参照する外部ファイル
- Webサイト側のメールやクラウドデータ
- Workspace DBの移行前退避。既存の退避は削除しないが、現在状態の基本バックアップへは混ぜない

コンテナ定義を復元しても、WebView2プロファイルを復元しないため、サイトへ再ログインが必要になる場合がある。

## 3. バックアップ

### 3.1 ダブルクリックで作成する

公開済み`v0.1.0-preview.1`のZIPには、ダブルクリック用補助ファイルを含めていない。[Backup-KitenData.cmd](../tools/Backup-KitenData.cmd)を保存し、展開済みKitenの`Maintenance`へ置く。GitHubでは「Download raw file」で保存し、ファイル名が`.cmd.txt`になっていないことを確認する。以降は次の手順を使う。

1. KitenのすべてのWindowとミニプレイヤーなどを通常終了する。
2. `Maintenance\Backup-KitenData.cmd`をダブルクリックする。
3. 表示された対象と保存先を確認し、実行するときだけ大文字で`BACKUP`と入力する。それ以外はキャンセルになる。
4. 「バックアップと作成後の検証が完了しました」を確認し、表示されたZIPと同名`.zip.sha256`をセットで保管する。何かキーを押して画面を閉じる。

この入口は配布版（`stable`）の基本バックアップを対象にし、保存先はユーザーフォルダーの`KitenBackup`である。履歴3種類は含めず、開発版データは対象にしない。計画の表示と作成の両方で同梱DataToolを使い、起動中・別の保守処理・未完了の復元の拒否、パスとデータ形式の確認、作成後のmanifest・SHA-256・SQLite検証を維持する。エラー時は完了表示を出さない。

### 3.2 PowerShellで対象・保存先を指定する

1. 対象チャンネルのKitenを通常終了する。
2. `Kiten.DataTool.exe`、`Backup-KitenData.ps1`、`Restore-KitenData.ps1`がある保守ツールフォルダーでPowerShellを開く。
3. データroot外の保存先を指定して実行する。

常用版の基本バックアップ:

```powershell
.\Backup-KitenData.ps1 -Channel stable -Destination 'D:\KitenBackup'
```

開発版の基本バックアップ:

```powershell
.\Backup-KitenData.ps1 -Channel development -Destination 'D:\KitenBackup-Development'
```

必要な履歴だけを含める例:

```powershell
.\Backup-KitenData.ps1 -Channel stable -Destination 'D:\KitenBackup' `
  -IncludeBrowsingHistory -IncludeRecentItems
```

最初に、対象チャンネル、データroot、保存先、対象件数、履歴の選択、暗号化されないことが表示される。内容を確認し、実行するときだけ大文字で`BACKUP`と入力する。キャンセル、検証失敗、書込み失敗では完成ZIPを確定せず、以前の正常なバックアップを削除しない。

成功時は次を作成する。

- `Kiten-DataBackup-<channel>-<UTC>-<一意値>.zip`
- 同名ZIPに`.sha256`を付けた検証ファイル

ZIP内の`backup-info.json`には、形式版、作成UTC、元アプリの公開版と完全SHA、チャンネル、暗号化の有無、選択した履歴、各相対パスの存在／不存在、種類、サイズ、SHA-256、取得できたデータ形式版を記録する。本文やURLはmanifestへ複製しない。manifest自身は循環するため自身のhash一覧へ入れない。

## 4. 復元計画を確認する

Kitenを通常終了し、ZIPと隣の`.sha256`を同じ場所に置く。`-Apply`を付けずに実行すると、完全性と互換性を検証し、元アプリ版、復元時点、基本対象、3種類の履歴を含むか、変更対象、確認済みZIP SHA-256を表示する。現在データは変更しない。

```powershell
.\Restore-KitenData.ps1 -Channel stable `
  -Archive 'D:\KitenBackup\Kiten-DataBackup-stable-....zip'
```

次は拒否される。

- ZIPまたは`.sha256`の破損・不一致
- 未対応のmanifestまたはデータ形式
- チャンネル不一致
- 絶対パス、`..`、重複項目、不正なWindows名、リンク、manifest外ファイル
- ファイル数、単一ファイル、展開後合計サイズの上限超過
- 対象rootや途中経路の再解析ポイント

バックアップに含まれるスクリプトや実行ファイルを実行することはない。

## 5. 復元を実行する

計画表示に問題がなければ、同じコマンドへ`-Apply`を付ける。

```powershell
.\Restore-KitenData.ps1 -Channel stable `
  -Archive 'D:\KitenBackup\Kiten-DataBackup-stable-....zip' -Apply
```

再検証後、戻す時点と「その後の変更は戻らない」ことが表示される。実行するときだけ大文字で`RESTORE`と入力する。スクリプトは計画時のZIP SHA-256を適用処理へ渡して再照合するため、確認待ちの間にZIPが別の正常な内容へ置き換わった場合も復元せず停止する。

復元ツールは、復元元ZIPのhash、形式、JSON、SQLite schemaを常に厳格に検証する。現在データが正常な場合は、書込み前に現在データを同じ検証付き方式でバックアップし、元データをroot外の復旧作業領域へ移してから、検証済みステージングのファイルへ切り替える。SQLite本体と`-wal`、`-shm`、`-journal`は一つの世代として退避し、別世代同士を混ぜない。不在と記録された管理対象は復元後も不在にし、対象外ファイルは変更しない。

現在の管理対象JSONまたはSQLiteが破損して検証付きバックアップを作れない場合は、復元元の検証を緩めず、現在データだけを「破損原本」として扱う。内容破損を見つけても残りの固定ファイルと3種類のセッション復元点を確認し、後続に未対応形式や環境障害があれば適用を中止する。置換対象のファイル、存在するSQLiteの`-wal`、`-shm`、`-journal`、セッション復元点を解釈・checkpointせずバイト単位で`raw-current\data`へコピーし、存在／不存在、相対パス、サイズ、SHA-256を`raw-current\original-snapshot.json`へ記録する。全コピーと照合が完了するまで現在データを変更しない。容量不足、I/O障害、ロック、アクセス拒否、キャンセル、コピー失敗、hash不一致、未対応の将来形式では適用を開始しない。WebView2、Cookie、認証情報、`private`、診断ログなどの対象外データは破損原本にも含めない。

成功後も、次の復旧作業領域を自動削除しない。

```text
%LOCALAPPDATA%\.kiten-restore-stable-<一意値>\
%LOCALAPPDATA%\.kiten-restore-development-<一意値>\
```

ここには`restore-journal.json`、通常時の検証済み復元前バックアップ、破損時の`raw-current\original-snapshot.json`と`raw-current\data`、必要に応じて失敗した復元物が残る。Kitenを起動して設定、ブックマーク、Workspace、メモ、セッションを確認するまで削除しない。破損原本は正常なバックアップではなく、正常復旧後も自動削除しない。

## 6. 復元が中断した場合

復元中はデータrootに`restore.in-progress`を置く。処理中の強制終了などでmarkerが残った場合、Kiten本体はデータを開かず起動を中止する。新しいバックアップや復元も開始しない。

Kitenを終了したまま、次を実行する。

```powershell
.\Restore-KitenData.ps1 -Channel stable -RollbackInterrupted
```

表示を確認し、開始前の状態へ戻すときだけ大文字で`ROLLBACK`と入力する。journal、raw原本または`original`にある実際の原本、復元前記録との対応を排他中に再検証し、現在データを動かす前に検証済み作業コピーを作る。原本の欠落・改変・journalとの不一致があれば、現在データとmarkerを保持したまま停止する。巻き戻しは確認済み作業コピーから完了済みでない項目だけを戻すため、途中中断後も同じ復旧を再実行できる。巻き戻し後も復旧記録は確認用に残る。

開始前状態が破損原本だった場合、ROLLBACKの成功は「原本と同じバイト列へ戻した」ことだけを表し、利用可能な正常データへ直ったことを意味しない。画面にも正常利用可能とは未確認である旨を表示する。この場合はKitenを起動せず、破損原本と復旧記録を残したまま、正常なバックアップから復元を再実行する。

自動巻き戻しにも失敗した場合は、marker、journal、復元前バックアップ、移動済み元データ、失敗した復元物を保持する。Kitenを起動せず、これらを手動で移動・削除しない。復旧ツールを再実行しても失敗する場合は、復旧作業領域を保全してサポートへ相談する。

## 7. 保証範囲

本方式は、正しくmutexを使う対象チャンネルのKitenが停止している状態で、確定済みJSONとSQLiteを整合したセットとして退避・復元する。現在のSQLite本体と関連ファイルは先に検証領域へバイトコピーし、原本ではなくそのコピーに対して`Microsoft.Data.Sqlite`のbackup APIを使う。これによりWAL内の確定済み変更を単一DBへ統合し、`quick_check`と`foreign_key_check`を行いながら、安全判定のために現在SQLiteを開いてcheckpointしない。破損原本はSQLiteとして開かず、関連ファイルとともに原本保全する。現行Workspace DBは`PRAGMA user_version=2`である。保守ツールはversion 0／1／2それぞれの必須テーブルと列を確認し、正常な旧versionのバックアップも受け入れる。version 0／1のDBを対応版Kitenが開くと、検証済み移行前バックアップを作成してから1トランザクションでversion 2へ移行する。version 2を超える未知形式や、版表記に必須列が伴わないDBは、破損救済に見せかけて旧版へ戻さない。

次は保証しない。

- 同じmutexを無視してデータを書き換える外部プログラムとの同時操作
- 物理媒体故障、メモリ破壊、すべての瞬間の電源断・OSクラッシュからの絶対復旧
- 悪意あるが形式上正しいバックアップの入手元の信頼性
- バックアップ対象外のCookie、認証情報、WebView2状態、外部ファイル、Webサイト側データの復元
- 未対応の新しいデータ形式を旧版Kitenへ戻すこと

新しいKitenがデータ形式を更新した後に、旧EXEだけへ戻さない。旧版へ戻す必要がある場合は、更新後データを別途保全し、更新前に作成した旧形式バックアップとの互換性を確認してから復元する。

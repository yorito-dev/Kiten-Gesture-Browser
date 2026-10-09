# Kiten Gesture Browser 公開アルファ版 導入・更新・削除ガイド

**本版は開発中のアルファ版です。** 不具合、仕様変更、保存データが失われる可能性があります。重要データの唯一の保存先にせず、利用前・更新前にバックアップしてください。

このガイドは、Windows 11 x64向けPreview版の実行用ZIPを手動で導入・更新する手順を説明する。公式GitHub配布リポジトリのReleasesから、無料の実行用ZIPとSHA-256を入手する。GitHub Sponsorsでの支援は任意で、本体の機能差はない。

作者表示名は「ヨリト」。本体は、試したい人が事前招待や人数制限なく、無償で、用途と期間を限定せず利用できる公開アルファ版である。本体ソースを公開し、[MIT License](../LICENSE.txt)で商用利用・改変・再配布を認める。著作権表示と許諾文を保持する。第三者ソフトウェアには各第三者ライセンスが適用される。

初回公開アルファは未署名で提供するため、Windowsの警告が出る可能性がある。入手元と公開されたSHA-256を確認し、DefenderやSmartScreenを無効にしない。SHA-256の一致だけでは配布元の信頼性を保証しない。

## 1. 対象となる配布物

初回Previewで案内する実行用ZIPは次の1種類である。

```text
Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip
```

GitHub等が自動生成する`Source code` ZIPは、Kitenの実行用ZIPではない。ダウンロード後は、配布ページに記載されたSHA-256と、ZIPと隣にある同名の`Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip.sha256`の値が一致することを確認する。配布ページの版番号・ZIPのSHA-256と、配布manifest／実行ファイルのソースSHAを対応させる。本体ソースを公開するKiten-Gesture-BrowserのReleaseタグ、公開ソースcommit、配布manifestと実行ファイルの埋込みSHAを対応させる。非公開開発repoからソースを切り出す場合は元の候補との対応も配布記録へ残す。

## 2. 動作条件

- 検証対象: Windows 11 x64
- 必要な外部Runtime: Microsoft Edge WebView2 Evergreen Runtime
- .NET: 実行用ZIPへ同梱する自己完結型。利用者による.NET SDKの導入は不要
- バックアップ／復元: PowerShell 7以上（`pwsh`）。未導入の場合はMicrosoft公式の[WindowsへのPowerShell導入手順](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)に従う
- インストーラー、自動更新、Microsoft Store版: 初回Previewの対象外

ローカル動画はWPF標準MediaElement方式で、対応形式はWindowsのコーデック環境に依存する。YouTubeミニプレイヤーと既存ミニプレイヤー内のローカル動画再生も利用できる。LibVLCは同梱しない。

WebView2 Runtimeがない場合は、Microsoft公式の[WebView2 Runtime配布ページ](https://developer.microsoft.com/microsoft-edge/webview2/)からEvergreen Runtimeを導入する。Windowsのセキュリティ機能を無効化する手順は使わない。

## 3. 初回導入

1. 配布ページから実行用ZIPを一つだけダウンロードする。
2. 公開されているSHA-256と、ダウンロードしたZIPのSHA-256を照合する。
3. 新しい空フォルダーを作る。
4. ZIPをそのフォルダーへすべて展開する。ZIP内から直接実行しない。
5. 展開先の`TabBrowser.exe`を引数なしで起動する。
6. `Ctrl+Shift+K`でコマンドパレットを開き、`バージョン情報・Runtime診断を開く`を実行する。製品版番号、公開区分、実行チャンネル、ソースSHAを配布ページの記録と照合する。

初回の公開PreviewはStableチャンネルとして起動し、ユーザーデータを`%LOCALAPPDATA%\TabBrowser`へ保存する。実行ファイルを別フォルダーへ移しても、同じチャンネルのデータは自動的に分離されない。

## 4. 手動更新

1. 開いているすべてのKiten Window、動画ミニプレイヤー、チャットWindowを通常の操作で終了する。強制終了しない。
2. [バックアップと復元](backup-restore-guide.md)の手順で更新前バックアップを作成し、検証成功を確認する。失敗した場合は更新を続けない。
3. 新版の実行用ZIPとSHA-256を照合する。
4. 旧版フォルダーを上書きせず、新しい別の空フォルダーへ新版を展開する。
5. 旧版プロセスがすべて終了していることを確認し、新版の`TabBrowser.exe`を引数なしで起動する。
6. Runtime診断の版情報、起動、設定、ブックマーク、Workspace、メモ、タブ復元を確認する。
7. 問題がなければ、旧アプリフォルダーは後日削除できる。更新前バックアップは、復旧不要と判断するまで自動削除しない。

旧版と新版は同じStableデータを使うため、同時に起動しない。アプリの単一起動制御は同じチャンネルの通常起動を抑止するが、更新手順では利用者自身も同時起動を避ける。

## 5. 問題が起きた場合と旧版への復帰

新版を閉じ、更新後の現在データを別途保全する。データ形式が更新された可能性があるため、旧版のEXEだけへ戻して新版データを直接開かない。

旧版へ戻す場合は、更新前に作成したバックアップが旧版で扱える形式かを確認し、[復元計画](backup-restore-guide.md#4-復元計画を確認する)を先に表示する。復元すると、バックアップ作成後の設定、タブ、メモ、Workspace等の変更は戻らない。未知の新しい形式や互換性情報がないバックアップは、復元ツールが拒否する。

復元途中で失敗した場合は、`restore.in-progress`、復元journal、復元前バックアップ、作業領域を削除・移動せず、[復元が中断した場合](backup-restore-guide.md#6-復元が中断した場合)の手順に従う。

## 6. アプリ本体を削除する

1. Kitenを通常終了する。
2. 必要ならバックアップを作成する。
3. Kitenを展開したアプリフォルダーだけを削除する。

この操作では`%LOCALAPPDATA%\TabBrowser`のユーザーデータを削除しない。そのため、あとで同じチャンネルのKitenを起動すると既存データを再利用できる。

## 7. ユーザーデータも削除する場合

ユーザーデータ削除はアプリ削除とは別の、取り消しにくい操作である。このガイドは自動削除コマンドを提示しない。必要なバックアップを検証し、すべてのKitenプロセスを終了したうえで、削除対象が`%LOCALAPPDATA%\TabBrowser`であることをExplorerで確認してから、利用者が明示的に判断する。

バックアップまたは復元を使った場合は、データrootとは別の場所にも個人情報を含むデータが残る。削除完了と判断する前に、次も個別に確認する。

- 自分で指定したバックアップ先にある`Kiten-DataBackup-*.zip`と同名の`.sha256`。不要と判断した世代だけを削除する
- `%LOCALAPPDATA%\.kiten-restore-stable-*`にある復元前バックアップと復旧記録。復元後の動作確認が済み、中断した復元や巻き戻しが残っていないことを確認できた作業領域だけを削除する。不明な場合は削除しない
- Developmentチャンネルも使った場合は、そのバックアップ先と`%LOCALAPPDATA%\.kiten-restore-development-*`を同じ条件で確認する

次のものはKitenのデータフォルダーを削除しても自動では削除されない。

- ダウンロード済みの実ファイル
- WorkspaceやFolderTabから参照していた外部ファイル、フォルダー
- Webサイトやクラウド側に保存されたデータ
- 別チャンネルの`%LOCALAPPDATA%\TabBrowser-Dev`

## 8. 入手先と報告先の現在状態

実行用ZIPは公式GitHub配布リポジトリのReleasesから無料で入手する。通常の不具合と非公開の脆弱性・個人情報に関わる報告は専用メールアドレス `kiten.gesture.contact@gmail.com` へ送る。公開Issueへ機密事項を書かない。アプリから診断情報を自動送信することはない。
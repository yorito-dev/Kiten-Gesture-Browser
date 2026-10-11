# 初めて使う方へ

Kiten Gesture Browserは、Windows 11 x64向けの無料のタブブラウザです。
**開発中のアルファ版です。不具合や保存データが失われる可能性があります。重要なデータは別にも保存し、利用前・更新前にバックアップしてください。**

## 1. ダウンロードする

1. [公開アルファの配布ページ（v0.1.0-preview.1）](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.1)を開きます。
2. `Assets`から`Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip`と、同名の`.zip.sha256`をダウンロードします。`Source code`は実行用ではありません。
3. ZIPを保存したフォルダーをPowerShellで開き、次を実行します。表示された`Hash`が、配布ページと`.sha256`ファイルのSHA-256に一致することを確認します。一致しなければ起動しません。

```powershell
Get-FileHash -LiteralPath '.\Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip' -Algorithm SHA256
```

実行用ZIPには.NET Runtimeを含みます。.NET SDKの導入やソースのビルドは不要です。Webページの表示にはMicrosoft Edge WebView2 Evergreen Runtimeが必要です。導入方法は[動作条件](distribution-guide.md#2-動作条件)を確認してください。

## 2. 展開して起動する

1. ZIPを右クリックして「すべて展開」を選び、新しい空フォルダーへ展開します。ZIP内から直接実行しません。
2. 展開先の`TabBrowser.exe`をダブルクリックします。
3. `Ctrl+Shift+K`でコマンドパレットを開き、`Runtime診断`を検索して「バージョン情報・Runtime診断を開く」を選びます。「バージョン情報」タブの版番号とソースSHAを、配布ページの対応表にある「実行ファイルの埋め込みSHA」と照合します。

未署名のためWindowsの警告が出る場合があります。入手元とSHA-256を確認し、DefenderやSmartScreenを無効にしないでください。起動できない場合は[不具合報告](../REPORTING.md)へ進んでください。

## 3. まずは基本操作を試す

Webタブで、アドレス欄にURLや検索語を入力して`Enter`を押します。タブはクリックして切り替えます。

| やりたいこと | キー・ボタン | 既定のマウスジェスチャー |
| --- | --- | --- |
| アドレス欄を選ぶ | `Ctrl+L`、またはアドレス欄をクリック | — |
| 新しいタブを開く | `Ctrl+T`、またはタブ列の`+` | `↓→` |
| 前のページへ戻る | ツールバーの`←` | `←` |
| 次のページへ進む | ツールバーの`→` | `→` |
| 今のタブを閉じる | `Ctrl+W` | `↓` |
| 最近閉じたタブ・Windowを復元する | `Ctrl+Shift+T`、または`復元` | `←↑` |

**ジェスチャーは、Webページ上で右ボタンを押したまま矢印の順に動かし、操作名が表示されたら離します。** 例えば`↓→`は「下へ動かしてから右へ動かす」です。戻る・進むには、そのタブの移動履歴が必要です。設定を変更した場合は画面に表示される操作名を優先し、取り消すときは右ボタンを離す前に`Esc`を押します。

ブックマーク、Workspace、動画などの詳しい使い方は、展開先の`Help\user-guide.html`を開くか、[操作説明書](../Help/user-guide.html)を確認してください。

## 4. バックアップを作る

設定・ブックマーク・Workspace・メモ・タブの状態などを保存します。**更新前と、大切なデータを保存した後に実行してください。**

1. KitenのすべてのWindowとミニプレイヤーなどを通常の操作で終了します。強制終了しません。
2. [PowerShell 7以上を用意](distribution-guide.md#2-動作条件)し、Windows PowerShellまたはPowerShell 7で次を実行します。`C:\Kiten`は、`TabBrowser.exe`がある展開先のパスへ置き換えてください。

```powershell
pwsh -NoProfile -File 'C:\Kiten\Maintenance\Backup-KitenData.ps1' `
  -Channel stable -Destination "$env:USERPROFILE\KitenBackup"
```

3. 表示された対象と保存先を確認し、実行するときだけ大文字で`BACKUP`と入力します。引数なしで起動した配布版のデータは`stable`が対象です。
4. 「バックアップを作成し、manifest・SHA-256・SQLite整合性を検証しました」と表示されたことを確認します。ユーザーフォルダーの`KitenBackup`に作られた`Kiten-DataBackup-stable-…zip`と、その隣の`.zip.sha256`をセットで保管します。エラーが出た場合は完了扱いにせず、更新を続けないでください。

バックアップにはCookie・ログイン状態・ダウンロード済みファイル・外部ファイルを含みません。ZIPは暗号化されないため、公開場所へ置かないでください。復元が必要になったら[バックアップ・復元ガイド](backup-restore-guide.md)の手順を確認してください。

## 困ったとき

- 起動・更新・削除：[導入ガイド](distribution-guide.md)
- 動画などの対応範囲：[既知の制約](known-limitations.md)
- 不具合：[コピーして記入できる報告書式](../REPORTING.md#コピーして記入する書式)。現在のメール窓口は`kiten.gesture.contact@gmail.com`です。

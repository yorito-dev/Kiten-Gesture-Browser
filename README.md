# Kiten Gesture Browser — 公開アルファ版

> **本版は開発中のアルファ版です。正式版としての安定性は保証していません。**
> 不具合、仕様変更、保存データが失われる可能性があります。
> 重要データの唯一の保存先にせず、利用前・更新前にバックアップしてください。

Windows 11 x64向けの、マウスジェスチャーでWeb探索を行うタブブラウザです。本体ソースはMIT Licenseで提供します。

**[無料の公開アルファをダウンロード（v0.1.0-preview.1）](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.1)**

実行用ZIPと本体ソースを公開しています。使いたい方が無償で試せる、開発中の公開アルファ版です。事前招待や人数制限はありません。利用にあたっては[利用条件](LICENSE.txt)を確認してください。

## ソースからのビルド

このソースは公開用構成です。
MITで提供する本体と、別の条件が適用される依存部品を区別してください。
ソースZIPには.NET RuntimeやNuGetパッケージ自体は含めません。ビルド時に固定バージョンを取得します。

Windows 11 x64、PowerShell 7以上、.NET SDK 10.0.401以降（.NET Runtime 10.0.12以上を含むもの）が必要です。`global.json`は10.0.400を指定し、`latestFeature`で.NET 10の新しいfeature bandを許可しています。cloneしたフォルダー、またはソースZIPを展開したフォルダーの直下で実行します。

```powershell
pwsh -NoProfile -File .\Build-Source.ps1
```

アプリとDataToolをビルドし、同梱の公開構成向けテストを実行します。
出力は`bin/Release/public/net10.0-windows/TabBrowser.exe`と
`tools/Kiten.DataTool/bin/Release/net10.0-windows/Kiten.DataTool.exe`です。
開発データを使う場合はアプリに`--channel development`を指定します。
普段の保存データを変更しないよう、初回確認は専用のWindowsテストアカウントで行ってください。

同梱の.NETテストは保存・復元・データ保護を中心に選んだ既存テストです。
元リポジトリの全回帰試験や実GUI確認の代わりではありません。
Node.js 22以上とChromeがある場合は、`Build-Source.ps1 -BrowserTests`で
追加の既存ブラウザー回帰も実行できます。未実施項目は成功と扱いません。
元の開発用リポジトリで、このソースの元になったcommitを`SOURCE-PROVENANCE.json`の`upstreamCommit`に記録しています。
ソースからのビルドではRuntime診断に`公開検証未実施（未コミット変更を含む可能性）`と表示されます。配布用ZIPに対して行った検証を、このビルドが受けたことを示すものではありません。

## 実行用ZIPのダウンロード

[v0.1.0-preview.1の配布ページ](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.1)のAssetsから、次の実行用ZIPを取得してください。

[Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/download/v0.1.0-preview.1/Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip)

ZIPと同名の[SHA-256ファイル](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/download/v0.1.0-preview.1/Kiten-Gesture-Browser-0.1.0-preview.1-win-x64.zip.sha256)も提供しています。`Source code` ZIPはアプリの実行用ではありません。`Preview`という版表示は、この開発中の公開アルファ版を指します。

次の順で導入してください。

1. ReleasesにあるZIPのSHA-256を確認する。
2. 新しい空フォルダーへZIPをすべて展開する。
3. 展開後の`TabBrowser.exe`を起動する。
4. Runtime診断のソースSHAを、Releaseページの対応表と照合する。実行ファイルに記録されるSHAは開発用リポジトリのcommitで、対応表には同じ内容の公開commitとZIPのSHA-256を記載します。

[導入・更新・削除ガイド](docs/distribution-guide.md)と[バックアップ・復元ガイド](docs/backup-restore-guide.md)、[既知の制約](docs/known-limitations.md)を確認してください。

## 主な機能

- マウスジェスチャーによるタブ操作
- タブ・Window・分割表示の保存と復元
- Workspace、ブックマーク、メモ、FolderTab、QuickLook
- YouTubeミニプレイヤーと、既存ミニプレイヤー内のローカル動画再生
- 停止中のバックアップ・検証・復元を行う同梱DataTool

ローカル動画はWPF標準MediaElement方式で、対応形式はWindowsのコーデック環境に依存します。YouTubeは既存のWebView2方式です。LibVLCは同梱しません。

## 実行用Releaseの動作条件と既知の制約

- Windows 11 x64
- Microsoft Edge WebView2 Evergreen Runtime
- 実行用Releaseは.NET RuntimeをZIPへ自己完結同梱しています。ソースからのビルドには上記SDKが必要
- バックアップ／復元にはPowerShell 7以上が必要
- 初回は未署名。入手元とSHA-256を確認し、Windowsのセキュリティ機能を無効にしない
- 自動更新はありません。修正版は新しい版番号のZIPで案内します

すべてのWindows環境、動画形式、GPU、画面構成での動作を確認したものではありません。

## 無料利用と任意の支援

本体のダウンロードと利用は無料です。開発支援は任意で、支援の有無による機能差はありません。

本体の実行用ZIPは[GitHub Releases](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.1)で無料提供しています。

## 利用条件・プライバシー・報告

作者・著作権者表示は「ヨリト」です。本体ソースにはMIT Licenseが適用されます。商用利用・改変・再配布を認め、改変版のソース公開は義務にしません。著作権表示と許諾文を保持してください。第三者ソフトウェアにはそれぞれのライセンスが適用されます。

- [本体の利用条件](LICENSE.txt)
- [第三者ライセンス通知](THIRD-PARTY-NOTICES.txt)
- [プライバシー説明](PRIVACY.md)
- [不具合報告](REPORTING.md)
- [非公開の脆弱性報告](SECURITY.md)
- [変更履歴](CHANGELOG.md)

Cookie、認証情報、実DB、バックアップZIP、未加工ログを公開Issueへ添付しないでください。

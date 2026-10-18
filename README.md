# Kiten Gesture Browser — 公開アルファ版

> **本版は開発中のアルファ版です。正式版としての安定性は保証していません。**
> 不具合、仕様変更、保存データが失われる可能性があります。
> 重要データの唯一の保存先にせず、大切なデータを保存した後・更新前にバックアップしてください。
> Kitenの保存データがまだない初回のお試しでは、バックアップは必須ではありません。

Windows 11 x64向けの、マウスジェスチャーでWeb探索を行うタブブラウザです。本体ソースはMIT Licenseで提供します。

**[無料の公開アルファをダウンロード（v0.1.0-preview.2）](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.2)**

**[初めて使う方へ：ダウンロード・起動・基本操作・バックアップ](docs/getting-started.md)**

実行用ZIPと本体ソースを公開しています。使いたい方が無償で試せる、開発中の公開アルファ版です。事前招待や人数制限はありません。利用にあたっては[利用条件](LICENSE.txt)を確認してください。

## 主な機能

- マウスジェスチャー：戻る・進むなどのブラウザ操作を、マウスの動きで行えます。
- YouTubeミニプレイヤー：通常動画・Shorts・ライブ配信を小さな別ウィンドウで再生できます。動画を見ながらほかのタブで調べものをしたり、複数の動画を並べたりできます。位置や大きさを調整し、必要に応じて最前面に表示できます。
- タブ・Window・分割表示の保存と復元、ブックマーク、メモ
- Workspace：Webリンクやファイル・フォルダーへの参照、メモをカードとして整理する作業場所です。
- FolderTab：実際のフォルダーをタブで開き、ファイル一覧やフォルダーの移動を扱います。
- QuickLook：WorkspaceやFolderTabで選んだファイルを、Spaceキーなどでプレビューします。
- ローカル動画のミニプレイヤー：PC内の動画も、移動・サイズ変更できる別の小さなWindowで再生します。
- DataTool：Kitenを終了した状態で、設定やブックマークなどのバックアップ・検証・復元を行う保守ツールです。

ローカル動画はWPF標準MediaElement方式で、対応形式はWindowsのコーデック環境に依存します。YouTubeは既存のWebView2方式です。LibVLCは同梱しません。

## 動作条件と既知の制約

- Windows 11 x64
- Microsoft Edge WebView2 Evergreen Runtimeは、Windows 11には通常、最初から入っています。Webページが表示されない場合だけ、[動作条件](docs/distribution-guide.md#2-動作条件)の手順で導入してください
- 実行用Releaseは.NET RuntimeをZIPへ自己完結同梱しています。ソースからのビルドには末尾のSDK要件を参照
- バックアップはZIPの`Maintenance\Backup-KitenData.cmd`をダブルクリックして作成できます。[手順と旧版0.1.0-preview.1向けの補足](docs/backup-restore-guide.md#31-ダブルクリックで作成する)を参照
- PowerShellスクリプトによるバックアップ／復元にはPowerShell 7以上が必要。ダブルクリック版のバックアップでは不要
- この版は未署名。入手元とSHA-256を確認し、Windowsのセキュリティ機能を無効にしない
- 自動更新はありません。修正版は新しい版番号のZIPで案内します

すべてのWindows環境、動画形式、GPU、画面構成での動作を確認したものではありません。

## ダウンロードと導入

[配布ページ](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.2)のAssetsにある[Kiten-Gesture-Browser-0.1.0-preview.2-win-x64.zip](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/download/v0.1.0-preview.2/Kiten-Gesture-Browser-0.1.0-preview.2-win-x64.zip)と、同名の[SHA-256ファイル](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/download/v0.1.0-preview.2/Kiten-Gesture-Browser-0.1.0-preview.2-win-x64.zip.sha256)を取得してください。`Source code`は実行用ではありません。

1. 入手元とZIPのSHA-256を確認します。
2. 新しい空フォルダーへZIPをすべて展開します。
3. 展開先の`TabBrowser.exe`を起動します。

詳細は[初めて使う方へ](docs/getting-started.md)を参照してください。Windowsの警告が出たときは[警告画面が出たとき](docs/getting-started.md#警告画面が出たとき)へ進んでください。セキュリティ機能は無効にしません。

[導入・更新・削除ガイド](docs/distribution-guide.md)、[バックアップ・復元ガイド](docs/backup-restore-guide.md)、[既知の制約](docs/known-limitations.md)も用意しています。`Preview`という版表示は、この開発中の公開アルファ版を指します。

## 開発のきっかけ

タブブラウザを使い始めたころ、ページを開いたまま、気になるところを次々に見て回れるのがとても便利だと感じました。特にSleipnirやLunascapeでは、右ボタンを押しながらホイールを回してタブを切り替えられ、たくさんのサイトを快適に行き来できました。

その後はChromeやEdgeに乗り換えましたが、あのころのようにタブを行き来する使い方は、どちらも自分にはしっくりきませんでした。Vivaldiは理想に近かったものの、自分の使い方にはあと一歩なじみませんでした。

自分で作りたいと思って勉強したこともありましたが、なかなか形にできませんでした。AIエージェントの登場をきっかけに「自分にもできるかもしれない」と思い、Kitenの開発を始めました。

Kitenでも、右ボタンを押しながらホイールを回すとタブを切り替えられます。自分が心地よいと感じるタブ操作を形にしたいという思いで作っています。動画を見ながら別のタブで調べものができる、YouTubeミニプレイヤーにも力を入れています。

AIに聞けばすぐに答えが返ってくる時代になりました。それでも、気になるページを開いて自分の目で確かめていく楽しさは変わらないと思っています。Kitenは、AIの力を借りて作り始めたからこそ、そんな探索を心地よく続けられるブラウザを目指しています。タブをたくさん開いて、あちこち巡るのが好きな人に、少しでも気に入ってもらえたらうれしいです。

## 無料利用と任意の支援

本体のダウンロードと利用は無料です。開発支援は任意で、支援の有無による機能差はありません。

本体の実行用ZIPは[GitHub Releases](https://github.com/yorito-dev/Kiten-Gesture-Browser/releases/tag/v0.1.0-preview.2)で無料提供しています。

## 利用条件・プライバシー・報告

作者・著作権者表示は「ヨリト」です。本体ソースにはMIT Licenseが適用されます。商用利用・改変・再配布を認め、改変版のソース公開は義務にしません。著作権表示と許諾文を保持してください。第三者ソフトウェアにはそれぞれのライセンスが適用されます。

- [本体の利用条件](LICENSE.txt)
- [第三者ライセンス通知](THIRD-PARTY-NOTICES.txt)
- [プライバシー説明](PRIVACY.md)
- [不具合報告](REPORTING.md)
- [非公開の脆弱性報告](SECURITY.md)
- [変更履歴](CHANGELOG.md)

Cookie、認証情報、実DB、バックアップZIP、未加工ログを公開Issueへ添付しないでください。

## ソースからのビルド（開発者向け）

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

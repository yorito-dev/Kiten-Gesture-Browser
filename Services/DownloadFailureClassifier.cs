using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

public static class DownloadFailureClassifier
{
    public static DownloadFailureInfo ClassifyWebView2(CoreWebView2DownloadInterruptReason reason)
    {
        var name = reason.ToString();
        var normalized = Normalize(name);

        if (ContainsAny(normalized, "nospace", "diskfull"))
        {
            return new DownloadFailureInfo(
                "ディスク容量が不足しています",
                "保存先ドライブの空き容量を確保してから再試行してください。");
        }

        if (ContainsAny(normalized, "nametoolong", "path", "filename"))
        {
            return new DownloadFailureInfo(
                "ファイル名または保存先パスが長すぎます",
                "短いファイル名または浅い保存先フォルダーを指定して再試行してください。");
        }

        if (ContainsAny(normalized, "accessdenied", "security", "blocked", "policy", "filefailed"))
        {
            return new DownloadFailureInfo(
                "保存先またはセキュリティ設定で拒否されました",
                "別の保存先を選ぶか、Windows セキュリティや組織ポリシーの警告を確認してください。");
        }

        if (ContainsAny(normalized, "network", "timeout", "disconnected", "serverdown", "connection"))
        {
            return new DownloadFailureInfo(
                "ネットワーク接続が中断されました",
                "接続状態を確認してから再試行してください。");
        }

        if (ContainsAny(normalized, "server", "forbidden", "unauthorized", "badcontent", "norange", "contentlength", "crossorigin"))
        {
            return new DownloadFailureInfo(
                "サーバー側でダウンロードが拒否または中断されました",
                "ログイン状態や配布元ページを確認してください。制限回避は行いません。");
        }

        if (ContainsAny(normalized, "usercanceled", "usershutdown"))
        {
            return new DownloadFailureInfo(
                "ユーザー操作または終了処理で中断されました",
                "必要であれば再試行してください。");
        }

        return new DownloadFailureInfo(
            "ダウンロードが中断されました",
            "保存先、ネットワーク、配布元ページの状態を確認して再試行してください。");
    }

    public static string BuildDetailText(DownloadFailureInfo failure)
    {
        if (string.IsNullOrWhiteSpace(failure.Suggestion))
        {
            return failure.Reason;
        }

        return failure.Reason + " / " + failure.Suggestion;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(Normalize(needle), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        return new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(static c => !char.IsWhiteSpace(c) && c is not '_' and not '-' and not ':' and not '.' and not '/' and not '\\')
            .ToArray());
    }
}

public enum DownloadFailureKind
{
    General,
    YouTubePoTokenRequired,
    YouTubePoTokenProviderConnectionFailed
}

public sealed record DownloadFailureInfo(string Reason, string Suggestion)
{
    public DownloadFailureKind Kind { get; init; }
}

namespace TabBrowser.Services;

public static class SessionDiagnosticSummaryFormatter
{
    public const int MaximumEntryCount = 8;

    public static string Format(IReadOnlyList<string> lines)
    {
        var summaries = lines
            .Select(FormatLine)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (summaries.Count == 0)
        {
            return "直近のセッション診断を読み取れませんでした。";
        }

        return "直近のセッション診断:\n\n" + string.Join("\n\n", summaries);
    }

    private static string FormatLine(string line)
    {
        var parts = line.Split(" | ", StringSplitOptions.None);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Length; i++)
        {
            var separatorIndex = parts[i].IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            fields[parts[i][..separatorIndex]] = parts[i][(separatorIndex + 1)..];
        }

        fields.TryGetValue("operation", out var operation);
        fields.TryGetValue("detail", out var detail);
        fields.TryGetValue("type", out var type);
        fields.TryGetValue("message", out var message);

        var lines = new List<string>
        {
            "・" + parts[0],
            "  " + DescribeOperation(operation)
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add("  詳細: " + FormatDetail(detail));
        }

        if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(message))
        {
            lines.Add("  例外: " + string.Join(" / ", new[] { type, message }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        }

        return string.Join("\n", lines);
    }

    private static string DescribeOperation(string? operation)
    {
        return operation switch
        {
            "load-failed" => "セッションファイルの読み込みに失敗しました",
            "load-recovered" => "バックアップまたはスナップショットから復旧しました",
            "load-empty" => "復元できるセッションがなく、空の状態で開始しました",
            "repair-failed" => "復旧後のセッションファイル修復に失敗しました",
            "save-failed" => "セッション保存に失敗しました",
            "youtube-state-capture-failed" => "YouTube 再生状態の保存に失敗しました",
            "window-state-capture-failed" => "ウィンドウ状態の保存に失敗しました",
            "tab-state-capture-failed" => "タブ状態の保存に失敗しました",
            "startup-session-restore-failed" => "起動時のセッション復元に失敗しました",
            "startup-tab-activate-failed" => "復元したタブの読み込みに失敗しました",
            "protected-session-restore-failed" => "保護復元点の手動復元に失敗しました",
            "closed-tab-history-restore-failed" => "閉じたタブ履歴の復元に失敗しました",
            "tab-restore-failed" => "一部タブの復元に失敗しました",
            "window-placement-restore-failed" => "ウィンドウ位置の復元に失敗しました",
            "window-restore-failed" => "一部ウィンドウの復元に失敗しました",
            "webview-recover-failed" => "WebView2 の再作成に失敗しました",
            null or "" => "診断イベントを記録しました",
            _ => operation
        };
    }

    private static string FormatDetail(string detail)
    {
        var fields = detail
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part =>
            {
                var separatorIndex = part.IndexOf('=');
                return separatorIndex <= 0
                    ? (Key: string.Empty, Value: part)
                    : (Key: part[..separatorIndex], Value: part[(separatorIndex + 1)..]);
            })
            .Select(static item => item.Key switch
            {
                "source" => "復旧元=" + item.Value,
                "target" => "対象=" + item.Value,
                "file" => "ファイル=" + item.Value,
                "tab" => "タブ=" + item.Value,
                "window" => "ウィンドウ=" + item.Value,
                "windowTabs" => "ウィンドウ内タブ数=" + item.Value,
                "automatic" => "自動復旧=" + item.Value,
                _ => string.IsNullOrWhiteSpace(item.Key) ? item.Value : item.Key + "=" + item.Value
            })
            .ToList();
        return fields.Count == 0
            ? detail
            : string.Join(" / ", fields);
    }
}

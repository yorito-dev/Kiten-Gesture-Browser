namespace TabBrowser.Services;

public sealed class LinkCopyService(ClipboardWriteService clipboardWriteService)
{
    public async Task<LinkCopyResult> CopyTextAsync(string? text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LinkCopyResult.Failure("コピーするテキストがありません");
        }

        var result = await clipboardWriteService.SetTextAsync(text);
        return result.IsSuccess
            ? LinkCopyResult.Success(successMessage)
            : LinkCopyResult.Failure("コピーに失敗しました");
    }

    public Task<LinkCopyResult> CopyAsync(LinkCopyTarget target, LinkCopyFormat format, string? successMessage = null)
    {
        try
        {
            return CopyTextAsync(LinkFormatters.Format(target, format), successMessage ?? GetSuccessMessage(format));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(LinkCopyResult.Failure("コピーに失敗しました"));
        }
    }

    public Task<LinkCopyResult> CopyAllTabsMarkdownAsync(IEnumerable<TabExportItem> items)
    {
        try
        {
            var exportItems = items.ToList();
            if (exportItems.Count == 0)
            {
                return Task.FromResult(LinkCopyResult.Failure("コピーできるタブ URL がありません"));
            }

            return CopyTextAsync(LinkFormatters.FormatMarkdownList(exportItems), "全タブの Markdown リストをコピーしました");
        }
        catch (ArgumentException)
        {
            return Task.FromResult(LinkCopyResult.Failure("コピーに失敗しました"));
        }
    }

    private static string GetSuccessMessage(LinkCopyFormat format)
    {
        return format switch
        {
            LinkCopyFormat.Url => "URL をコピーしました",
            LinkCopyFormat.Title => "タイトルをコピーしました",
            LinkCopyFormat.TitleAndUrl => "タイトル + URL をコピーしました",
            LinkCopyFormat.Markdown => "Markdown リンクをコピーしました",
            LinkCopyFormat.Html => "HTML リンクをコピーしました",
            _ => "コピーしました"
        };
    }
}

public sealed record LinkCopyResult(bool IsSuccess, string Message)
{
    public static LinkCopyResult Success(string message)
    {
        return new LinkCopyResult(true, message);
    }

    public static LinkCopyResult Failure(string message)
    {
        return new LinkCopyResult(false, message);
    }
}

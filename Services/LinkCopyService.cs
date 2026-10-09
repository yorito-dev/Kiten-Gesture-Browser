using System.Windows;

namespace TabBrowser.Services;

public sealed class LinkCopyService
{
    public LinkCopyResult CopyText(string? text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LinkCopyResult.Failure("コピーするテキストがありません");
        }

        try
        {
            Clipboard.SetText(text);
            return LinkCopyResult.Success(successMessage);
        }
        catch (Exception ex)
        {
            return LinkCopyResult.Failure("コピーに失敗しました: " + ex.Message);
        }
    }

    public LinkCopyResult Copy(LinkCopyTarget target, LinkCopyFormat format)
    {
        return Copy(target, format, GetSuccessMessage(format));
    }

    public LinkCopyResult Copy(LinkCopyTarget target, LinkCopyFormat format, string successMessage)
    {
        try
        {
            var text = LinkFormatters.Format(target, format);
            Clipboard.SetText(text);
            return LinkCopyResult.Success(successMessage);
        }
        catch (Exception ex)
        {
            return LinkCopyResult.Failure("コピーに失敗しました: " + ex.Message);
        }
    }

    public LinkCopyResult CopyAllTabsMarkdown(IEnumerable<TabExportItem> items)
    {
        try
        {
            var exportItems = items.ToList();
            if (exportItems.Count == 0)
            {
                return LinkCopyResult.Failure("コピーできるタブ URL がありません");
            }

            Clipboard.SetText(LinkFormatters.FormatMarkdownList(exportItems));
            return LinkCopyResult.Success("全タブの Markdown リストをコピーしました");
        }
        catch (Exception ex)
        {
            return LinkCopyResult.Failure("コピーに失敗しました: " + ex.Message);
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

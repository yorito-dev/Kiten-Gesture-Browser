using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class BookmarkEditWindow : Window
{
    public BookmarkEditWindow(BookmarkItem bookmark)
    {
        InitializeComponent();
        Bookmark = bookmark;
        TitleBox.Text = bookmark.Title;
        UrlBox.Text = bookmark.Url;
        FolderBox.Text = bookmark.FolderPath;
        TitleBox.SelectAll();
        TitleBox.Focus();
    }

    public BookmarkItem Bookmark { get; }

    public string EditedTitle { get; private set; } = string.Empty;

    public string EditedUrl { get; private set; } = string.Empty;

    public string EditedFolderPath { get; private set; } = string.Empty;

    public bool IsAccepted { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var url = BookmarkService.NormalizeUrl(UrlBox.Text);
        if (!IsValidBookmarkUrl(url))
        {
            StatusText.Text = "http / https / file の URL を入力してください";
            return;
        }

        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = TryGetHost(url) ?? url;
        }

        EditedTitle = title;
        EditedUrl = url;
        EditedFolderPath = BookmarkService.NormalizeFolderPath(FolderBox.Text);
        IsAccepted = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        Close();
    }

    private static bool IsValidBookmarkUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }
}

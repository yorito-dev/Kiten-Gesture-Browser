using System.Text;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class DownloadDetailWindow : Window
{
    private readonly DownloadItem _item;
    private readonly Action<DownloadItem>? _retryRequested;
    private readonly ClipboardWriteService _clipboardWriteService;

    public DownloadDetailWindow(DownloadItem item, Action<DownloadItem>? retryRequested = null)
    {
        _clipboardWriteService = new ClipboardWriteService(Dispatcher);
        InitializeComponent();
        _item = item;
        _retryRequested = retryRequested;
        DetailTextBox.Text = BuildDetailText(item);
        RetryButton.IsEnabled = item.CanRetry && _retryRequested is not null;
    }

    private async void CopyErrorButton_Click(object sender, RoutedEventArgs e)
    {
        var text = !string.IsNullOrWhiteSpace(_item.ErrorText)
            ? _item.ErrorText
            : _item.LastOutputText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await CopyTextAsync(text);
        }
    }

    private async void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        await CopyTextAsync(DetailTextBox.Text);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_item.CanRetry || _retryRequested is null)
        {
            return;
        }

        _retryRequested(_item);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task CopyTextAsync(string text)
    {
        var result = await _clipboardWriteService.SetTextAsync(text);
        if (!result.IsSuccess)
        {
            MessageBox.Show(
                this,
                "クリップボードを使用できませんでした。少し待ってから再試行してください。",
                "コピー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string BuildDetailText(DownloadItem item)
    {
        var builder = new StringBuilder();
        AppendLine(builder, "ファイル名", item.FileName);
        AppendLine(builder, "状態", item.StateText);
        AppendLine(builder, "種類", item.KindText);
        AppendLine(builder, "進捗", item.ProgressText);
        AppendLine(builder, "サイズ", item.SizeText);
        AppendLine(builder, "速度", item.SpeedText);
        AppendLine(builder, "残り時間", item.RemainingTimeText);
        AppendLine(builder, "保存先", item.FilePath);
        AppendLine(builder, "URL", item.SourceUrl);
        AppendLine(builder, "Content-Type", item.ContentType);
        AppendLine(builder, "Content-Disposition", item.ContentDisposition);
        AppendLine(builder, "開始", item.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendLine(builder, "完了", item.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? string.Empty);
        AppendLine(builder, "外部終了コード", item.ExternalExitCode?.ToString() ?? string.Empty);
        AppendLine(builder, "警告", item.SafetyWarning);
        AppendLine(builder, "エラー", item.ErrorText);

        AppendLine(builder, "失敗理由", item.FailureReason);
        AppendLine(builder, "対処案", item.FailureSuggestion);

        if (!string.IsNullOrWhiteSpace(item.LastOutputText))
        {
            builder.AppendLine();
            builder.AppendLine("最後の出力:");
            builder.AppendLine(item.LastOutputText);
        }

        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string label, string value)
    {
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(value ?? string.Empty);
    }
}

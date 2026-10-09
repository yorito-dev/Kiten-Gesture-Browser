using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class SitePermissionPromptWindow : Window
{
    private readonly TaskCompletionSource<SitePermissionDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public SitePermissionPromptWindow(SitePermissionRequestInfo request)
    {
        InitializeComponent();
        Request = request;
        TitleText.Text = request.IsScreenCapture
            ? $"{request.SiteName} が画面共有を要求しています"
            : $"{request.SiteName} が「{request.PermissionName}」を要求しています";
        OriginText.Text = request.Origin;
        ContainerText.Text = request.IsPrivate
            ? "環境: プライベートWindow"
            : $"コンテナ: {request.ContainerName}";
        DescriptionText.Text = request.IsScreenCapture
            ? "続行すると、WebView2の画面選択UIが開きます。この許可は保存されません。"
            : "許可範囲を選択してください。拒否は権限一覧から既定へ戻せます。";

        if (request.IsScreenCapture)
        {
            AllowOnceButton.Content = "続行";
            AllowAlwaysButton.Visibility = Visibility.Collapsed;
        }
        else if (request.IsPrivate)
        {
            AllowAlwaysButton.Content = "このWindow中は許可";
        }

        Closed += (_, _) => Complete(SitePermissionDecision.Dismiss, closeWindow: false);
    }

    public SitePermissionRequestInfo Request { get; }

    public Task<SitePermissionDecision> Completion => _completion.Task;

    public void CancelForTabClose()
    {
        Complete(SitePermissionDecision.Dismiss);
    }

    private void AllowOnceButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(SitePermissionDecision.AllowOnce);
    }

    private void AllowAlwaysButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(SitePermissionDecision.AllowAlways);
    }

    private void DenyButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(SitePermissionDecision.Deny);
    }

    private void Complete(SitePermissionDecision decision, bool closeWindow = true)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completion.TrySetResult(decision);
        if (closeWindow && IsLoaded)
        {
            Close();
        }
    }
}

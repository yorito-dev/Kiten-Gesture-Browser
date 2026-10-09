using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabBrowser.Views;

public partial class GestureStatusHud : UserControl
{
    private static readonly Brush RecognizingBrush = CreateBrush(102, 227, 255);
    private static readonly Brush RegisteredBrush = CreateBrush(52, 211, 153);
    private static readonly Brush ContinuingBrush = CreateBrush(251, 191, 36);
    private static readonly Brush ErrorBrush = CreateBrush(248, 113, 113);

    public GestureStatusHud()
    {
        InitializeComponent();
    }

    public void ShowPresentation(
        string? route,
        GestureStatusHudState state,
        string primaryText,
        string? nextDirections = null)
    {
        var accent = state switch
        {
            GestureStatusHudState.Registered => RegisteredBrush,
            GestureStatusHudState.Continuing => ContinuingBrush,
            GestureStatusHudState.Unregistered or GestureStatusHudState.Cancelled => ErrorBrush,
            _ => RecognizingBrush
        };

        AccentBar.Background = accent;
        RouteText.Foreground = accent;
        StatusBorder.BorderBrush = accent;
        StatusText.Foreground = accent;
        RouteText.Text = string.IsNullOrWhiteSpace(route) ? "方向  …" : $"方向  {route}";
        StatusText.Text = state switch
        {
            GestureStatusHudState.Registered => "登録済み",
            GestureStatusHudState.Continuing => "入力中",
            GestureStatusHudState.Unregistered => "未登録",
            GestureStatusHudState.Cancelled => "キャンセル",
            _ => "認識中"
        };
        CommandText.Text = primaryText;
        NextText.Text = string.IsNullOrWhiteSpace(nextDirections)
            ? string.Empty
            : $"次  {nextDirections}";
        NextText.Visibility = string.IsNullOrWhiteSpace(nextDirections)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

public enum GestureStatusHudState
{
    Recognizing,
    Registered,
    Continuing,
    Unregistered,
    Cancelled
}

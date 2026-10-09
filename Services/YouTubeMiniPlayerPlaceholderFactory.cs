using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record YouTubeMiniPlayerPlaceholderParts(
    FrameworkElement Root,
    Button LoadButton,
    FrameworkElement CloseGestureIndicator);

public static class YouTubeMiniPlayerPlaceholderFactory
{
    public static YouTubeMiniPlayerPlaceholderParts Create(
        YouTubeMiniPlayerState state,
        Style buttonStyle)
    {
        var root = new Grid
        {
            Background = Brushes.Transparent
        };
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16)
        };
        panel.Children.Add(new TextBlock
        {
            Text = state.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(230, 236, 244)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(120, state.Width - 48),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var loadButton = YouTubeMiniPlayerChromeFactory.CreateButton(
            "読み込み",
            "ミニプレイヤーを読み込む",
            78,
            buttonStyle);
        panel.Children.Add(loadButton);
        root.Children.Add(panel);

        var closeGestureIndicator = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 7, 12, 7),
            Background = new SolidColorBrush(Color.FromArgb(224, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(138, 180, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "閉じる",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            }
        };
        Panel.SetZIndex(closeGestureIndicator, 1);
        root.Children.Add(closeGestureIndicator);

        return new YouTubeMiniPlayerPlaceholderParts(root, loadButton, closeGestureIndicator);
    }
}

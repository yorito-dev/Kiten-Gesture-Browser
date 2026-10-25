using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record YouTubeMiniPlayerTitleBandParts(
    FrameworkElement Root,
    TextBlock TitleText,
    Button BackButton,
    Button ForwardButton,
    Button LiveViewerCountButton,
    TextBlock LiveViewerCountText,
    Button ChatButton,
    Button TopmostButton,
    Button MinimizeButton,
    Button SourceButton,
    Button CloseButton);

public readonly record struct YouTubeMiniPlayerTitleBandVisibility(
    bool ShowChat,
    bool ShowTopmost,
    bool ShowMinimize,
    bool ShowSource,
    bool ShowClose);

public sealed record YouTubeMiniPlayerToolbarParts(
    FrameworkElement Root,
    Button ZoomResetButton,
    Button SpeedDownButton,
    Button SpeedResetButton,
    Button SpeedUpButton,
    Button LoopButton,
    Button AbRepeatButton,
    MenuItem AbRepeatStatusMenuItem,
    MenuItem AbRepeatSetStartMenuItem,
    MenuItem AbRepeatSetEndMenuItem,
    MenuItem AbRepeatToggleMenuItem,
    MenuItem AbRepeatClearMenuItem);

public static class YouTubeMiniPlayerChromeFactory
{
    internal const string LiveViewerCountFallbackText = "LIVEへ";
    internal const string LiveViewerCountToolTip = "クリックでライブ位置へ";

    private const double TitleBandHorizontalPadding = 12;
    private const double MinimumTitleWidth = 60;
    private const double NavigationHistoryButtonWidth = 28;
    private const string PreviousVideoGlyph = "\uE892";
    private const string NextVideoGlyph = "\uE893";
    private const double LiveViewerCountWidth = 108;
    private const double ButtonLeftMargin = 4;
    private const double ChatButtonWidth = 52;
    private const double TopmostButtonWidth = 68;
    private const double MinimizeButtonWidth = 54;
    private const double SourceButtonWidth = 48;
    private const double CloseButtonWidth = 26;

    public static YouTubeMiniPlayerTitleBandParts CreateTitleBand(string title, Style buttonStyle)
    {
        var titleBand = CreateChromeBorder(new Thickness(8, 4, 4, 4));

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleContent = new Grid();
        titleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var backButton = CreateButton(PreviousVideoGlyph, "前の動画へ戻る", NavigationHistoryButtonWidth, buttonStyle);
        backButton.Margin = new Thickness(0, 0, 3, 0);
        backButton.FontFamily = new FontFamily("Segoe Fluent Icons");
        backButton.FontSize = 13;
        backButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(backButton, 0);
        titleContent.Children.Add(backButton);

        var forwardButton = CreateButton(NextVideoGlyph, "次の動画へ進む", NavigationHistoryButtonWidth, buttonStyle);
        forwardButton.Margin = new Thickness(0, 0, 3, 0);
        forwardButton.FontFamily = new FontFamily("Segoe Fluent Icons");
        forwardButton.FontSize = 13;
        forwardButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(forwardButton, 1);
        titleContent.Children.Add(forwardButton);

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 246, 250)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var fullTitleText = new TextBlock
        {
            MaxWidth = 480,
            TextWrapping = TextWrapping.Wrap
        };
        fullTitleText.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text))
        {
            Source = titleText,
            Mode = BindingMode.OneWay
        });
        titleText.ToolTip = new ToolTip
        {
            Content = fullTitleText,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            Foreground = titleText.Foreground,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 51, 64)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 5, 7, 5)
        };
        ToolTipService.SetShowDuration(titleText, 30000);
        Grid.SetColumn(titleText, 2);
        titleContent.Children.Add(titleText);
        Grid.SetColumn(titleContent, 0);
        headerGrid.Children.Add(titleContent);

        var liveViewerCountText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var liveViewerCountButton = CreateButton(
            string.Empty,
            LiveViewerCountToolTip,
            LiveViewerCountWidth,
            buttonStyle);
        liveViewerCountButton.Width = double.NaN;
        liveViewerCountButton.MinWidth = 0;
        liveViewerCountButton.MaxWidth = LiveViewerCountWidth;
        liveViewerCountButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        liveViewerCountButton.Content = liveViewerCountText;
        liveViewerCountButton.Visibility = Visibility.Collapsed;
        liveViewerCountButton.IsEnabled = false;
        Grid.SetColumn(liveViewerCountButton, 1);
        headerGrid.Children.Add(liveViewerCountButton);

        var chatButton = CreateButton("チャット", "ライブチャット／リプレイを開く／閉じる", ChatButtonWidth, buttonStyle);
        UpdateChatButtonAvailability(chatButton, null);
        Grid.SetColumn(chatButton, 2);
        headerGrid.Children.Add(chatButton);

        var topmostButton = CreateButton("最前面", "常に最前面に表示", TopmostButtonWidth, buttonStyle);
        Grid.SetColumn(topmostButton, 3);
        headerGrid.Children.Add(topmostButton);

        var minimizeButton = CreateButton("最小化", "ミニプレイヤーを最小化", MinimizeButtonWidth, buttonStyle);
        minimizeButton.Content = "最小化";
        minimizeButton.ToolTip = "ミニプレイヤーをタスクバーへ最小化";
        Grid.SetColumn(minimizeButton, 4);
        headerGrid.Children.Add(minimizeButton);

        var sourceButton = CreateButton("元タブ", "元タブへ移動", SourceButtonWidth, buttonStyle);
        Grid.SetColumn(sourceButton, 5);
        headerGrid.Children.Add(sourceButton);

        var closeButton = CreateButton("×", "ミニプレイヤーを閉じる", CloseButtonWidth, buttonStyle);
        closeButton.FontSize = 16;
        closeButton.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(closeButton, 6);
        headerGrid.Children.Add(closeButton);

        titleBand.Child = headerGrid;
        return new YouTubeMiniPlayerTitleBandParts(
            titleBand,
            titleText,
            backButton,
            forwardButton,
            liveViewerCountButton,
            liveViewerCountText,
            chatButton,
            topmostButton,
            minimizeButton,
            sourceButton,
            closeButton);
    }

    public static YouTubeMiniPlayerTitleBandVisibility GetTitleBandButtonVisibility(
        double availableWidth,
        MiniPlayerSourceKind sourceKind,
        bool hasLiveViewerCount = false)
    {
        var normalizedWidth = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : 0;
        var supportsChat = sourceKind != MiniPlayerSourceKind.LocalFile;
        var remainingWidth = Math.Max(
            0,
            normalizedWidth
            - TitleBandHorizontalPadding
            - MinimumTitleWidth
            - (hasLiveViewerCount ? GetButtonOccupiedWidth(LiveViewerCountWidth) : 0)
            - GetButtonOccupiedWidth(supportsChat ? ChatButtonWidth : MinimizeButtonWidth)
            - GetButtonOccupiedWidth(CloseButtonWidth));

        if (!supportsChat)
        {
            var showTopmost = TryReserveButtonWidth(ref remainingWidth, TopmostButtonWidth);
            var showSource = TryReserveButtonWidth(ref remainingWidth, SourceButtonWidth);
            return new YouTubeMiniPlayerTitleBandVisibility(
                ShowChat: false,
                showTopmost,
                ShowMinimize: true,
                showSource,
                ShowClose: true);
        }

        var showYouTubeSource = TryReserveButtonWidth(ref remainingWidth, SourceButtonWidth);
        var showYouTubeMinimize = TryReserveButtonWidth(ref remainingWidth, MinimizeButtonWidth);
        var showYouTubeTopmost = TryReserveButtonWidth(ref remainingWidth, TopmostButtonWidth);
        return new YouTubeMiniPlayerTitleBandVisibility(
            ShowChat: true,
            showYouTubeTopmost,
            showYouTubeMinimize,
            showYouTubeSource,
            ShowClose: true);
    }

    internal static void UpdateTitleBandButtonVisibility(
        YouTubeMiniPlayerRuntime runtime,
        double availableWidth)
    {
        var visibility = GetTitleBandButtonVisibility(
            availableWidth,
            runtime.State.SourceKind,
            runtime.TitleBandLiveViewerCountButton.Visibility == Visibility.Visible);
        runtime.TitleBandChatButton.Visibility = ToVisibility(visibility.ShowChat);
        runtime.TitleBandTopmostButton.Visibility = ToVisibility(visibility.ShowTopmost);
        runtime.TitleBandMinimizeButton.Visibility = ToVisibility(visibility.ShowMinimize);
        runtime.TitleBandSourceButton.Visibility = ToVisibility(visibility.ShowSource);
        runtime.TitleBandCloseButton.Visibility = ToVisibility(visibility.ShowClose);
    }

    internal static void UpdateLiveViewerCount(
        YouTubeMiniPlayerRuntime runtime,
        bool isLive,
        string? viewerCountText)
    {
        var displayText = GetLiveViewerCountDisplayText(isLive, viewerCountText);
        runtime.TitleBandLiveViewerCountText.Text = displayText ?? string.Empty;
        runtime.TitleBandLiveViewerCountButton.Visibility = displayText is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        runtime.TitleBandLiveViewerCountButton.IsEnabled = displayText is not null
            && !runtime.IsLiveEdgeSeekInProgress;

        var availableWidth = double.IsFinite(runtime.TitleBand.Width)
            ? runtime.TitleBand.Width
            : runtime.State.Width;
        UpdateTitleBandButtonVisibility(runtime, availableWidth);
    }

    internal static void UpdateNavigationHistoryButtons(
        YouTubeMiniPlayerRuntime runtime,
        bool canGoBack,
        bool canGoForward)
    {
        runtime.NavigationHistory.Update(canGoBack, canGoForward);
        runtime.TitleBandBackButton.Visibility = ToVisibility(canGoBack);
        runtime.TitleBandForwardButton.Visibility = ToVisibility(canGoForward);
    }

    internal static string? GetLiveViewerCountDisplayText(
        bool isLive,
        string? viewerCountText)
    {
        if (!isLive)
        {
            return null;
        }

        return YouTubeMiniPlayerStateService.NormalizeLiveViewerCountText(viewerCountText)
            ?? LiveViewerCountFallbackText;
    }

    public static void UpdateTopmostButton(Button button, bool isTopmost)
    {
        button.Content = isTopmost ? "最前面解除" : "最前面";
        button.ToolTip = isTopmost
            ? "常に最前面を解除"
            : "常に最前面に表示";
    }

    internal static void UpdateChatButtonAvailability(YouTubeMiniPlayerRuntime runtime, bool? isAvailable)
    {
        runtime.LiveChatAvailability = isAvailable;
        ApplyChatButtonState(runtime.TitleBandChatButton, isAvailable, runtime.IsLiveChatWindowOpen);
    }

    internal static void UpdateChatWindowOpen(YouTubeMiniPlayerRuntime runtime, bool isOpen)
    {
        runtime.IsLiveChatWindowOpen = isOpen;
        ApplyChatButtonState(runtime.TitleBandChatButton, runtime.LiveChatAvailability, isOpen);
    }

    public static void UpdateChatButtonAvailability(Button button, bool? isAvailable)
        => ApplyChatButtonState(button, isAvailable, false);

    private static void ApplyChatButtonState(Button button, bool? isAvailable, bool isWindowOpen)
    {
        button.IsEnabled = isWindowOpen || isAvailable == true;
        button.ToolTip = isWindowOpen ? "ライブチャット／リプレイを閉じる" : isAvailable switch
        {
            true => "ライブチャット／リプレイを開く",
            false => "この動画ではチャットを利用できません",
            null => "チャット情報を確認中"
        };
    }

    public static YouTubeMiniPlayerToolbarParts CreateToolbar(Style buttonStyle)
    {
        var toolbar = CreateChromeBorder(new Thickness(8, 4, 8, 4));
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var zoomResetButton = CreateControlButton(
            "100%",
            "現在の動画倍率。クリックで100%と前回の倍率を切り替える（位置は中央）",
            48,
            buttonStyle);

        var speedDownButton = CreateControlButton("-", "YouTube の再生スピードを下げる", 26, buttonStyle);
        var speedResetButton = CreateControlButton("1x", "現在の再生スピード。クリックで 1.0x と前回の速度を切り替える", 44, buttonStyle);
        var speedUpButton = CreateControlButton("+", "YouTube の再生スピードを上げる", 26, buttonStyle);
        var loopButton = CreateControlButton("リピOFF", "YouTube のリピート再生を切り替える", 58, buttonStyle);
        var abRepeatButton = CreateControlButton("A-B", "A-Bリピートを設定", 52, buttonStyle);
        var abRepeatMenu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 77)),
            BorderThickness = new Thickness(1),
            Placement = PlacementMode.Top,
            PlacementTarget = abRepeatButton
        };
        var abRepeatStatusMenuItem = CreateAbRepeatMenuItem("A-B未設定", isEnabled: false);
        var abRepeatSetStartMenuItem = CreateAbRepeatMenuItem("A点を設定");
        var abRepeatSetEndMenuItem = CreateAbRepeatMenuItem("B点を設定");
        var abRepeatToggleMenuItem = CreateAbRepeatMenuItem("A-B再生を開始");
        var abRepeatClearMenuItem = CreateAbRepeatMenuItem("A-B範囲を消去");
        abRepeatMenu.Items.Add(abRepeatStatusMenuItem);
        abRepeatMenu.Items.Add(new Separator());
        abRepeatMenu.Items.Add(abRepeatSetStartMenuItem);
        abRepeatMenu.Items.Add(abRepeatSetEndMenuItem);
        abRepeatMenu.Items.Add(abRepeatToggleMenuItem);
        abRepeatMenu.Items.Add(abRepeatClearMenuItem);
        abRepeatButton.ContextMenu = abRepeatMenu;

        controls.Children.Add(zoomResetButton);
        controls.Children.Add(speedDownButton);
        controls.Children.Add(speedResetButton);
        controls.Children.Add(speedUpButton);
        controls.Children.Add(loopButton);
        controls.Children.Add(abRepeatButton);

        toolbar.Child = controls;
        return new YouTubeMiniPlayerToolbarParts(
            toolbar,
            zoomResetButton,
            speedDownButton,
            speedResetButton,
            speedUpButton,
            loopButton,
            abRepeatButton,
            abRepeatStatusMenuItem,
            abRepeatSetStartMenuItem,
            abRepeatSetEndMenuItem,
            abRepeatToggleMenuItem,
            abRepeatClearMenuItem);
    }

    public static void UpdateContentZoomButton(Button button, double zoomFactor)
    {
        button.Content = YouTubeMiniPlayerContentTransformState.FormatPercentage(zoomFactor);
        button.ToolTip = "現在の動画倍率。クリックで100%と前回の倍率を切り替える（位置は中央）";
    }

    public static void UpdateAbRepeatControls(
        YouTubeMiniPlayerToolbarParts parts,
        YouTubeMiniPlayerState state)
    {
        var hasStart = state.AbRepeatStartSeconds is { } start;
        var hasRange = YouTubeMiniPlayerAbRepeatState.HasValidRange(state);
        parts.AbRepeatButton.Content = state.IsAbRepeatEnabled
            ? "A-B中"
            : hasStart
                ? "A設定"
                : "A-B";
        parts.AbRepeatStatusMenuItem.Header = hasRange
            ? $"A {FormatTime(state.AbRepeatStartSeconds!.Value)} / B {FormatTime(state.AbRepeatEndSeconds!.Value)}"
            : hasStart
                ? $"A {FormatTime(state.AbRepeatStartSeconds!.Value)} / B 未設定"
                : "A-B未設定";
        parts.AbRepeatSetEndMenuItem.IsEnabled = hasStart;
        parts.AbRepeatToggleMenuItem.IsEnabled = hasRange;
        parts.AbRepeatToggleMenuItem.Header = state.IsAbRepeatEnabled
            ? "A-B再生を一時解除"
            : "A-B再生を開始";
        parts.AbRepeatClearMenuItem.IsEnabled = hasStart || state.AbRepeatEndSeconds.HasValue;
    }

    public static Thumb CreateResizeThumb(
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        double width,
        double height,
        Cursor cursor)
    {
        return new Thumb
        {
            Width = width,
            Height = height,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Cursor = cursor,
            Focusable = false,
            Template = CreateResizeThumbTemplate()
        };
    }

    private static Border CreateChromeBorder(Thickness padding)
    {
        var border = new Border
        {
            Padding = padding,
            Background = new SolidColorBrush(Color.FromArgb(210, 18, 22, 30)),
            Opacity = 0.72,
            Cursor = Cursors.SizeAll
        };
        border.MouseEnter += (_, _) => border.Opacity = 1.0;
        border.MouseLeave += (_, _) => border.Opacity = 0.72;
        return border;
    }

    private static Button CreateControlButton(string content, string toolTip, double width, Style buttonStyle)
    {
        var button = CreateButton(content, toolTip, width, buttonStyle);
        button.Height = 26;
        button.Margin = width == 58
            ? new Thickness(0)
            : new Thickness(0, 0, 3, 0);
        return button;
    }

    private static MenuItem CreateAbRepeatMenuItem(string header, bool isEnabled = true)
    {
        return new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            Background = new SolidColorBrush(Color.FromRgb(23, 27, 34)),
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4)
        };
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    public static Button CreateButton(string content, string toolTip, double width, Style buttonStyle)
    {
        var button = new Button
        {
            Width = width,
            Height = 23,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            Content = content,
            ToolTip = toolTip,
            FontSize = 11,
            Style = buttonStyle
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private static bool TryReserveButtonWidth(ref double remainingWidth, double buttonWidth)
    {
        var occupiedWidth = GetButtonOccupiedWidth(buttonWidth);
        if (remainingWidth < occupiedWidth)
        {
            return false;
        }

        remainingWidth -= occupiedWidth;
        return true;
    }

    private static double GetButtonOccupiedWidth(double buttonWidth)
    {
        return buttonWidth + ButtonLeftMargin;
    }

    private static Visibility ToVisibility(bool isVisible)
    {
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ControlTemplate CreateResizeThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        return new ControlTemplate(typeof(Thumb))
        {
            VisualTree = border
        };
    }
}

using System.Globalization;
using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record ChatGptReadabilityPanelElements(
    CheckBox ChatGptReadabilityEnabledCheckBox,
    ComboBox ChatGptFontScaleBox,
    ComboBox ChatGptLineHeightBox,
    ComboBox ChatGptThemeBox,
    ComboBox ChatGptContentWidthBox,
    ComboBox ChatGptCodeFontScaleBox,
    CheckBox ChatGptCodeWrapCheckBox,
    CheckBox ChatGptLargeInputCheckBox,
    CheckBox ChatGptFocusHighlightCheckBox);

internal interface IChatGptReadabilityPanelHost
{
    ChatGptReadabilitySettings Settings { get; set; }
    void SaveSettings();
    void ApplyToLoadedTabs();
    void SetStatus(string message);
}

// 選択肢・表示同期・入力変換・保存手順を所有し、実保存とWebView適用はWindowへ接続する。
internal sealed class ChatGptReadabilityPanelController
{
    private readonly ChatGptReadabilityPanelElements _elements;
    private readonly IChatGptReadabilityPanelHost _host;
    private bool _isSyncing;

    internal ChatGptReadabilityPanelController(
        ChatGptReadabilityPanelElements elements,
        IChatGptReadabilityPanelHost host)
    {
        _elements = elements;
        _host = host;
    }

    internal void Initialize()
    {
        _elements.ChatGptFontScaleBox.ItemsSource = CreateReadabilityPercentOptions();
        _elements.ChatGptLineHeightBox.ItemsSource = ChatGptReadabilityProfile.LineHeights
            .Select(static value => new ReadabilityOption(
                value.ToString("0.0", CultureInfo.InvariantCulture),
                value.ToString("0.0", CultureInfo.InvariantCulture)))
            .ToList();
        _elements.ChatGptThemeBox.ItemsSource = ChatGptReadabilityProfile.ThemeIds
            .Select(static value => new ReadabilityOption(ChatGptReadabilityProfile.GetThemeLabel(value), value))
            .ToList();
        _elements.ChatGptContentWidthBox.ItemsSource = ChatGptReadabilityProfile.WidthIds
            .Select(static value => new ReadabilityOption(ChatGptReadabilityProfile.GetWidthLabel(value), value))
            .ToList();
        _elements.ChatGptCodeFontScaleBox.ItemsSource = CreateReadabilityPercentOptions();
        Sync();
    }

    internal void Sync()
    {
        _host.Settings = ChatGptReadabilityProfile.Normalize(_host.Settings);
        var settings = _host.Settings;

        _isSyncing = true;
        try
        {
            _elements.ChatGptReadabilityEnabledCheckBox.IsChecked = settings.IsEnabled;
            _elements.ChatGptFontScaleBox.SelectedValue = settings.FontScalePercent.ToString(CultureInfo.InvariantCulture);
            _elements.ChatGptLineHeightBox.SelectedValue = settings.LineHeight.ToString("0.0", CultureInfo.InvariantCulture);
            _elements.ChatGptThemeBox.SelectedValue = settings.Theme;
            _elements.ChatGptContentWidthBox.SelectedValue = settings.ContentWidth;
            _elements.ChatGptCodeFontScaleBox.SelectedValue = settings.CodeFontScalePercent.ToString(CultureInfo.InvariantCulture);
            _elements.ChatGptCodeWrapCheckBox.IsChecked = settings.WrapCodeBlocks;
            _elements.ChatGptLargeInputCheckBox.IsChecked = settings.LargeInput;
            _elements.ChatGptFocusHighlightCheckBox.IsChecked = settings.HighlightKeyboardFocus;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    internal void SaveFromPanel()
    {
        if (_isSyncing)
        {
            return;
        }

        var settings = new ChatGptReadabilitySettings
        {
            IsEnabled = _elements.ChatGptReadabilityEnabledCheckBox.IsChecked == true,
            FontScalePercent = ParseSelectedInt(_elements.ChatGptFontScaleBox, 100),
            LineHeight = ParseSelectedDouble(_elements.ChatGptLineHeightBox, 1.6),
            Theme = GetSelectedValue(_elements.ChatGptThemeBox, ChatGptReadabilityProfile.ThemeStandard),
            ContentWidth = GetSelectedValue(_elements.ChatGptContentWidthBox, ChatGptReadabilityProfile.WidthStandard),
            CodeFontScalePercent = ParseSelectedInt(_elements.ChatGptCodeFontScaleBox, 100),
            WrapCodeBlocks = _elements.ChatGptCodeWrapCheckBox.IsChecked == true,
            LargeInput = _elements.ChatGptLargeInputCheckBox.IsChecked == true,
            HighlightKeyboardFocus = _elements.ChatGptFocusHighlightCheckBox.IsChecked == true
        };

        _host.Settings = ChatGptReadabilityProfile.Normalize(settings);
        _host.SaveSettings();
        Sync();
        _host.ApplyToLoadedTabs();
        _host.SetStatus(
            _host.Settings.IsEnabled
                ? "ChatGPT 表示設定を適用しました"
                : "ChatGPT 読みやすさをオフにしました");
    }

    private static List<ReadabilityOption> CreateReadabilityPercentOptions()
    {
        return ChatGptReadabilityProfile.FontScalePercents
            .Select(static value => new ReadabilityOption(
                $"{value}%",
                value.ToString(CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static int ParseSelectedInt(ComboBox comboBox, int fallback)
    {
        return int.TryParse(GetSelectedValue(comboBox, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static double ParseSelectedDouble(ComboBox comboBox, double fallback)
    {
        return double.TryParse(
            GetSelectedValue(comboBox, string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
    }

    private static string GetSelectedValue(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedValue as string ?? fallback;
    }

    private sealed class ReadabilityOption(string label, string value)
    {
        public string Label { get; } = label;

        public string Value { get; } = value;
    }

    internal void Reset()
    {
        _host.Settings = new ChatGptReadabilitySettings();
        _host.SaveSettings();
        Sync();
        _host.ApplyToLoadedTabs();
        _host.SetStatus("ChatGPT 読みやすさを標準表示に戻しました");
    }
}

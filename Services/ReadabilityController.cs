using System.Globalization;
using System.Text;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ReadabilityController
{
    public const string StyleElementId = "tabbrowser-chatgpt-readability-style";
    public const string WideElementAttributeName = "data-tabbrowser-readability-wide";

    public bool IsChatGptUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsHttpOrHttps(uri))
        {
            return false;
        }

        return uri.Host.Equals(ChatGptReadabilityProfile.Host, StringComparison.OrdinalIgnoreCase);
    }

    public string BuildInjectReadabilityStyleScript(ChatGptReadabilitySettings? settings)
    {
        var normalized = ChatGptReadabilityProfile.Normalize(settings);
        if (!normalized.IsEnabled)
        {
            return BuildResetReadabilityStyleScript();
        }

        var css = BuildReadabilityCss(settings);
        if (string.IsNullOrWhiteSpace(css))
        {
            return BuildResetReadabilityStyleScript();
        }

        var styleIdJson = JsonSerializer.Serialize(StyleElementId);
        var wideAttributeJson = JsonSerializer.Serialize(WideElementAttributeName);
        var cssJson = JsonSerializer.Serialize(css);
        var enableWideTargetsJson = JsonSerializer.Serialize(normalized.ContentWidth.Equals(ChatGptReadabilityProfile.WidthMaximum, StringComparison.Ordinal));
        return $$"""
            (() => {
                const styleId = {{styleIdJson}};
                const wideAttribute = {{wideAttributeJson}};
                const css = {{cssJson}};
                const enableWideTargets = {{enableWideTargetsJson}};
                const observerKey = "__tabBrowserChatGptReadabilityObserver";
                const parent = document.head || document.documentElement;
                if (!parent) {
                    return;
                }

                let style = document.getElementById(styleId);
                if (!style) {
                    style = document.createElement("style");
                    style.id = styleId;
                    style.type = "text/css";
                    parent.appendChild(style);
                }

                if (style.textContent !== css) {
                    style.textContent = css;
                }

                const clearWideTargets = () => {
                    document.querySelectorAll(`[${wideAttribute}]`).forEach((element) => {
                        element.removeAttribute(wideAttribute);
                    });
                };

                const markWideAncestors = (anchor) => {
                    if (!anchor || !(anchor instanceof Element)) {
                        return;
                    }

                    const root = anchor.closest("main, [role=\"main\"]");
                    let current = anchor;
                    let depth = 0;
                    while (current && current !== root && depth < 16) {
                        if (current instanceof HTMLElement) {
                            current.setAttribute(wideAttribute, "true");
                        }

                        current = current.parentElement;
                        depth += 1;
                    }
                };

                const markWideTargets = () => {
                    clearWideTargets();
                    if (!enableWideTargets) {
                        return;
                    }

                    const anchorSelector = [
                        "main article",
                        "main [data-message-author-role]",
                        "main .markdown",
                        "main .prose",
                        "main pre",
                        "[role=\"main\"] article",
                        "[role=\"main\"] [data-message-author-role]",
                        "[role=\"main\"] .markdown",
                        "[role=\"main\"] .prose",
                        "[role=\"main\"] pre"
                    ].join(",");
                    document.querySelectorAll(anchorSelector).forEach(markWideAncestors);
                };

                if (window[observerKey]) {
                    window[observerKey].disconnect();
                    delete window[observerKey];
                }

                markWideTargets();

                if (enableWideTargets) {
                    let pending = false;
                    const scheduleMarkWideTargets = () => {
                        if (pending) {
                            return;
                        }

                        pending = true;
                        requestAnimationFrame(() => {
                            pending = false;
                            markWideTargets();
                        });
                    };

                    const observer = new MutationObserver(scheduleMarkWideTargets);
                    observer.observe(document.body || document.documentElement, {
                        childList: true,
                        subtree: true,
                        attributes: true,
                        attributeFilter: ["class", "style"]
                    });
                    window[observerKey] = observer;
                }
            })();
            """;
    }

    public string BuildResetReadabilityStyleScript()
    {
        var styleIdJson = JsonSerializer.Serialize(StyleElementId);
        var wideAttributeJson = JsonSerializer.Serialize(WideElementAttributeName);
        return $$"""
            (() => {
                document.getElementById({{styleIdJson}})?.remove();
                const wideAttribute = {{wideAttributeJson}};
                document.querySelectorAll(`[${wideAttribute}]`).forEach((element) => {
                    element.removeAttribute(wideAttribute);
                });

                const observerKey = "__tabBrowserChatGptReadabilityObserver";
                if (window[observerKey]) {
                    window[observerKey].disconnect();
                    delete window[observerKey];
                }
            })();
            """;
    }

    public string BuildReadabilityCss(ChatGptReadabilitySettings? settings)
    {
        var normalized = ChatGptReadabilityProfile.Normalize(settings);
        if (!normalized.IsEnabled)
        {
            return string.Empty;
        }

        var theme = ThemePresets.Get(normalized.Theme);
        var fontScale = ToCssScale(normalized.FontScalePercent);
        var codeFontScale = ToCssScale(normalized.CodeFontScalePercent);
        var lineHeight = normalized.LineHeight.ToString("0.0", CultureInfo.InvariantCulture);
        var maxWidth = ChatGptReadabilityProfile.GetContentMaxWidth(normalized.ContentWidth);
        var isMaximumWidth = normalized.ContentWidth.Equals(ChatGptReadabilityProfile.WidthMaximum, StringComparison.Ordinal);
        var contentMaxWidth = isMaximumWidth ? "none" : "min(100%, var(--tabbrowser-chatgpt-content-max-width))";
        var contentWidth = isMaximumWidth ? "100%" : "min(100%, var(--tabbrowser-chatgpt-content-max-width))";
        var contentMargin = isMaximumWidth ? "0" : "auto";
        var codeWhiteSpace = normalized.WrapCodeBlocks ? "pre-wrap" : "pre";
        var codeOverflowX = normalized.WrapCodeBlocks ? "hidden" : "auto";
        var codeOverflowWrap = normalized.WrapCodeBlocks ? "anywhere" : "normal";
        var codeWordBreak = normalized.WrapCodeBlocks ? "break-word" : "normal";

        var css = new StringBuilder();
        css.AppendLine(":root {");
        css.AppendLine($"  --tabbrowser-chatgpt-font-scale: {fontScale};");
        css.AppendLine($"  --tabbrowser-chatgpt-code-font-scale: {codeFontScale};");
        css.AppendLine($"  --tabbrowser-chatgpt-line-height: {lineHeight};");
        css.AppendLine($"  --tabbrowser-chatgpt-content-max-width: {maxWidth};");
        css.AppendLine("  --thread-content-max-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("  --thread-content-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("  --thread-content-margin: 0px !important;");
        css.AppendLine("  --chatgpt-thread-content-max-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.ContentVariableTargets} {{");
        css.AppendLine("  --thread-content-max-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("  --thread-content-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("  --thread-content-margin: 0px !important;");
        css.AppendLine("  --chatgpt-thread-content-max-width: var(--tabbrowser-chatgpt-content-max-width) !important;");
        css.AppendLine("}");
        css.AppendLine();

        if (!theme.IsStandard)
        {
            css.AppendLine("html, body,");
            css.AppendLine($"{ChatGptSelectors.Main} {{");
            css.AppendLine($"  background: {theme.Background} !important;");
            css.AppendLine($"  color: {theme.Text} !important;");
            css.AppendLine($"  color-scheme: {theme.ColorScheme};");
            css.AppendLine("}");
            css.AppendLine();
        }

        css.AppendLine($"{ChatGptSelectors.Article} {{");
        css.AppendLine("  font-size: calc(1rem * var(--tabbrowser-chatgpt-font-scale)) !important;");
        css.AppendLine("  line-height: var(--tabbrowser-chatgpt-line-height) !important;");
        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.TextContent} {{");
        css.AppendLine("  font-size: calc(1rem * var(--tabbrowser-chatgpt-font-scale)) !important;");
        css.AppendLine("  line-height: var(--tabbrowser-chatgpt-line-height) !important;");
        if (!theme.IsStandard)
        {
            css.AppendLine($"  color: {theme.Text} !important;");
        }

        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.MessageContent} {{");
        css.AppendLine($"  max-width: {contentMaxWidth} !important;");
        css.AppendLine($"  width: {contentWidth} !important;");
        css.AppendLine($"  margin-left: {contentMargin} !important;");
        css.AppendLine($"  margin-right: {contentMargin} !important;");
        css.AppendLine("  align-self: stretch !important;");
        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.ContentWidthTargets} {{");
        css.AppendLine($"  max-width: {contentMaxWidth} !important;");
        css.AppendLine($"  width: {contentWidth} !important;");
        css.AppendLine($"  flex-basis: {contentWidth} !important;");
        css.AppendLine("  align-self: stretch !important;");
        css.AppendLine("}");
        css.AppendLine();
        if (isMaximumWidth)
        {
            css.AppendLine($"{ChatGptSelectors.MaximumWidthTargets} {{");
            css.AppendLine("  max-width: none !important;");
            css.AppendLine("  width: 100% !important;");
            css.AppendLine("  min-width: 0 !important;");
            css.AppendLine("  flex-basis: 100% !important;");
            css.AppendLine("  margin-left: 0 !important;");
            css.AppendLine("  margin-right: 0 !important;");
            css.AppendLine("  box-sizing: border-box !important;");
            css.AppendLine("  max-inline-size: none !important;");
            css.AppendLine("  inline-size: 100% !important;");
            css.AppendLine("  grid-column: 1 / -1 !important;");
            css.AppendLine("  justify-self: stretch !important;");
            css.AppendLine("  align-self: stretch !important;");
            css.AppendLine("}");
            css.AppendLine();
        }

        css.AppendLine($"{ChatGptSelectors.CodeBlock} {{");
        css.AppendLine("  max-width: 100% !important;");
        css.AppendLine("  min-width: 0 !important;");
        css.AppendLine("  width: 100% !important;");
        css.AppendLine("  box-sizing: border-box !important;");
        css.AppendLine($"  overflow-x: {codeOverflowX} !important;");
        css.AppendLine($"  white-space: {codeWhiteSpace} !important;");
        css.AppendLine($"  overflow-wrap: {codeOverflowWrap} !important;");
        css.AppendLine($"  word-break: {codeWordBreak} !important;");
        css.AppendLine("  font-size: calc(1rem * var(--tabbrowser-chatgpt-code-font-scale)) !important;");
        css.AppendLine("  line-height: 1.55 !important;");
        css.AppendLine($"  background: {theme.CodeBackground} !important;");
        css.AppendLine($"  color: {theme.CodeText} !important;");
        css.AppendLine($"  border-color: {theme.Border} !important;");

        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.CodeText} {{");
        css.AppendLine("  font-size: calc(1rem * var(--tabbrowser-chatgpt-code-font-scale)) !important;");
        css.AppendLine("  line-height: inherit !important;");
        css.AppendLine($"  white-space: {codeWhiteSpace} !important;");
        css.AppendLine($"  overflow-wrap: {codeOverflowWrap} !important;");
        css.AppendLine($"  word-break: {codeWordBreak} !important;");
        css.AppendLine("  min-width: 0 !important;");
        css.AppendLine("  max-width: 100% !important;");
        css.AppendLine("  background: transparent !important;");
        css.AppendLine($"  color: {theme.CodeText} !important;");

        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.CodeScrollContainer} {{");
        css.AppendLine($"  overflow-x: {codeOverflowX} !important;");
        css.AppendLine("  max-width: 100% !important;");
        css.AppendLine("  min-width: 0 !important;");
        css.AppendLine("  background: " + theme.CodeBackground + " !important;");
        css.AppendLine("  border-color: " + theme.Border + " !important;");
        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.CodeWrapTargets} {{");
        css.AppendLine("  max-width: 100% !important;");
        css.AppendLine("  min-width: 0 !important;");
        css.AppendLine($"  white-space: {codeWhiteSpace} !important;");
        css.AppendLine($"  overflow-wrap: {codeOverflowWrap} !important;");
        css.AppendLine($"  word-break: {codeWordBreak} !important;");
        css.AppendLine("  background: transparent !important;");
        css.AppendLine("}");
        css.AppendLine();
        css.AppendLine($"{ChatGptSelectors.InputArea} {{");
        css.AppendLine("  font-size: calc(1rem * var(--tabbrowser-chatgpt-font-scale)) !important;");
        css.AppendLine("  line-height: var(--tabbrowser-chatgpt-line-height) !important;");
        if (!theme.IsStandard)
        {
            css.AppendLine($"  background: {theme.Surface} !important;");
            css.AppendLine($"  color: {theme.Text} !important;");
            css.AppendLine($"  border-color: {theme.Border} !important;");
        }

        if (normalized.LargeInput)
        {
            css.AppendLine("  min-height: 8rem !important;");
        }

        css.AppendLine("}");

        if (normalized.HighlightKeyboardFocus)
        {
            css.AppendLine();
            css.AppendLine($"{ChatGptSelectors.Focusable} {{");
            css.AppendLine($"  outline: 3px solid {theme.Focus} !important;");
            css.AppendLine("  outline-offset: 2px !important;");
            css.AppendLine($"  box-shadow: 0 0 0 4px {theme.FocusShadow} !important;");
            css.AppendLine("}");
        }

        return css.ToString();
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToCssScale(int percent)
    {
        return (percent / 100d).ToString("0.##", CultureInfo.InvariantCulture);
    }
}

public static class ChatGptReadabilityProfile
{
    public const string Host = "chatgpt.com";
    public const string ThemeStandard = "standard";
    public const string ThemeSoftLight = "softLight";
    public const string ThemeSepia = "sepia";
    public const string ThemeGray = "gray";
    public const string ThemeDark = "dark";
    public const string ThemeBlack = "black";
    public const string ThemeHighContrast = "highContrast";
    public const string WidthNarrow = "narrow";
    public const string WidthStandard = "standard";
    public const string WidthWide = "wide";
    public const string WidthMaximum = "maximum";

    public static IReadOnlyList<int> FontScalePercents { get; } = [80, 90, 100, 110, 125, 150, 175, 200];

    public static IReadOnlyList<double> LineHeights { get; } = [1.4, 1.6, 1.8, 2.0];

    public static IReadOnlyList<string> ThemeIds { get; } =
    [
        ThemeStandard,
        ThemeSoftLight,
        ThemeSepia,
        ThemeGray,
        ThemeDark,
        ThemeBlack,
        ThemeHighContrast
    ];

    public static IReadOnlyList<string> WidthIds { get; } =
    [
        WidthNarrow,
        WidthStandard,
        WidthWide,
        WidthMaximum
    ];

    public static ChatGptReadabilitySettings Normalize(ChatGptReadabilitySettings? settings)
    {
        settings ??= new ChatGptReadabilitySettings();

        return new ChatGptReadabilitySettings
        {
            IsEnabled = settings.IsEnabled,
            FontScalePercent = FontScalePercents.Contains(settings.FontScalePercent)
                ? settings.FontScalePercent
                : 100,
            LineHeight = LineHeights.Contains(settings.LineHeight)
                ? settings.LineHeight
                : 1.6,
            Theme = ThemeIds.Contains(settings.Theme, StringComparer.Ordinal)
                ? settings.Theme
                : ThemeStandard,
            ContentWidth = WidthIds.Contains(settings.ContentWidth, StringComparer.Ordinal)
                ? settings.ContentWidth
                : WidthStandard,
            CodeFontScalePercent = FontScalePercents.Contains(settings.CodeFontScalePercent)
                ? settings.CodeFontScalePercent
                : 100,
            WrapCodeBlocks = settings.WrapCodeBlocks,
            LargeInput = settings.LargeInput,
            HighlightKeyboardFocus = settings.HighlightKeyboardFocus
        };
    }

    public static string GetContentMaxWidth(string width)
    {
        return width switch
        {
            WidthNarrow => "720px",
            WidthWide => "1040px",
            WidthMaximum => "100%",
            _ => "860px"
        };
    }

    public static string GetThemeLabel(string theme)
    {
        return theme switch
        {
            ThemeSoftLight => "ソフトライト",
            ThemeSepia => "セピア",
            ThemeGray => "グレー",
            ThemeDark => "ダーク",
            ThemeBlack => "ブラック",
            ThemeHighContrast => "高コントラスト",
            _ => "標準"
        };
    }

    public static string GetWidthLabel(string width)
    {
        return width switch
        {
            WidthNarrow => "狭い",
            WidthWide => "広い",
            WidthMaximum => "最大",
            _ => "標準"
        };
    }
}

public static class ChatGptSelectors
{
    public const string Main =
        "main, [role=\"main\"]";

    public const string Article =
        "main article, [role=\"main\"] article";

    public const string MessageContent =
        "main article, main article > div, main [data-message-author-role], main [data-message-author-role] > div, [role=\"main\"] article, [role=\"main\"] [data-message-author-role]";

    public const string ContentVariableTargets =
        "main, [role=\"main\"], main [style*=\"--thread-content-max-width\"], main [style*=\"--thread-content-width\"], [role=\"main\"] [style*=\"--thread-content-max-width\"], [role=\"main\"] [style*=\"--thread-content-width\"]";

    public const string ContentWidthTargets =
        "main [class*=\"max-w-3xl\"], main [class*=\"max-w-4xl\"], main [class*=\"max-w-5xl\"], main [class*=\"max-w-6xl\"], main [class*=\"max-w-[\"], main [class*=\"w-[var(--thread-content\"], main [style*=\"--thread-content-max-width\"], main [style*=\"--thread-content-width\"], main .markdown, main .prose, main [data-message-author-role] .markdown, [role=\"main\"] [class*=\"max-w-3xl\"], [role=\"main\"] [class*=\"max-w-4xl\"], [role=\"main\"] [class*=\"max-w-5xl\"], [role=\"main\"] [class*=\"max-w-6xl\"], [role=\"main\"] [class*=\"max-w-[\"], [role=\"main\"] [class*=\"w-[var(--thread-content\"], [role=\"main\"] [style*=\"--thread-content-max-width\"], [role=\"main\"] [style*=\"--thread-content-width\"], [role=\"main\"] .markdown, [role=\"main\"] .prose, [role=\"main\"] [data-message-author-role] .markdown";

    public const string MaximumWidthTargets =
        "main [data-tabbrowser-readability-wide=\"true\"], [role=\"main\"] [data-tabbrowser-readability-wide=\"true\"], main article > div, main article [class*=\"mx-auto\"], main article [class*=\"m-auto\"], main article [class*=\"max-w-\"], main article [class*=\"w-full\"], main article .markdown, main article .prose, main article [class*=\"text-message\"], main article [class*=\"conversation-turn\"], main article [class*=\"agent-turn\"], [role=\"main\"] article > div, [role=\"main\"] article [class*=\"mx-auto\"], [role=\"main\"] article [class*=\"m-auto\"], [role=\"main\"] article [class*=\"max-w-\"], [role=\"main\"] article [class*=\"w-full\"], [role=\"main\"] article .markdown, [role=\"main\"] article .prose, [role=\"main\"] article [class*=\"text-message\"], [role=\"main\"] article [class*=\"conversation-turn\"], [role=\"main\"] article [class*=\"agent-turn\"]";

    public const string TextContent =
        "main article p, main article li, main article blockquote, main article table, main article span, main article .markdown, main article .prose, main article [class*=\"text-message\"], main [data-message-author-role], main [data-message-author-role] p, main [data-message-author-role] li, main [data-message-author-role] blockquote, main [data-message-author-role] table, main [data-message-author-role] span, main [data-message-author-role] .markdown, main [data-message-author-role] .prose, main [data-message-author-role] [class*=\"text-message\"], main [data-message-author-role] [class*=\"whitespace-pre-wrap\"], [role=\"main\"] article p, [role=\"main\"] article li, [role=\"main\"] article blockquote, [role=\"main\"] article table, [role=\"main\"] article span, [role=\"main\"] article .markdown, [role=\"main\"] article .prose, [role=\"main\"] article [class*=\"text-message\"], [role=\"main\"] [data-message-author-role], [role=\"main\"] [data-message-author-role] p, [role=\"main\"] [data-message-author-role] li, [role=\"main\"] [data-message-author-role] blockquote, [role=\"main\"] [data-message-author-role] table, [role=\"main\"] [data-message-author-role] span, [role=\"main\"] [data-message-author-role] .markdown, [role=\"main\"] [data-message-author-role] .prose, [role=\"main\"] [data-message-author-role] [class*=\"text-message\"], [role=\"main\"] [data-message-author-role] [class*=\"whitespace-pre-wrap\"]";

    public const string CodeBlock =
        "main pre, main .markdown pre, [role=\"main\"] pre, [role=\"main\"] .markdown pre";

    public const string CodeText =
        "main pre *, main pre code, main pre code *, main pre span, main article pre *, main [data-message-author-role] pre *, main [data-message-author-role] pre code, main [data-message-author-role] pre code *, main [data-message-author-role] pre span, [role=\"main\"] pre *, [role=\"main\"] pre code, [role=\"main\"] pre code *, [role=\"main\"] pre span, [role=\"main\"] article pre *, [role=\"main\"] [data-message-author-role] pre *, [role=\"main\"] [data-message-author-role] pre code, [role=\"main\"] [data-message-author-role] pre code *, [role=\"main\"] [data-message-author-role] pre span";

    public const string CodeScrollContainer =
        "main .markdown [class*=\"overflow-x-auto\"], main .markdown [class*=\"overflow-x-scroll\"], main pre [class*=\"overflow-x-auto\"], [role=\"main\"] .markdown [class*=\"overflow-x-auto\"], [role=\"main\"] .markdown [class*=\"overflow-x-scroll\"], [role=\"main\"] pre [class*=\"overflow-x-auto\"]";

    public const string CodeWrapTargets =
        "main .markdown pre *, main .markdown [class*=\"overflow-x-auto\"] *, main .markdown [class*=\"overflow-x-scroll\"] *, main .markdown [class*=\"whitespace-pre\"], main .markdown [class*=\"whitespace-nowrap\"], [role=\"main\"] .markdown pre *, [role=\"main\"] .markdown [class*=\"overflow-x-auto\"] *, [role=\"main\"] .markdown [class*=\"overflow-x-scroll\"] *, [role=\"main\"] .markdown [class*=\"whitespace-pre\"], [role=\"main\"] .markdown [class*=\"whitespace-nowrap\"]";

    public const string InputArea =
        "textarea, [contenteditable=\"true\"]";

    public const string Focusable =
        "a:focus-visible, button:focus-visible, textarea:focus-visible, [contenteditable=\"true\"]:focus-visible, [tabindex]:focus-visible";
}

public static class ThemePresets
{
    public static IReadOnlyList<ReadabilityThemePreset> All { get; } =
    [
        new(ChatGptReadabilityProfile.ThemeStandard, "標準", "#000000", "#FFFFFF", "#FFFFFF", "#4B2F6F", "#160F24", "#F4ECFF", "#FACC15", "rgba(250, 204, 21, 0.35)", "light", IsStandard: true),
        new(ChatGptReadabilityProfile.ThemeSoftLight, "ソフトライト", "#F7F3EA", "#1F2933", "#FFFBF2", "#4B2F6F", "#160F24", "#F4ECFF", "#2563EB", "rgba(37, 99, 235, 0.22)", "light"),
        new(ChatGptReadabilityProfile.ThemeSepia, "セピア", "#F1E5CF", "#2C2117", "#F8EDD8", "#4B2F6F", "#160F24", "#F4ECFF", "#7C3AED", "rgba(124, 58, 237, 0.24)", "light"),
        new(ChatGptReadabilityProfile.ThemeGray, "グレー", "#E8EAED", "#202124", "#F5F6F7", "#4B2F6F", "#160F24", "#F4ECFF", "#1D4ED8", "rgba(29, 78, 216, 0.24)", "light"),
        new(ChatGptReadabilityProfile.ThemeDark, "ダーク", "#111827", "#E5E7EB", "#1F2937", "#4B2F6F", "#160F24", "#F4ECFF", "#FACC15", "rgba(250, 204, 21, 0.25)", "dark"),
        new(ChatGptReadabilityProfile.ThemeBlack, "ブラック", "#000000", "#F5F5F5", "#050505", "#4B2F6F", "#160F24", "#F4ECFF", "#FACC15", "rgba(250, 204, 21, 0.30)", "dark"),
        new(ChatGptReadabilityProfile.ThemeHighContrast, "高コントラスト", "#000000", "#FFFFFF", "#000000", "#4B2F6F", "#160F24", "#F4ECFF", "#FFFF00", "rgba(255, 255, 0, 0.45)", "dark")
    ];

    public static ReadabilityThemePreset Get(string theme)
    {
        return All.FirstOrDefault(x => x.Id.Equals(theme, StringComparison.Ordinal))
            ?? All[0];
    }
}

public sealed record ReadabilityThemePreset(
    string Id,
    string Label,
    string Background,
    string Text,
    string Surface,
    string Border,
    string CodeBackground,
    string CodeText,
    string Focus,
    string FocusShadow,
    string ColorScheme,
    bool IsStandard = false);

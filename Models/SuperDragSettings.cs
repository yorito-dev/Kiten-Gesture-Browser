namespace TabBrowser.Models;

public sealed class SuperDragSettings
{
    public bool IsEnabled { get; set; }
    public List<string> ExcludedHosts { get; set; } = [];
    public Dictionary<string, SuperDragSearchProvider> CustomSearchProviders { get; set; } = [];
    public SuperDragDirections Text { get; set; } = new()
    {
        Up = "SearchForeground",
        Right = "SearchBackground",
        Down = "Copy",
        Left = "Translate"
    };
    public SuperDragDirections Link { get; set; } = new();
    public SuperDragDirections Image { get; set; } = new();
}

public sealed class SuperDragDirections
{
    public string Up { get; set; } = "Foreground";
    public string Right { get; set; } = "Background";
    public string Down { get; set; } = "Copy";
    public string Left { get; set; } = "NewWindow";
    public string UpRight { get; set; } = "None";
    public string UpLeft { get; set; } = "None";
    public string RightUp { get; set; } = "None";
    public string RightDown { get; set; } = "None";
    public string DownRight { get; set; } = "None";
    public string DownLeft { get; set; } = "None";
    public string LeftUp { get; set; } = "None";
    public string LeftDown { get; set; } = "None";
}

public sealed record SuperDragActionOption(string Value, string Label);
public sealed record SuperDragSearchPreset(string Key, string Name, string SearchUrlTemplate);

public sealed class SuperDragSearchProvider
{
    public string Name { get; set; } = "";
    public string SearchUrlTemplate { get; set; } = "";
}

public static class SuperDragSettingsProfile
{
    public static IReadOnlyList<SuperDragActionOption> Routes { get; } = Array.AsReadOnly<SuperDragActionOption>(
    [new("up", "↑ 上"), new("right", "→ 右"), new("down", "↓ 下"), new("left", "← 左"),
        new("upRight", "↑→"), new("upLeft", "↑←"), new("rightUp", "→↑"), new("rightDown", "→↓"),
        new("downRight", "↓→"), new("downLeft", "↓←"), new("leftUp", "←↑"), new("leftDown", "←↓")]);

    public static IReadOnlyList<SuperDragSearchPreset> SearchPresets { get; } = Array.AsReadOnly<SuperDragSearchPreset>(
    [
        new("SearchGoogle", "Google", "https://www.google.com/search?q={0}"),
        new("SearchBing", "Bing", "https://www.bing.com/search?q={0}"),
        new("SearchYouTube", "YouTube", "https://www.youtube.com/results?search_query={0}"),
        new("SearchX", "X", "https://x.com/search?q={0}&src=typed_query"),
        new("SearchWikipedia", "Wikipedia", "https://ja.wikipedia.org/w/index.php?search={0}"),
        new("SearchBrave", "Brave Search", "https://search.brave.com/search?q={0}"),
        new("SearchGoogleMaps", "Google マップ", "https://www.google.com/maps/search/?api=1&query={0}"),
        new("SearchGitHub", "GitHub", "https://github.com/search?q={0}"),
        new("SearchGoogleScholar", "Google Scholar", "https://scholar.google.com/scholar?hl=ja&q={0}"),
        // 既存のWeb URL連携を使う。パラメーターの互換性は提供元に依存する。
        new("SearchChatGPT", "ChatGPT", "https://chatgpt.com/?q={0}&hints=search")
    ]);

    // 保存済みBing割り当ての互換性を保ち、新規追加の候補からだけ外す。
    public static IReadOnlyList<SuperDragSearchPreset> AddableSearchPresets { get; }
        = SearchPresets.Where(preset => preset.Key != "SearchBing").ToList().AsReadOnly();

    public static SuperDragSearchPreset? GetSearchPreset(string action)
    {
        const string foreground = "Foreground";
        var key = action.EndsWith(foreground, StringComparison.Ordinal) ? action[..^foreground.Length] : action;
        return SearchPresets.FirstOrDefault(preset => preset.Key == key);
    }

    public static IReadOnlyList<SuperDragActionOption> GetSelectableActions(string kind,
        IReadOnlyDictionary<string, SuperDragSearchProvider>? providers = null)
        => GetActions(kind, providers).Where(option => GetSearchPreset(option.Value) is null).ToArray();

    public static IReadOnlyList<SuperDragActionOption> GetActions(string kind,
        IReadOnlyDictionary<string, SuperDragSearchProvider>? providers = null)
    {
        IReadOnlyList<SuperDragActionOption> builtIn = kind == "text"
        ? [new("None", "何もしない"), new("SearchForeground", "検索（前面タブ）"),
            new("SearchBackground", "検索（裏タブ）"), new("Copy", "テキストをコピー"),
            new("Translate", "翻訳（裏タブ）"), new("SearchYouTube", "YouTube検索（裏タブ）"),
            new("SearchYouTubeForeground", "YouTube検索（前面タブ）"),
            new("SearchX", "X検索（裏タブ）"), new("SearchXForeground", "X検索（前面タブ）"),
            new("SearchChatGPT", "ChatGPT検索（裏タブ）"), new("SearchChatGPTForeground", "ChatGPT検索（前面タブ）"),
            new("SearchGoogle", "Google検索（裏タブ）"), new("SearchGoogleForeground", "Google検索（前面タブ）"),
            new("SearchBing", "Bing検索（裏タブ）"), new("SearchBingForeground", "Bing検索（前面タブ）"),
            new("SearchWikipedia", "Wikipedia検索（裏タブ）"), new("SearchWikipediaForeground", "Wikipedia検索（前面タブ）"),
            new("OpenUrlForeground", "選択したURLを開く（前面タブ）"), new("OpenUrlBackground", "選択したURLを開く（裏タブ）"),
            new("ChooseSearchForeground", "検索先を選ぶ（前面タブ）"), new("ChooseSearchBackground", "検索先を選ぶ（裏タブ）")]
        : kind == "link"
        ? [.. GetActions("image"), new("SplitRight", "右側に分割表示で開く"),
            new("SplitBottom", "下側に分割表示で開く"), new("CopyMarkdown", "リンクをMarkdown形式でコピー")]
        : [new("None", "何もしない"), new("Foreground", "前面タブで開く"),
            new("Background", "裏タブで開く"), new("Copy", "URLをコピー"), new("NewWindow", "新しいウィンドウで開く")];
        if (kind != "text" || providers is null) return builtIn;
        // 編集途中のURLが未完成でも、既存の選択は失わない。実行・保存時は別途検証する。
        return [.. builtIn, .. providers.Where(pair => Guid.TryParseExact(pair.Key, "N", out _) && pair.Value is not null)
            .SelectMany(pair => new[] { "Foreground", "Background" }.Select(disposition =>
                new SuperDragActionOption(CustomSearchAction(pair.Key, disposition),
                    $"追加検索: {(string.IsNullOrWhiteSpace(pair.Value.Name) ? "名前未入力" : pair.Value.Name.Trim())}（{(disposition == "Foreground" ? "前面タブ" : "裏タブ")}）")))];
    }

    public static string CustomSearchAction(string id, string disposition) => $"SearchCustom:{id}:{disposition}";

    public static bool TryGetCustomSearch(string action, IReadOnlyDictionary<string, SuperDragSearchProvider>? providers,
        out SuperDragSearchProvider? provider)
    {
        provider = null;
        var parts = action.Split(':');
        return parts.Length == 3 && parts[0] == "SearchCustom" && Guid.TryParseExact(parts[1], "N", out _)
            && parts[2] is "Foreground" or "Background" && providers is not null
            && providers.TryGetValue(parts[1], out provider) && provider is not null
            && !string.IsNullOrWhiteSpace(provider.Name)
            && SearchAliasSettingsProfile.IsValidSearchUrlTemplate(provider.SearchUrlTemplate);
    }

    // URLテンプレートはホスト側だけで保持し、ページスクリプトには渡さない。
    public static object CreatePageSettings(SuperDragSettings settings) => new
    {
        settings.IsEnabled,
        settings.ExcludedHosts,
        settings.Text,
        settings.Link,
        settings.Image
    };

    public static SuperDragSettings Normalize(SuperDragSettings? value)
    {
        var defaults = new SuperDragSettings();
        var providers = (value?.CustomSearchProviders ?? [])
            .Where(pair => Guid.TryParseExact(pair.Key, "N", out _) && pair.Value is not null
                && !string.IsNullOrWhiteSpace(pair.Value.Name)
                && SearchAliasSettingsProfile.IsValidSearchUrlTemplate(pair.Value.SearchUrlTemplate))
            .ToDictionary(pair => pair.Key, pair => new SuperDragSearchProvider
            {
                Name = pair.Value.Name.Trim(),
                SearchUrlTemplate = pair.Value.SearchUrlTemplate.Trim()
            });
        return new SuperDragSettings
        {
            IsEnabled = value?.IsEnabled == true,
            ExcludedHosts = (value?.ExcludedHosts ?? []).Select(NormalizeHost)
                .Where(static host => host is not null)
                .Select(static host => host!).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList(),
            CustomSearchProviders = providers,
            Text = NormalizeDirections(value?.Text, defaults.Text, "text", providers),
            Link = NormalizeDirections(value?.Link, defaults.Link, "link"),
            Image = NormalizeDirections(value?.Image, defaults.Image, "image")
        };
    }

    private static string? NormalizeHost(string? value)
    {
        var host = value?.Trim().TrimEnd('.');
        return !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) != UriHostNameType.Unknown
            && Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri)
            ? uri.IdnHost.ToLowerInvariant() : null;
    }

    private static SuperDragDirections NormalizeDirections(SuperDragDirections? value, SuperDragDirections defaults, string kind,
        IReadOnlyDictionary<string, SuperDragSearchProvider>? providers = null)
    {
        string Read(string? action, string fallback) => GetActions(kind, providers).Any(option => option.Value == action)
            ? action! : action?.StartsWith("SearchCustom:", StringComparison.Ordinal) == true ? "None" : fallback;
        return new()
        {
            Up = Read(value?.Up, defaults.Up),
            Right = Read(value?.Right, defaults.Right),
            Down = Read(value?.Down, defaults.Down),
            Left = Read(value?.Left, defaults.Left),
            UpRight = Read(value?.UpRight, "None"),
            UpLeft = Read(value?.UpLeft, "None"),
            RightUp = Read(value?.RightUp, "None"),
            RightDown = Read(value?.RightDown, "None"),
            DownRight = Read(value?.DownRight, "None"),
            DownLeft = Read(value?.DownLeft, "None"),
            LeftUp = Read(value?.LeftUp, "None"),
            LeftDown = Read(value?.LeftDown, "None")
        };
    }

    public static bool IsEnabledForPage(SuperDragSettings settings, string? url) => settings.IsEnabled
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
        && !settings.ExcludedHosts.Any(host => uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    public static string? GetAction(SuperDragSettings settings, string kind, string direction)
    {
        var mappings = kind switch { "text" => settings.Text, "link" => settings.Link, "image" => settings.Image, _ => null };
        return direction switch
        {
            "up" => mappings?.Up,
            "right" => mappings?.Right,
            "down" => mappings?.Down,
            "left" => mappings?.Left,
            "upRight" => mappings?.UpRight,
            "upLeft" => mappings?.UpLeft,
            "rightUp" => mappings?.RightUp,
            "rightDown" => mappings?.RightDown,
            "downRight" => mappings?.DownRight,
            "downLeft" => mappings?.DownLeft,
            "leftUp" => mappings?.LeftUp,
            "leftDown" => mappings?.LeftDown,
            _ => null
        };
    }
}

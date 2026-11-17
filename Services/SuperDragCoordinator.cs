using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record SuperDragOperation(string Kind, string Action, string Value, string? LinkText = null);
internal sealed record SuperDragNavigation(string Url, string Disposition, string? Title = null);
internal sealed record SuperDragSearchChoice(string Label, string Action, SuperDragNavigation Navigation);
internal sealed record SuperDragSearchSelection(Guid Id, Guid TabId, object View, string PageUrl,
    SuperDragOperation Operation, IReadOnlyList<SuperDragSearchChoice> Choices);

// ページ内容はドラッグ完了時の操作にだけ使い、保存・診断へ渡さない。
internal sealed class SuperDragCoordinator
{
    private sealed record Pending(object View, string RequestId, string PageUrl);
    private readonly Dictionary<Guid, Pending> _pending = [];
    private SuperDragSearchSelection? _selection;
    internal event Action? SearchSelectionInvalidated;

    public void Cancel(Guid tabId)
    {
        _pending.Remove(tabId);
        if (_selection?.TabId == tabId) { _selection = null; SearchSelectionInvalidated?.Invoke(); }
    }
    public void CancelAll()
    {
        _pending.Clear();
        if (_selection is not null) { _selection = null; SearchSelectionInvalidated?.Invoke(); }
    }

    internal void CloseSearchSelection(Guid id)
    {
        if (_selection?.Id == id) _selection = null;
    }

    internal SuperDragSearchSelection BeginSearchSelection(Guid tabId, object view, string url, SuperDragOperation operation,
        SuperDragSettings settings, Func<string, string> defaultSearch, int maximumSearchLength)
    {
        CancelAll();
        var foreground = operation.Action == "ChooseSearchForeground";
        var actions = new List<(string Label, string Action)> { ("既定の検索先", foreground ? "SearchForeground" : "SearchBackground") };
        actions.AddRange(settings.CustomSearchProviders.Select(pair => (pair.Value.Name,
            SuperDragSettingsProfile.CustomSearchAction(pair.Key, foreground ? "Foreground" : "Background"))));
        var choices = actions.Select(item => (item.Label, item.Action, Navigation: CreateNavigation(
                new("text", item.Action, operation.Value), defaultSearch, maximumSearchLength, settings.CustomSearchProviders)))
            .Where(item => item.Navigation is not null)
            .Select(item => new SuperDragSearchChoice(item.Label, item.Action, item.Navigation!)).ToArray();
        return _selection = new(Guid.NewGuid(), tabId, view, url, operation, choices);
    }

    internal SuperDragNavigation? TakeSearchSelection(Guid id, Guid tabId, object view, string url, bool isCurrentDocument,
        SuperDragSettings settings, Func<string, string> defaultSearch, int maximumSearchLength, string action)
    {
        var selection = _selection;
        if (selection?.Id != id) return null;
        _selection = null;
        if (!isCurrentDocument || selection.TabId != tabId || !ReferenceEquals(selection.View, view) || selection.PageUrl != url
            || !SuperDragSettingsProfile.IsEnabledForPage(settings, url)) return null;
        var choice = selection.Choices.FirstOrDefault(item => item.Action == action);
        if (choice is null) return null;
        var current = CreateNavigation(new("text", action, selection.Operation.Value), defaultSearch, maximumSearchLength,
            settings.CustomSearchProviders);
        return current == choice.Navigation ? current : null;
    }

    public SuperDragOperation? Receive(Guid tabId, object view, string currentUrl, string messageSource,
        JsonElement message, SuperDragSettings settings)
    {
        static string? Read(JsonElement element, string name) => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var phase = Read(message, "phase");
        var requestId = Read(message, "requestId");
        if (phase == "cancel") { Cancel(tabId); return null; }
        if (!SuperDragSettingsProfile.IsEnabledForPage(settings, currentUrl)
            || !string.Equals(currentUrl, messageSource, StringComparison.Ordinal)
            || !string.Equals(currentUrl, Read(message, "pageUrl"), StringComparison.Ordinal)
            || string.IsNullOrEmpty(requestId) || requestId.Length > 128)
        {
            Cancel(tabId);
            return null;
        }
        if (phase == "start")
        {
            Cancel(tabId);
            _pending[tabId] = new(view, requestId, currentUrl);
            return null;
        }
        if (phase != "drop" || !_pending.Remove(tabId, out var pending)
            || !ReferenceEquals(view, pending.View) || requestId != pending.RequestId || currentUrl != pending.PageUrl)
            return null;
        var kind = Read(message, "kind") ?? "";
        var action = SuperDragSettingsProfile.GetAction(settings, kind, Read(message, "direction") ?? "");
        var value = Read(message, "value");
        if (action is null or "None" || !SuperDragSettingsProfile.GetActions(kind, settings.CustomSearchProviders).Any(option => option.Value == action)
            || string.IsNullOrWhiteSpace(value) || value.Length > 8192)
            return null;
        if (kind != "text" && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            return null;
        var linkText = kind == "link" && action == "CopyMarkdown" ? Read(message, "linkText") : null;
        if (linkText?.Length > 160) return null;
        return new(kind, action, value, string.IsNullOrWhiteSpace(linkText) ? null : linkText);
    }

    internal static SuperDragNavigation? CreateNavigation(
        SuperDragOperation operation, Func<string, string> defaultSearch, int maximumSearchLength,
        IReadOnlyDictionary<string, SuperDragSearchProvider>? providers = null)
    {
        if (operation.Kind is not ("text" or "link" or "image")
            || !SuperDragSettingsProfile.GetActions(operation.Kind, providers).Any(option => option.Value == operation.Action)) return null;
        var action = operation.Action;
        var url = operation.Value.Trim();
        string? title = null;
        if (operation.Kind == "text")
        {
            if (action is "OpenUrlForeground" or "OpenUrlBackground")
            {
                if (url.Any(char.IsWhiteSpace)) return null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                var query = url[..Math.Min(maximumSearchLength, url.Length)];
                if (action.StartsWith("SearchCustom:", StringComparison.Ordinal))
                {
                    if (!SuperDragSettingsProfile.TryGetCustomSearch(action, providers, out var provider)) return null;
                    url = AddressService.FormatSearchUrl(provider!.SearchUrlTemplate, query);
                    title = provider.Name.Trim() + " 検索";
                }
                else if (action is "SearchForeground" or "SearchBackground") url = defaultSearch(query);
                else
                {
                    var preset = SuperDragSettingsProfile.GetSearchPreset(action);
                    if (preset is null) return null;
                    url = AddressService.FormatSearchUrl(preset.SearchUrlTemplate, query);
                    title = preset.Name + " 検索";
                }
            }
        }
        else if (action is not ("Foreground" or "Background" or "NewWindow" or "SplitRight" or "SplitBottom")) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var disposition = action is "NewWindow" or "SplitRight" or "SplitBottom" ? action
            : action.EndsWith("Foreground", StringComparison.Ordinal) ? "Foreground" : "Background";
        return new(url, disposition, title);
    }
}

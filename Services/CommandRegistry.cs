using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class CommandRegistry
{
    private static readonly IReadOnlySet<string> GestureExcludedActionIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ContextCommandProvider.OpenLinkInNewTabActionId,
            ContextCommandProvider.OpenLinkInSplitActionId,
            ContextCommandProvider.CopyLinkUrlActionId,
            ClipboardHistoryCommandProvider.ClearHistoryActionId,
            MiniPlayerCommandProvider.SeekRelativeActionId,
            MiniPlayerCommandProvider.SetSpeedActionId,
            MiniPlayerCommandProvider.ExecuteAbRepeatActionId
        };

    private readonly Dictionary<string, CommandAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CommandAction> Actions => _actions.Values;

    public void Register(CommandAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.Id))
        {
            throw new ArgumentException("コマンドIDは空にできません。", nameof(action));
        }

        if (!_actions.TryAdd(action.Id, action))
        {
            throw new InvalidOperationException($"コマンドIDが重複しています: {action.Id}");
        }
    }

    public bool TryGetAction(string actionId, out CommandAction action)
    {
        return _actions.TryGetValue(actionId, out action!);
    }

    public IReadOnlyList<CommandPaletteItem> CreateCommandItems(
        CommandInvocationContext? context = null,
        bool includeUnavailable = false,
        IEnumerable<GestureMappingSettings>? gestureMappings = null)
    {
        var invocationContext = context ?? CommandInvocationContext.Global;
        var gestureScope = GestureCommandScope.FromSurface(invocationContext.Surface);
        var mappings = GestureSettingsCatalog.ResolveMappingsForScope(
            gestureMappings,
            gestureScope);
        return _actions.Values
            .Where(static action => action.ShowInPalette)
            .Select(action => new
            {
                Action = action,
                Availability = action.GetAvailability(invocationContext)
            })
            .Where(result =>
                result.Availability.State == CommandAvailabilityState.Available
                || includeUnavailable)
            .OrderBy(static result => result.Action.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(result =>
            {
                var action = result.Action;
                var gestureCommandIds = GetGestureCommandIds(action);
                var gestureRoutes = mappings
                    .Where(mapping => gestureCommandIds.Contains(
                        mapping.Command,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(static mapping => mapping.Route)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var shortcutText = string.Join(" / ", action.Shortcuts);
                var gestureText = gestureRoutes.Count > 0
                    ? string.Join(" / ", gestureRoutes)
                    : gestureCommandIds.Count > 0 ? "未割り当て" : string.Empty;
                var isAvailable =
                    result.Availability.State == CommandAvailabilityState.Available;
                return new CommandPaletteItem
                {
                    Id = "command:" + action.Id,
                    Type = CommandPaletteItemType.Command,
                    Title = action.Title,
                    Subtitle = isAvailable
                        ? string.IsNullOrWhiteSpace(action.Description)
                            ? action.Category
                            : $"{action.Category} / {action.Description}"
                        : result.Availability.Reason,
                    ActionId = action.Id,
                    Category = action.Category,
                    ShortcutText = shortcutText,
                    GestureText = gestureText,
                    IsAvailable = isAvailable,
                    UnavailableReason = result.Availability.Reason,
                    CanConfigureGesture = gestureCommandIds.Count > 0,
                    GestureCommandId = gestureCommandIds.FirstOrDefault() ?? string.Empty,
                    Keywords = action.Keywords
                        .Concat(action.Shortcuts.SelectMany(CreateShortcutSearchTerms))
                        .Concat(gestureRoutes)
                        .Concat(gestureCommandIds)
                        .Concat(
                            gestureCommandIds.Count > 0 && gestureRoutes.Count == 0
                                ? new[] { "未割り当て", "unassigned" }
                                : Array.Empty<string>())
                        .Append(action.Category)
                        .ToList()
                };
            })
            .ToList();
    }

    public IReadOnlyList<GestureCommandOption> CreateGestureCommandOptions()
    {
        return _actions.Values
            .Where(IsGestureAssignable)
            .SelectMany(action =>
            {
                var scopes = GetGestureScopes(action);
                if (scopes.Count == 0)
                {
                    return [];
                }

                var commandIds = action.GestureCommandIds.Count > 0
                    ? action.GestureCommandIds
                    : [action.Id];
                return commandIds.Select(commandId => new GestureCommandOption(
                    commandId,
                    action.Title,
                    action.Category,
                    action.GestureCommandIds.Count > 0
                        ? [GestureCommandScope.Any]
                        : scopes,
                    action.Keywords));
            })
            .GroupBy(static option => option.Command, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static option => option.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public CommandAvailability GetAvailability(
        string actionId,
        CommandInvocationContext? context = null)
    {
        return TryGetAction(actionId, out var action)
            ? action.GetAvailability(context ?? CommandInvocationContext.Global)
            : CommandAvailability.Hidden("コマンドが登録されていません");
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        string actionId,
        CommandActionContext context)
    {
        if (!TryGetAction(actionId, out var action))
        {
            return CommandExecutionResult.NotFound(actionId);
        }

        var availability = action.GetAvailability(
            context.InvocationContext ?? CommandInvocationContext.Global);
        if (availability.State != CommandAvailabilityState.Available)
        {
            return CommandExecutionResult.Unavailable(availability.Reason);
        }

        try
        {
            await action.RunAsync(context);
            return CommandExecutionResult.Succeeded;
        }
        catch (OperationCanceledException)
        {
            return CommandExecutionResult.Cancelled;
        }
        catch (Exception ex)
        {
            return CommandExecutionResult.Failed(ex);
        }
    }

    private static IEnumerable<string> CreateShortcutSearchTerms(string shortcut)
    {
        yield return shortcut;
        yield return shortcut.Replace("+", " ", StringComparison.Ordinal);
        yield return shortcut.Replace("+", string.Empty, StringComparison.Ordinal);
    }

    private static bool IsGestureAssignable(CommandAction action)
    {
        return action.ShowInPalette
            && !GestureExcludedActionIds.Contains(action.Id);
    }

    private static IReadOnlyList<string> GetGestureCommandIds(CommandAction action)
    {
        if (!IsGestureAssignable(action))
        {
            return [];
        }

        return action.GestureCommandIds.Count > 0
            ? action.GestureCommandIds
            : [action.Id];
    }

    private static IReadOnlyList<string> GetGestureScopes(CommandAction action)
    {
        var contexts = new[]
        {
            new CommandInvocationContext(
                CommandSurface.Global,
                IsSplitView: true,
                HasMiniPlayers: true,
                TargetId: Guid.Empty),
            new CommandInvocationContext(
                CommandSurface.Web,
                IsSplitView: true,
                HasSelection: true,
                HasLink: true,
                HasMiniPlayers: true,
                SelectionCount: 2,
                TargetId: Guid.Empty,
                SelectedText: "text",
                LinkUrl: "https://example.com/",
                CanNavigateBack: true,
                CanNavigateForward: true),
            new CommandInvocationContext(
                CommandSurface.FolderTab,
                IsSplitView: true,
                HasMiniPlayers: true,
                SelectionCount: 2,
                TargetId: Guid.Empty,
                CanNavigateBack: true,
                CanNavigateForward: true),
            new CommandInvocationContext(
                CommandSurface.Workspace,
                IsSplitView: true,
                HasMiniPlayers: true,
                SelectionCount: 2,
                TargetId: Guid.Empty),
            new CommandInvocationContext(
                CommandSurface.MiniPlayer,
                HasMiniPlayers: true,
                TargetId: Guid.Empty)
        };
        var supported = contexts
            .Where(context =>
                action.GetAvailability(context).State == CommandAvailabilityState.Available)
            .Select(context => GestureCommandScope.FromSurface(context.Surface))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return supported.Count == contexts.Length
            ? [GestureCommandScope.Any]
            : supported;
    }
}

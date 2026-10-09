using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

/// <summary>Workspaceの論理削除済み項目を、明示操作で復元・完全削除する画面。</summary>
public sealed class WorkspaceTrashWindow : Window
{
    private readonly WorkspaceService _service;
    private readonly Grid _root = new() { Margin = new Thickness(12) };
    private readonly ListBox _items = new() { Background = Brush("#11141A"), Foreground = Brush("#F3F4F6"), BorderBrush = Brush("#343842") };
    private readonly ComboBox _destination = new() { MinWidth = 240, Background = Brush("#23262D"), Foreground = Brush("#F3F4F6"), BorderBrush = Brush("#343842") };
    private readonly TextBlock _status = new() { Foreground = Brush("#FBBF24"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel _confirmation = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmationText = new() { Foreground = Brush("#F87171"), TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<WorkspaceItem> _deleted = [];
    private IReadOnlyList<Destination> _destinations = [];
    private IReadOnlyList<Guid>? _pendingDelete;
    private IReadOnlyDictionary<Guid, DateTimeOffset>? _pendingDeletionTimes;
    private bool _busy;

    public WorkspaceTrashWindow(WorkspaceService service)
    {
        _service = service;
        Title = "最近削除したWorkspace項目";
        Width = 760; Height = 540; MinWidth = 540; MinHeight = 380;
        Background = Brush("#101114"); Foreground = Brush("#F3F4F6");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _root;
        for (var i = 0; i < 5; i++) _root.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var hint = new TextBlock { Text = "削除したカードと配下を保持しています。期限による自動完全削除はありません。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        _root.Children.Add(hint);
        Grid.SetRow(_items, 1); _root.Children.Add(_items);
        _items.DisplayMemberPath = nameof(TrashRow.Label);
        _items.SelectionChanged += (_, _) => SelectOriginalDestination();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(actions, 2); _root.Children.Add(actions);
        actions.Children.Add(new TextBlock { Text = "復元先: ", VerticalAlignment = VerticalAlignment.Center });
        _destination.DisplayMemberPath = nameof(Destination.Label);
        _destination.Style = TryFindResource("DarkDialogComboBoxStyle") as Style;
        actions.Children.Add(_destination);
        actions.Children.Add(Button("復元", async () => await RestoreSelectedAsync()));
        actions.Children.Add(Button("更新", async () => await ReloadAsync()));
        actions.Children.Add(Button("完全削除…", () => { PreparePermanentDelete(); return Task.CompletedTask; }));
        Grid.SetRow(_status, 3); _root.Children.Add(_status);
        Grid.SetRow(_confirmation, 4); _root.Children.Add(_confirmation);
        _confirmation.Children.Add(_confirmationText);
        var confirmationButtons = new WrapPanel();
        confirmationButtons.Children.Add(Button("確認した項目を完全削除", ConfirmPermanentDeleteAsync));
        confirmationButtons.Children.Add(Button("キャンセル", () => { CancelPermanentDelete(); return Task.CompletedTask; }));
        _confirmation.Children.Add(confirmationButtons);
        Loaded += async (_, _) => await ReloadAsync();
    }

    internal string StatusText => _status.Text;

    internal async Task ReloadAsync()
    {
        await RunAsync(async () =>
        {
            var result = await Task.Run(async () =>
            {
                var deleted = await _service.GetDeletedItemsAsync();
                var destinations = new List<Destination>();
                foreach (var workspace in await _service.GetWorkspacesAsync())
                {
                    destinations.Add(new Destination(workspace.Id, null, $"{workspace.Name} / 直下"));
                    foreach (var folder in (await _service.GetWorkspaceItemsAsync(workspace.Id)).OfType<FolderItem>())
                        destinations.Add(new Destination(workspace.Id, folder.Id, $"{workspace.Name} / {folder.Title} ({folder.Id.ToString("N")[..6]})"));
                }
                return (deleted, destinations);
            });
            _deleted = result.deleted;
            _destinations = result.destinations;
            _destination.ItemsSource = _destinations;
            _items.ItemsSource = _deleted.OrderByDescending(x => x.DeletedAt).Select(x => new TrashRow(x)).ToList();
            _status.Text = _deleted.Count == 0 ? "最近削除した項目はありません" : $"{_deleted.Count} 件。復元先を確認して「復元」を押してください";
            CancelPermanentDelete();
        });
    }

    internal void SelectItem(Guid id) => _items.SelectedItem = _items.Items.Cast<TrashRow>().FirstOrDefault(x => x.Item.Id == id);

    internal void SelectDestination(Guid workspaceId, Guid? folderId)
        => _destination.SelectedItem = _destinations.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.FolderId == folderId);

    private void SelectOriginalDestination()
    {
        CancelPermanentDelete();
        if (_items.SelectedItem is not TrashRow row) return;
        var original = _destinations.FirstOrDefault(x => x.WorkspaceId == row.Item.WorkspaceId && x.FolderId == row.Item.ParentFolderId);
        _destination.SelectedItem = original;
        if (original is null) _status.Text = "元の親またはWorkspaceがありません。安全な復元先を選択してください";
    }

    internal async Task RestoreSelectedAsync()
    {
        if (_items.SelectedItem is not TrashRow row || _destination.SelectedItem is not Destination destination)
        {
            _status.Text = "項目と復元先を選択してください";
            return;
        }
        var restored = false;
        await RunAsync(async () =>
        {
            var items = await _service.RestoreItemAsync(row.Item.Id, destination.WorkspaceId, destination.FolderId);
            _status.Text = items.Count == 0 ? "対象は既に復元または完全削除されています。更新してください" : $"{items.Count} 件を復元しました";
            restored = items.Count > 0;
            if (restored)
            {
                var ids = items.Select(x => x.Id).ToHashSet();
                _deleted = _deleted.Where(x => !ids.Contains(x.Id)).ToList();
                _items.ItemsSource = _deleted.Select(x => new TrashRow(x)).ToList();
            }
        });
    }

    internal void PreparePermanentDelete()
    {
        if (_busy || _items.SelectedItem is not TrashRow row) return;
        _pendingDelete = GetPermanentDeleteTargets(_deleted, row.Item.Id);
        _pendingDeletionTimes = _deleted.Where(x => _pendingDelete.Contains(x.Id)).ToDictionary(x => x.Id, x => x.DeletedAt!.Value);
        _confirmationText.Text = $"「{row.Item.Title}」と配下、合計 {_pendingDelete.Count} 件を完全削除します。元に戻せません。Shellカードの参照先ファイルは削除しません。";
        _confirmation.Visibility = Visibility.Visible;
    }

    internal static IReadOnlyList<Guid> GetPermanentDeleteTargets(IReadOnlyList<WorkspaceItem> deleted, Guid rootId)
    {
        var ids = new HashSet<Guid> { rootId };
        bool added;
        do
        {
            added = false;
            foreach (var item in deleted)
                if (item.ParentFolderId is { } parent && ids.Contains(parent)) added |= ids.Add(item.Id);
        } while (added);
        return ids.ToArray();
    }

    internal async Task ConfirmPermanentDeleteAsync()
    {
        if (_pendingDelete is not { Count: > 0 } ids) return;
        await RunAsync(async () =>
        {
            await _service.PermanentlyDeleteItemsAsync(ids, expectedDeletionTimes: _pendingDeletionTimes);
            CancelPermanentDelete();
            _deleted = _deleted.Where(x => !ids.Contains(x.Id)).ToList();
            _items.ItemsSource = _deleted.Select(x => new TrashRow(x)).ToList();
            _status.Text = $"{ids.Count} 件を完全削除しました。この操作は元に戻せません";
        });
    }

    private void CancelPermanentDelete() { _pendingDelete = null; _pendingDeletionTimes = null; _confirmation.Visibility = Visibility.Collapsed; }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _root.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { _status.Text = "処理を完了できませんでした。更新して状態を確認してください: " + ex.Message; }
        finally { _busy = false; _root.IsEnabled = true; }
    }

    protected override void OnClosing(CancelEventArgs e) { if (_busy) e.Cancel = true; base.OnClosing(e); }
    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = Brush("#F3F4F6"),
            Background = Brush("#23262D"),
            BorderBrush = Brush("#343842")
        };
        button.Click += async (_, _) => await action();
        return button;
    }
    private sealed record Destination(Guid WorkspaceId, Guid? FolderId, string Label);
    private sealed record TrashRow(WorkspaceItem Item)
    {
        public string Label => $"{Item.Title}　[{Kind(Item.Type)}]　削除: {Item.DeletedAt:yyyy/MM/dd HH:mm:ss}";
        private static string Kind(WorkspaceItemType type) => type switch
        {
            WorkspaceItemType.Note => "メモ",
            WorkspaceItemType.Bookmark => "ブックマーク",
            WorkspaceItemType.SessionGroup => "タブグループ",
            WorkspaceItemType.Folder => "フォルダー",
            WorkspaceItemType.ShellItem => "ファイル参照",
            WorkspaceItemType.YouTubeMiniPlayerSet => "ミニプレイヤー構成",
            _ => type.ToString()
        };
    }
}

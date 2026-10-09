using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

public sealed class NamedSessionRestorePointsWindow : Window
{
    private readonly SessionService _service;
    private readonly Func<Task<SessionData>> _capture;
    private readonly Func<SessionData, Task<string>> _restore;
    private readonly Grid _root = new() { Margin = new Thickness(12) };
    private readonly TextBox _name = new() { MaxLength = 120, MinWidth = 230, Foreground = Brush("#F3F4F6"), Background = Brush("#11141A"), BorderBrush = Brush("#343842"), Padding = new Thickness(6) };
    private readonly ListBox _items = new() { Foreground = Brush("#F3F4F6"), Background = Brush("#11141A"), BorderBrush = Brush("#343842") };
    private readonly TextBlock _status = new() { Foreground = Brush("#FBBF24"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel _confirmation = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmationText = new() { Foreground = Brush("#F87171"), TextWrapping = TextWrapping.Wrap };
    private NamedSessionRestorePointRequest? _pendingSave;
    private NamedSessionRestorePointInfo? _pendingDelete;
    private bool _busy;

    public NamedSessionRestorePointsWindow(SessionService service, Func<Task<SessionData>> capture, Func<SessionData, Task<string>> restore)
    {
        _service = service; _capture = capture; _restore = restore;
        Title = "名前付きタブセッション復元点";
        Width = 840; Height = 530; MinWidth = 580; MinHeight = 360;
        Background = Brush("#101114"); Foreground = Brush("#F3F4F6");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _root;
        for (var i = 0; i < 6; i++) _root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        _root.Children.Add(new TextBlock
        {
            Text = "対象: 全通常Windowのタブセッション。Workspace・メモ・認証情報は戻しません。\n自動整理では削除しません。復元は現在のタブを残して、新しいWindowに開きます。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var saveControls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(saveControls, 1); _root.Children.Add(saveControls);
        saveControls.Children.Add(new TextBlock { Text = "名前: ", VerticalAlignment = VerticalAlignment.Center });
        saveControls.Children.Add(_name);
        _name.TextChanged += (_, _) => { if (!_busy) _pendingSave = null; };
        saveControls.Children.Add(Button("現在のタブを名前付きで保存", SaveAsync));
        Grid.SetRow(_items, 2); _root.Children.Add(_items);
        _items.DisplayMemberPath = nameof(RestorePointRow.Label);
        _items.SelectionChanged += (_, _) => CancelDelete();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(actions, 3); _root.Children.Add(actions);
        actions.Children.Add(Button("新しいWindowに復元", RestoreAsync));
        actions.Children.Add(Button("更新", RefreshAsync));
        actions.Children.Add(Button("削除…", () => { PrepareDelete(); return Task.CompletedTask; }));
        Grid.SetRow(_status, 4); _root.Children.Add(_status);
        Grid.SetRow(_confirmation, 5); _root.Children.Add(_confirmation);
        _confirmation.Children.Add(_confirmationText);
        var confirmationActions = new WrapPanel();
        confirmationActions.Children.Add(Button("この復元点1件を削除", ConfirmDeleteAsync));
        confirmationActions.Children.Add(Button("キャンセル", () => { CancelDelete(); return Task.CompletedTask; }));
        _confirmation.Children.Add(confirmationActions);
        Loaded += async (_, _) => await RefreshAsync();
    }

    internal string StatusText => _status.Text;
    internal Func<Task>? BeforeReloadAsync { get; init; }
    internal void SetName(string name) => _name.Text = name;
    internal void SelectItem(Guid id) => _items.SelectedItem = _items.Items.Cast<RestorePointRow>().FirstOrDefault(x => x.Info.Id == id);

    internal Task RefreshAsync() => RunAsync(async () =>
    {
        await ReloadItemsAsync();
        _status.Text = _items.Items.Count == 0 ? "名前付き復元点はありません" : "名前・日時・タブ数・対象範囲を確認して選択してください";
    });

    private async Task ReloadItemsAsync()
    {
        if (BeforeReloadAsync is not null) await BeforeReloadAsync();
        _items.ItemsSource = (await _service.GetNamedRestorePointsAsync()).Select(info => new RestorePointRow(info)).ToList();
        CancelDelete();
    }

    internal Task SaveAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _status.Text = "復元点の名前を入力してください"; return; }
        // 失敗後の再試行では同じ要求ID・snapshotを使用する。名前を編集した場合だけ新しい要求になる。
        _pendingSave ??= new NamedSessionRestorePointRequest(Guid.NewGuid(), _name.Text.Trim(), DateTimeOffset.Now, await _capture());
        var saved = await _service.SaveNamedRestorePointAsync(_pendingSave);
        _pendingSave = null;
        _status.Text = $"「{saved.Name}」を保存しました（{saved.TabCount}タブ）。自動整理では削除しません";
        try { await ReloadItemsAsync(); SelectItem(saved.Id); }
        catch { _status.Text += "。一覧の更新だけに失敗しました。保存済みなので再保存せず「更新」を押してください"; }
    });

    internal Task RestoreAsync() => RunAsync(async () =>
    {
        if (_items.SelectedItem is not RestorePointRow row) { _status.Text = "復元点を選択してください"; return; }
        if (!row.Info.CanRestore) { _status.Text = row.Info.Error; return; }
        var session = await _service.LoadNamedRestorePointAsync(row.Info.Id, expectedContentHash: row.Info.ContentHash);
        _status.Text = await _restore(session);
    });

    internal void PrepareDelete()
    {
        if (_busy || _items.SelectedItem is not RestorePointRow row) return;
        _pendingDelete = row.Info;
        _confirmationText.Text = $"名前付きタブセッション復元点「{row.Info.Name}」1件（{row.Info.TabCount}タブ）を削除します。元に戻せません。現在のタブは閉じません。";
        _confirmation.Visibility = Visibility.Visible;
    }

    internal Task ConfirmDeleteAsync() => RunAsync(async () =>
    {
        if (_pendingDelete is not { } point) return;
        await _service.DeleteNamedRestorePointAsync(point.Id, point.ContentHash);
        CancelDelete();
        _status.Text = "確認した復元点1件を削除しました。現在のタブは変更していません";
        try { await ReloadItemsAsync(); }
        catch { _status.Text += "。一覧の更新だけに失敗しました。「更新」で確認してください"; }
    });

    private void CancelDelete() { _pendingDelete = null; _confirmation.Visibility = Visibility.Collapsed; }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _root.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { _status.Text = "処理を完了できませんでした。既存の復元点を確認して再試行してください: " + ex.Message; }
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
    private sealed record RestorePointRow(NamedSessionRestorePointInfo Info)
    {
        public string Label => $"{Info.Name}　{Info.CreatedAt:yyyy/MM/dd HH:mm:ss}　{Info.TabCount}タブ / {Info.WindowCount}Window\n{Info.ScopeLabel}" + (Info.Error is null ? "" : "\n" + Info.Error);
    }
}

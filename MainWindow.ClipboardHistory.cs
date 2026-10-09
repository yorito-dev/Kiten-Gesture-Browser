using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow
{
    private void InitializeClipboardHistoryIntegration()
    {
        App.ClipboardHistory.ItemsChanged += ClipboardHistoryItems_CollectionChanged;
    }

    private void DisposeClipboardHistoryIntegration()
    {
        App.ClipboardHistory.ItemsChanged -= ClipboardHistoryItems_CollectionChanged;
        ModelessWindowService.CloseOwned<ClipboardHistoryWindow>(this);
    }

    private void ApplyClipboardHistorySettings()
    {
        var normalized = ClipboardHistorySettingsProfile.Normalize(_settings.ClipboardHistory);
        foreach (var window in GetOpenMainWindows())
        {
            window._settings.ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(normalized);
        }

        _settings.ClipboardHistory = normalized;
        App.ClipboardHistory.ApplySettings(normalized);
    }

    private async void ClipboardHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(ClipboardHistoryCommandProvider.OpenHistoryActionId);
    }

    private Task OpenClipboardHistoryFromCommandPaletteAsync()
    {
        ShowClipboardHistoryWindow();
        return Task.CompletedTask;
    }

    private void ShowClipboardHistoryWindow()
    {
        if (ModelessWindowService.TryGetOwned<ClipboardHistoryWindow>(this, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            WindowTopmostService.Apply(existing, Topmost);
            existing.Activate();
            return;
        }

        var window = new ClipboardHistoryWindow(App.ClipboardHistory)
        {
            Owner = this,
            OpenItemRequested = OpenClipboardHistoryItem,
            OpenSettingsRequested = () => _ = OpenSettingsDialogAsync()
        };
        try
        {
            ModelessWindowService.Show(window);
        }
        catch (InvalidOperationException)
        {
            SetStatus("クリップボード履歴を開けませんでした", autoClear: true);
        }
    }

    private void OpenClipboardHistoryItem(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardHistoryItemKind.Url)
        {
            OpenTab(item.Text, activate: true, title: item.Text);
            return;
        }

        if (item.Kind != ClipboardHistoryItemKind.Files || item.PrimaryPath is not { } path)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            OpenShellFolderInTab(path);
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("参照先が見つかりません。", path);
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private Task CopyClipboardHistoryItemFromCommandPaletteAsync(CommandActionContext context)
    {
        var result = App.ClipboardHistory.CopyToClipboard(context.SelectedItem?.Payload as ClipboardHistoryItem);
        SetStatus(result.Message, autoClear: true);
        return Task.CompletedTask;
    }

    private Task ClearClipboardHistoryFromCommandPaletteAsync()
    {
        App.ClipboardHistory.Clear();
        SetStatus("クリップボード履歴を消去しました", autoClear: true);
        return Task.CompletedTask;
    }

    private Task ToggleClipboardHistoryPauseFromCommandPaletteAsync()
    {
        if (!App.ClipboardHistory.IsEnabled || !App.ClipboardHistory.IsMonitoring)
        {
            SetStatus(
                App.ClipboardHistory.IsEnabled
                    ? "クリップボード履歴の監視を開始できていません"
                    : "クリップボード履歴は設定で無効になっています",
                autoClear: true);
            return Task.CompletedTask;
        }

        App.ClipboardHistory.SetPaused(!App.ClipboardHistory.IsPaused);
        SetStatus(
            App.ClipboardHistory.IsPaused
                ? "クリップボード履歴の監視を一時停止しました"
                : "クリップボード履歴の監視を再開しました",
            autoClear: true);
        return Task.CompletedTask;
    }

    private void ClipboardHistoryItems_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (IsCommandPaletteOpen)
        {
            QueueCommandPaletteRefresh();
        }
    }
}

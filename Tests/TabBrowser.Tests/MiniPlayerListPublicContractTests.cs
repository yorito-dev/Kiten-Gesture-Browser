using Xunit;

namespace TabBrowser.Tests;

public sealed class MiniPlayerListPublicContractTests
{
    [Fact]
    public void MiniPlayerListAndArrangementPopupShareEscapeWithPreviewCancellationFirst()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.YouTubeMiniPlayer.cs"));
        Assert.Contains("_youTubeMiniPlayerListWindow.KeyDown += YouTubeMiniPlayerArrangement_KeyDown", main);
        Assert.Contains("_youTubeMiniPlayerListWindow.KeyDown -= YouTubeMiniPlayerArrangement_KeyDown", code);
        var popup = xaml[xaml.IndexOf("<Popup x:Name=\"YouTubeMiniPlayerArrangePopup\"", StringComparison.Ordinal)..];
        Assert.Contains("PreviewKeyDown=\"YouTubeMiniPlayerArrangement_KeyDown\"", popup[..popup.IndexOf('>')]);
        var start = code.IndexOf("private void YouTubeMiniPlayerArrangement_KeyDown(", StringComparison.Ordinal);
        var handler = code[start..code.IndexOf("private void YouTubeMiniPlayerOpenLocalVideoButton_Click", start, StringComparison.Ordinal)];
        var cancel = handler.IndexOf("CancelFloatingMediaArrangementPreview(showStatus: true)", StringComparison.Ordinal);
        var areaDropdown = handler.IndexOf("YouTubeMiniPlayerArrangeAreaComboBox.IsDropDownOpen", StringComparison.Ordinal);
        var close = handler.IndexOf("YouTubeMiniPlayerArrangePopup.IsOpen = false;", StringComparison.Ordinal);
        Assert.Contains("if (e.Key != Key.Escape) return;", handler);
        Assert.True(cancel >= 0 && close > cancel);
        Assert.True(areaDropdown >= 0 && areaDropdown < cancel);
        Assert.Contains("YouTubeMiniPlayerArrangeAreaComboBox.IsDropDownOpen = false;", handler);
        Assert.Contains("e.Handled = true;\n            return;", handler.Replace("\r\n", "\n"));
        Assert.Contains("if (!YouTubeMiniPlayerArrangePopup.IsOpen) return;", handler);
        Assert.Contains("YouTubeMiniPlayerArrangeMenuButton.Focus();", handler);
        Assert.DoesNotContain(".Close(", handler);
    }

    [Fact]
    public void MiniPlayerWheelVolumeIsConnectedOnlyToThePopupSlider()
    {
        var root = FindRepositoryRoot();
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        var handlers = document.Descendants().Where(element => (string?)element.Attribute("PreviewMouseWheel") == "YouTubeMiniPlayerVolumeSlider_PreviewMouseWheel").ToArray();
        var slider = Assert.Single(handlers);
        Assert.Equal("Slider", slider.Name.LocalName);
        Assert.Equal("5", (string?)slider.Attribute("TickFrequency"));
        Assert.Equal("YouTubeMiniPlayerVolumeSlider_ValueChanged", (string?)slider.Attribute("ValueChanged"));
    }

    [Fact]
    public void MiniPlayerInformationBarNavigatesWithoutChangingSelection()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        Assert.Contains("MouseLeftButtonUp=\"YouTubeMiniPlayerInformationBar_MouseLeftButtonUp\"", xaml);
        var code = File.ReadAllText(Path.Combine(root, "Views", "YouTubeMiniPlayerListViewController.cs"));
        var start = code.IndexOf("public bool ScrollToActivePlayer()", StringComparison.Ordinal);
        var handler = code[start..code.IndexOf("public void YouTubeMiniPlayerListItem_MouseDoubleClick", start, StringComparison.Ordinal)];
        Assert.Contains("!string.IsNullOrEmpty(YouTubeMiniPlayerListWindow.OperationStatus)", handler);
        Assert.Contains("YouTubeMiniPlayerList.ScrollIntoView(item);", handler);
        Assert.DoesNotContain("Selected", handler);
        Assert.DoesNotContain("BringToFront", handler);
    }

    [Fact]
    public void MiniPlayerMoreMenuUsesExistingFrontAndReloadPaths()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        Assert.Contains("Content=\"前面に表示\" Click=\"YouTubeMiniPlayerItemBringToFrontButton_Click\"", xaml);
        Assert.Contains("Content=\"再読み込み\" Click=\"YouTubeMiniPlayerItemReloadMenuButton_Click\"", xaml);
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.YouTubeMiniPlayer.cs"));
        var start = code.IndexOf("private void YouTubeMiniPlayerItemBringToFrontButton_Click", StringComparison.Ordinal);
        var end = code.IndexOf("private async void YouTubeMiniPlayerItemSourceTabButton_Click", start, StringComparison.Ordinal);
        var handlers = code[start..end];
        Assert.Contains("((IYouTubeMiniPlayerListHost)this).RestoreAndBringToFront(item);", handlers);
        Assert.Contains("ExecuteMiniPlayerCommandAsync(MiniPlayerCommandProvider.ReloadActionId, item)", handlers);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(handlers, "YouTubeMiniPlayerItemMorePopup.IsOpen = false;").Count);
        Assert.Contains("YouTubeMiniPlayerListItem_MouseDoubleClick", File.ReadAllText(Path.Combine(root, "Views", "YouTubeMiniPlayerListViewController.cs")));
    }

    [Fact]
    public void MiniPlayerArrangementComboEscapeClosesOnlyTheDropDown()
    {
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MainWindow.YouTubeMiniPlayer.cs"));
        var start = code.IndexOf("if (YouTubeMiniPlayerArrangeDisplayComboBox.IsDropDownOpen)", StringComparison.Ordinal);
        var cancel = code.IndexOf("if (CancelFloatingMediaArrangementPreview(showStatus: true))", start, StringComparison.Ordinal);
        var guard = code[start..cancel];
        Assert.Contains("YouTubeMiniPlayerArrangeDisplayComboBox.IsDropDownOpen = false;", guard);
        Assert.Contains("e.Handled = true;", guard);
        Assert.Contains("return;", guard);
        Assert.DoesNotContain("YouTubeMiniPlayerArrangePopup.IsOpen = false", guard);
    }

    [Fact]
    public void MiniPlayerVolumePopupUsesTheExistingRowMuteCommand()
    {
        var root = FindRepositoryRoot();
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        System.Xml.Linq.XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var popup = xaml.Descendants(p + "Popup").Single(element => (string?)element.Attribute(x + "Name") == "YouTubeMiniPlayerVolumePopup");
        var button = popup.Descendants(p + "Button").Single(element => (string?)element.Attribute(x + "Name") == "YouTubeMiniPlayerVolumeMuteButton");
        Assert.Equal("{Binding MuteButtonText}", (string?)button.Attribute("Content"));
        Assert.Equal("{Binding MuteButtonText}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("YouTubeMiniPlayerItemMuteButton_Click", (string?)button.Attribute("Click"));
        Assert.Equal("{StaticResource YouTubeMiniPlayerListButtonStyle}", (string?)button.Attribute("Style"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.YouTubeMiniPlayer.cs"));
        var start = code.IndexOf("private async void YouTubeMiniPlayerItemMuteButton_Click(", StringComparison.Ordinal);
        var handler = code[start..code.IndexOf("private async void YouTubeMiniPlayerItemMuteButton_PreviewMouseRightButtonUp(", start, StringComparison.Ordinal)];
        Assert.Contains("MiniPlayerCommandProvider.ToggleMuteActionId", handler);
        var actions = File.ReadAllText(Path.Combine(root, "Views", "YouTubeMiniPlayerListActionsController.cs"));
        Assert.Contains("YouTubeMiniPlayerVolumePopup.DataContext = item;", actions);
        Assert.Contains("YouTubeMiniPlayerVolumePopup.DataContext = null;", actions);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("ソースの配置場所を確認してください。");
    }
}

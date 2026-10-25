using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class ScopedContextMenuToggleTests
{
    [Theory]
    [InlineData("HierarchyButton")]
    [InlineData("PlacesButton")]
    [InlineData("SortButton")]
    [InlineData("ArrangeMenuButton")]
    public void TargetButtonOpensClosesAndReopensItsOwnMenu(string name)
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            using var fixture = new Fixture(name);
            fixture.Click();
            Assert.True(fixture.Menu.IsOpen);
            Assert.Same(fixture.Button, fixture.Menu.PlacementTarget);
            fixture.Click();
            Assert.False(fixture.Menu.IsOpen);
            fixture.Click();
            Assert.True(fixture.Menu.IsOpen);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData("HierarchyButton")]
    [InlineData("PlacesButton")]
    [InlineData("SortButton")]
    [InlineData("ArrangeMenuButton")]
    public void AutomaticDismissalConsumesOnlyTheSamePressAndAllowsTheNextClick(string name)
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            using var fixture = new Fixture(name);
            fixture.Click();
            fixture.Menu.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1000, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownOutsideCapturedElementEvent });
            fixture.Menu.IsOpen = false;
            var samePress = fixture.PreviewPress(1000);
            Assert.True(samePress.Handled);
            Assert.False(fixture.Menu.IsOpen);
            Assert.False(fixture.PreviewPress(1001).Handled);
            fixture.Click();
            Assert.True(fixture.Menu.IsOpen);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData("HierarchyButton")]
    [InlineData("PlacesButton")]
    [InlineData("SortButton")]
    [InlineData("ArrangeMenuButton")]
    public void ClosingDoesNotRebuildMenuItems(string name)
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            using var fixture = new Fixture(name);
            fixture.Click();
            Assert.NotEmpty(fixture.Menu.Items);
            var first = fixture.Menu.Items[0];
            fixture.Click();
            Assert.False(fixture.Menu.IsOpen);
            Assert.Same(first, fixture.Menu.Items[0]);
            return Task.CompletedTask;
        });

    [Fact]
    public void DismissalOfOneButtonDoesNotConsumeAnotherButtonOrAnotherViewPress()
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            using var first = new Fixture("HierarchyButton");
            using var second = new Fixture("HierarchyButton");
            first.Click();
            first.Menu.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1000, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownOutsideCapturedElementEvent });
            first.Menu.IsOpen = false;
            Assert.False(second.PreviewPress(1000).Handled);
            var other = Assert.IsType<Button>(first.View.FindName("PlacesButton"));
            var otherPress = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1000, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            other.RaiseEvent(otherPress);
            Assert.False(otherPress.Handled);
            Assert.False(first.PreviewPress(1000).Handled);
            return Task.CompletedTask;
        });

    [Fact]
    public void RightButtonOutsidePressDoesNotCancelNextLeftClick()
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            using var fixture = new Fixture("SortButton");
            fixture.Click();
            fixture.Menu.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1000, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownOutsideCapturedElementEvent });
            fixture.Menu.IsOpen = false;
            Assert.False(fixture.PreviewPress(1000).Handled);
            fixture.Click();
            Assert.True(fixture.Menu.IsOpen);
            return Task.CompletedTask;
        });

    private sealed class Fixture : IDisposable
    {
        private readonly UserControl _view;
        internal UserControl View => _view;
        internal Button Button { get; }
        internal ContextMenu Menu => Button.ContextMenu!;

        internal Fixture(string name)
        {
            _view = name == "ArrangeMenuButton"
                ? new WorkspaceCanvasView()
                : new FolderTabView(AppContext.BaseDirectory);
            if (_view is FolderTabView folder)
                typeof(FolderTabView).GetProperty(nameof(FolderTabView.CurrentPath))!.SetValue(folder, AppContext.BaseDirectory);
            Button = Assert.IsType<Button>(_view.FindName(name));
            Button.Measure(new Size(100, 30));
            Button.Arrange(new Rect(0, 0, 100, 30));
        }

        internal void Click() => Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        internal MouseButtonEventArgs PreviewPress(int timestamp)
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            Button.RaiseEvent(press);
            return press;
        }
        public void Dispose()
        {
            Menu.IsOpen = false;
            ((IDisposable)_view).Dispose();
        }
    }
}

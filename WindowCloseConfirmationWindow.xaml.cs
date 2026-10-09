using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace TabBrowser;

public partial class WindowCloseConfirmationWindow : Window
{
    private bool _hasMovedPointer;

    public WindowCloseConfirmationWindow()
    {
        InitializeComponent();
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_hasMovedPointer
            || !CloseCurrentButton.IsVisible
            || CloseCurrentButton.ActualWidth <= 0
            || CloseCurrentButton.ActualHeight <= 0)
        {
            return;
        }

        _hasMovedPointer = true;
        var buttonCenter = CloseCurrentButton.PointToScreen(
            new Point(
                CloseCurrentButton.ActualWidth / 2,
                CloseCurrentButton.ActualHeight / 2));
        SetCursorPos(
            (int)Math.Round(buttonCenter.X),
            (int)Math.Round(buttonCenter.Y));
    }

    private void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Yes;
        Close();
    }

    private void CloseCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.No;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}

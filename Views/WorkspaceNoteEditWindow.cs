using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;

namespace TabBrowser.Views;

public sealed class WorkspaceNoteEditWindow : Window
{
    private readonly TextBox _contentBox;
    private readonly TextBlock _fontSizeText;
    private readonly TextBlock _statusText;
    private readonly Button _saveAndCloseButton;
    private readonly Button _updateButton;
    private readonly Button _saveAsNewButton;
    private readonly Button _cancelButton;
    private readonly Button _decreaseFontSizeButton;
    private readonly Button _increaseFontSizeButton;
    private double _noteFontSize;
    private Func<string, double, Task<bool>>? _updateWithoutClosingAsync;
    private bool _isSaving;
    private bool _allowCloseAfterSave;

    public WorkspaceNoteEditWindow(
        string title,
        string initialContent,
        WorkspaceNoteEditorWindowPlacementSettings? placement = null,
        double initialFontSize = WorkspaceNoteFontSizeProfile.DefaultFontSize)
    {
        _noteFontSize = WorkspaceNoteFontSizeProfile.Normalize(initialFontSize);
        Title = title;
        Width = WorkspaceNoteEditorWindowPlacementProfile.DefaultWidth;
        Height = WorkspaceNoteEditorWindowPlacementProfile.DefaultHeight;
        MinWidth = WorkspaceNoteEditorWindowPlacementProfile.MinimumWidth;
        MinHeight = WorkspaceNoteEditorWindowPlacementProfile.MinimumHeight;
        Background = CreateBrush(16, 17, 20);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ApplyWindowPlacement(placement);
        WindowPlacementVisibilityService.Attach(this);

        var root = new Grid
        {
            Margin = new Thickness(8)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.PreviewKeyDown += Root_PreviewKeyDown;
        Content = root;

        _contentBox = CreateTextBox(initialContent);
        _contentBox.AcceptsReturn = true;
        _contentBox.AcceptsTab = true;
        _contentBox.FontSize = _noteFontSize;
        _contentBox.TextWrapping = TextWrapping.Wrap;
        _contentBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _contentBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _contentBox.VerticalContentAlignment = VerticalAlignment.Top;
        Grid.SetRow(_contentBox, 0);
        root.Children.Add(_contentBox);

        _statusText = new TextBlock
        {
            Foreground = CreateBrush(248, 113, 113),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_statusText, 1);
        root.Children.Add(_statusText);

        var shortcutHint = new TextBlock
        {
            Text = "Ctrl+Enter：保存して閉じる　Esc：キャンセル",
            Foreground = CreateBrush(143, 148, 158),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(shortcutHint, 3);
        root.Children.Add(shortcutHint);

        var buttons = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        _saveAndCloseButton = CreateButton("保存して閉じる");
        _saveAndCloseButton.Click += OkButton_Click;
        buttons.Children.Add(_saveAndCloseButton);

        _updateButton = CreateButton("保存して続ける");
        _updateButton.Visibility = Visibility.Collapsed;
        _updateButton.Click += UpdateButton_Click;
        buttons.Children.Add(_updateButton);

        _saveAsNewButton = CreateButton("新しいメモとして保存");
        _saveAsNewButton.Visibility = Visibility.Collapsed;
        _saveAsNewButton.ToolTip = "元のWorkspaceの直下へ別のメモとして保存して閉じます";
        _saveAsNewButton.Click += async (_, _) => await TrySaveAsNewAsync();
        Grid.SetRow(_saveAsNewButton, 2);
        _saveAsNewButton.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_saveAsNewButton);

        _cancelButton = CreateButton("キャンセル");
        _cancelButton.Click += (_, _) => Close();
        DockPanel.SetDock(_cancelButton, Dock.Right);
        buttons.Children.Add(_cancelButton);

        var fontSizeControls = new StackPanel
        {
            Margin = new Thickness(12, 0, 6, 0),
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(fontSizeControls, Dock.Right);
        buttons.Children.Add(fontSizeControls);

        _decreaseFontSizeButton = CreateButton("A−");
        _decreaseFontSizeButton.MinWidth = 34;
        _decreaseFontSizeButton.Width = 34;
        _decreaseFontSizeButton.ToolTip = "文字を小さく";
        _decreaseFontSizeButton.Click += (_, _) => ChangeFontSize(-WorkspaceNoteFontSizeProfile.Step);
        fontSizeControls.Children.Add(_decreaseFontSizeButton);

        _fontSizeText = new TextBlock
        {
            Width = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CreateBrush(209, 213, 219),
            Text = FormatFontSize(_noteFontSize),
            TextAlignment = TextAlignment.Center
        };
        fontSizeControls.Children.Add(_fontSizeText);

        _increaseFontSizeButton = CreateButton("A＋");
        _increaseFontSizeButton.MinWidth = 34;
        _increaseFontSizeButton.Width = 34;
        _increaseFontSizeButton.Margin = new Thickness(0);
        _increaseFontSizeButton.ToolTip = "文字を大きく";
        _increaseFontSizeButton.Click += (_, _) => ChangeFontSize(WorkspaceNoteFontSizeProfile.Step);
        fontSizeControls.Children.Add(_increaseFontSizeButton);

        Loaded += (_, _) =>
        {
            FocusContentAtEnd();
        };
    }

    public string NoteContent { get; private set; } = string.Empty;

    public double NoteFontSize => _noteFontSize;

    public bool IsAccepted { get; private set; }

    public Func<string, double, Task<bool>>? SaveAndCloseAsync { get; set; }

    public Func<string, double, Task<bool>>? SaveAsNewAsync { get; set; }

    internal bool IsTargetMissing { get; private set; }

    internal void ReportMissingTarget()
    {
        IsTargetMissing = true;
        _saveAsNewButton.Visibility = SaveAsNewAsync is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public Func<string, double, Task<bool>>? UpdateWithoutClosingAsync
    {
        get => _updateWithoutClosingAsync;
        set
        {
            _updateWithoutClosingAsync = value;
            _updateButton.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public WorkspaceNoteEditorWindowPlacementSettings? CapturedWindowPlacement { get; private set; }

    public void FocusContentAtEnd()
    {
        _contentBox.Focus();
        _contentBox.CaretIndex = _contentBox.Text.Length;
        _contentBox.SelectionLength = 0;
        _contentBox.ScrollToEnd();
    }

    private static TextBox CreateTextBox(string text)
    {
        return new TextBox
        {
            Text = text,
            Padding = new Thickness(10, 8, 10, 8),
            Foreground = CreateBrush(243, 244, 246),
            Background = CreateBrush(8, 10, 14),
            BorderBrush = CreateBrush(52, 56, 66),
            BorderThickness = new Thickness(1),
            CaretBrush = CreateBrush(243, 244, 246)
        };
    }

    private static Button CreateButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 68,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Foreground = CreateBrush(243, 244, 246),
            Background = CreateBrush(35, 38, 45),
            BorderBrush = CreateBrush(52, 56, 66),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void ApplyWindowPlacement(WorkspaceNoteEditorWindowPlacementSettings? placement)
    {
        if (WorkspaceNoteEditorWindowPlacementProfile.Normalize(placement) is not { } normalized)
        {
            return;
        }

        Width = normalized.Width;
        Height = normalized.Height;
        if (!HasSufficientVisibleArea(
                normalized.Left,
                normalized.Top,
                normalized.Width,
                normalized.Height))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = normalized.Left;
        Top = normalized.Top;
    }

    private void CaptureWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        CapturedWindowPlacement = WorkspaceNoteEditorWindowPlacementProfile.Normalize(
            new WorkspaceNoteEditorWindowPlacementSettings
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height
            });
    }

    private static bool HasSufficientVisibleArea(double left, double top, double width, double height)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        var minimumVisibleWidth = Math.Min(80, width);
        var minimumVisibleHeight = Math.Min(32, height);

        return left + width >= screenLeft + minimumVisibleWidth
            && left <= screenRight - minimumVisibleWidth
            && top + height >= screenTop + minimumVisibleHeight
            && top <= screenBottom - minimumVisibleHeight;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isSaving && !_allowCloseAfterSave)
        {
            e.Cancel = true;
            ShowStatus("保存中は閉じられません", isError: false);
            base.OnClosing(e);
            return;
        }

        CaptureWindowPlacement();
        base.OnClosing(e);
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            TryAccept();
            e.Handled = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        TryAccept();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await TrySaveWithoutClosingAsync();
    }

    internal Task<bool> TrySaveAndCloseAsync()
        => TrySaveAsync(closeAfterSuccess: true);

    internal Task<bool> TrySaveWithoutClosingAsync()
        => TrySaveAsync(closeAfterSuccess: false);

    internal Task<bool> TrySaveAsNewAsync()
        => IsTargetMissing ? TrySaveAsync(closeAfterSuccess: true, saveAsNew: true) : Task.FromResult(false);

    internal bool IsSaving => _isSaving;

    internal bool IsEditorReadOnly => _contentBox.IsReadOnly;

    internal string EditorContent => _contentBox.Text;

    internal string StatusText => _statusText.Text;

    internal void ShowRequiredCloseAction()
    {
        if (_statusText.Visibility != Visibility.Visible)
            ShowStatus("全Windowを閉じるには、このメモを保存して閉じるかキャンセルしてください", isError: false);
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async Task<bool> TrySaveAsync(bool closeAfterSuccess, bool saveAsNew = false)
    {
        if (_isSaving)
        {
            return false;
        }

        var saveAsync = saveAsNew ? SaveAsNewAsync : closeAfterSuccess
            ? SaveAndCloseAsync ?? _updateWithoutClosingAsync : _updateWithoutClosingAsync;
        if (saveAsync is null)
        {
            ShowStatus("保存処理を開始できません", isError: true);
            return false;
        }

        if (!TryGetContent(out var content))
        {
            return false;
        }

        _isSaving = true;
        SetEditorEnabled(enabled: false);
        ShowStatus("保存中です", isError: false);
        try
        {
            bool saved;
            try
            {
                saved = await saveAsync(content, NoteFontSize);
            }
            catch (Exception ex)
            {
                if (IsLoaded)
                {
                    ShowStatus("保存できませんでした: " + ex.Message, isError: true);
                }

                return false;
            }

            if (!IsLoaded)
            {
                return saved;
            }

            if (!saved)
            {
                ShowStatus(IsTargetMissing
                    ? "元のメモは削除されています。新しいメモとして元のWorkspace直下へ保存できます"
                    : "保存できませんでした。内容を確認して再試行してください", isError: true);
                return false;
            }

            NoteContent = content;
            if (closeAfterSuccess)
            {
                IsAccepted = true;
                _allowCloseAfterSave = true;
                Close();
                return true;
            }

            ShowStatus("更新しました", isError: false);
            _contentBox.Focus();
            return true;
        }
        finally
        {
            _allowCloseAfterSave = false;
            _isSaving = false;
            if (IsLoaded)
            {
                SetEditorEnabled(enabled: true);
            }
        }
    }

    private void TryAccept()
    {
        _ = TrySaveAndCloseAsync();
    }

    private void SetEditorEnabled(bool enabled)
    {
        _contentBox.IsReadOnly = !enabled;
        _saveAndCloseButton.IsEnabled = enabled;
        _updateButton.IsEnabled = enabled;
        _saveAsNewButton.IsEnabled = enabled;
        _cancelButton.IsEnabled = enabled;
        _decreaseFontSizeButton.IsEnabled = enabled;
        _increaseFontSizeButton.IsEnabled = enabled;
    }

    private void ChangeFontSize(double delta)
    {
        var next = WorkspaceNoteFontSizeProfile.Normalize(_noteFontSize + delta);
        if (Math.Abs(next - _noteFontSize) < 0.1)
        {
            return;
        }

        _noteFontSize = next;
        _contentBox.FontSize = next;
        _fontSizeText.Text = FormatFontSize(next);
    }

    private static string FormatFontSize(double value)
    {
        return $"{value:0}";
    }

    private bool TryGetContent(out string content)
    {
        content = _contentBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        ShowStatus("メモを入力してください", isError: true);
        return false;
    }

    private void ShowStatus(string text, bool isError)
    {
        _statusText.Text = text;
        _statusText.Foreground = isError
            ? CreateBrush(248, 113, 113)
            : CreateBrush(74, 222, 128);
        _statusText.Visibility = Visibility.Visible;
    }
}

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
    private readonly Border _closeConfirmation;
    private Window? _closeConfirmationWindow;
    private readonly TextBlock _closeConfirmationStatus;
    private readonly Button _confirmSaveButton;
    private readonly Button _discardAllButton;
    private bool _isRequestingCloseAll;
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
    private bool _hasBeenShown;

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

        // 保存状態の表示とは分け、保存済み・エラー表示中でも終了の案内を見落とさない。
        var closeActions = new StackPanel();
        var message = new Grid { Margin = new Thickness(0, 4, 0, 28) };
        message.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        message.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        message.Children.Add(new TextBlock
        {
            Text = "\uE946",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 38,
            Foreground = CreateBrush(244, 193, 94),
            Margin = new Thickness(0, 2, 0, 0)
        });
        var messageText = new StackPanel();
        Grid.SetColumn(messageText, 1);
        message.Children.Add(messageText);
        closeActions.Children.Add(message);
        messageText.Children.Add(new TextBlock
        {
            Text = "終了する前に、メモを確認してください。",
            Foreground = CreateBrush(243, 244, 246),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        messageText.Children.Add(new TextBlock
        {
            Text = "保存せずに終了すると、すべてのメモの未保存変更が失われます。\n保存済みの内容は残ります。",
            Foreground = CreateBrush(209, 213, 219),
            FontSize = 16,
            LineHeight = 26,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 22, 0, 16)
        });
        messageText.Children.Add(new TextBlock
        {
            Text = "保存する場合は、このメモを閉じた後にもう一度終了してください。",
            Foreground = CreateBrush(160, 166, 177),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        var actionRow = new Grid();
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        closeActions.Children.Add(actionRow);
        _confirmSaveButton = CreateButton("このメモを保存して閉じる");
        _confirmSaveButton.Background = CreateBrush(30, 100, 133);
        _confirmSaveButton.BorderBrush = CreateBrush(82, 160, 190);
        _confirmSaveButton.ToolTip = "このメモを保存します。全ウィンドウの終了は、もう一度操作してください。";
        _confirmSaveButton.Click += async (_, _) =>
        {
            if (IsTargetMissing) await TrySaveAsNewAsync();
            else await TrySaveAndCloseAsync();
        };
        Grid.SetColumn(_confirmSaveButton, 1);
        actionRow.Children.Add(_confirmSaveButton);
        _discardAllButton = CreateButton("保存せずにすべて閉じる");
        _discardAllButton.Background = CreateBrush(20, 22, 26);
        _discardAllButton.BorderBrush = CreateBrush(244, 193, 94);
        _discardAllButton.Foreground = CreateBrush(244, 193, 94);
        _discardAllButton.Click += async (_, _) => await RequestCloseAllWithoutSavingAsync();
        Grid.SetColumn(_discardAllButton, 2);
        actionRow.Children.Add(_discardAllButton);
        var continueEditing = CreateButton("編集に戻る");
        continueEditing.Click += (_, _) => DismissCloseConfirmation();
        actionRow.Children.Add(continueEditing);
        foreach (var button in actionRow.Children.OfType<Button>())
        {
            button.Height = 44;
            button.FontSize = 14;
            button.FontWeight = FontWeights.SemiBold;
            button.Margin = new Thickness(0, 0, ReferenceEquals(button, _discardAllButton) ? 0 : 12, 0);
        }
        _closeConfirmationStatus = new TextBlock
        {
            Foreground = CreateBrush(248, 113, 113),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        closeActions.Children.Add(_closeConfirmationStatus);
        _closeConfirmation = new Border
        {
            Background = CreateBrush(28, 32, 40),
            Padding = new Thickness(24),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                Content = closeActions,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };

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
            _hasBeenShown = true;
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
            if (IsCloseConfirmationVisible)
            {
                DismissCloseConfirmation();
                e.Handled = true;
                return;
            }
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

    // 非表示にした編集Windowも保護し、一度も表示されていない未使用Windowは区別する。
    internal bool RequiresCloseConfirmation => _hasBeenShown || IsVisible || IsSaving;

    internal bool IsEditorReadOnly => _contentBox.IsReadOnly;

    internal string EditorContent => _contentBox.Text;

    internal string StatusText => _statusText.Text;

    internal bool IsCloseConfirmationVisible => _closeConfirmationWindow?.IsVisible == true;

    internal Window? CloseConfirmationWindow => _closeConfirmationWindow;

    internal Func<Task<WindowPersistenceResult>>? CloseAllWithoutSavingAsync { get; set; }

    internal async Task RequestCloseAllWithoutSavingAsync()
    {
        if (_isSaving || _isRequestingCloseAll || CloseAllWithoutSavingAsync is null) return;
        _isRequestingCloseAll = true;
        SetEditorEnabled(false);
        try
        {
            var result = await CloseAllWithoutSavingAsync();
            if (!result.Succeeded) ShowStatus(result.FailureMessage, isError: true);
        }
        catch (Exception ex)
        {
            ShowStatus("終了できませんでした: " + ex.Message, isError: true);
        }
        finally
        {
            _isRequestingCloseAll = false;
            SetEditorEnabled(true);
        }
    }

    internal void DismissCloseConfirmation()
    {
        if (_isRequestingCloseAll) return;
        _closeConfirmationWindow?.Close();
    }

    internal void ShowRequiredCloseAction()
    {
        _closeConfirmation.Visibility = Visibility.Visible;
        _discardAllButton.Visibility = CloseAllWithoutSavingAsync is null ? Visibility.Collapsed : Visibility.Visible;
        _confirmSaveButton.Content = IsTargetMissing ? "新しいメモとして保存して閉じる" : "このメモを保存して閉じる";
        if (_statusText.Visibility != Visibility.Visible)
            ShowStatus("全Windowを閉じるには、このメモを保存して閉じるかキャンセルしてください", isError: false);
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (_closeConfirmationWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var dialog = new Window
        {
            Title = "メモの確認",
            Owner = this,
            Width = Math.Min(920, SystemParameters.WorkArea.Width - 32),
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = CreateBrush(16, 17, 20),
            Content = _closeConfirmation
        };
        _closeConfirmationWindow = dialog;
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DismissCloseConfirmation();
        };
        dialog.Closed += (_, _) =>
        {
            dialog.Content = null;
            _closeConfirmationWindow = null;
            if (IsVisible) FocusContentAtEnd();
        };
        ModelessWindowService.Show(dialog, forceTopmost: true);
    }

    private async Task<bool> TrySaveAsync(bool closeAfterSuccess, bool saveAsNew = false)
    {
        if (_isSaving || _isRequestingCloseAll)
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
        _confirmSaveButton.IsEnabled = enabled;
        _discardAllButton.IsEnabled = enabled;
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
        _closeConfirmationStatus.Text = isError ? text : string.Empty;
    }
}

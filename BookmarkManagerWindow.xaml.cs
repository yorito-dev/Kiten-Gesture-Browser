using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class BookmarkManagerWindow : Window
{
    private const string BookmarkDragFormat = "TabBrowser.BookmarkDrag.v1";
    private const string FolderDragFormat = "TabBrowser.BookmarkFolderDrag.v1";
    private const int BookmarkCheckConcurrency = 6;
    private static readonly TimeSpan BookmarkCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient BookmarkCheckHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    });

    private readonly BookmarkService _bookmarkService = new();
    private readonly BookmarkData _initialData;

    public BookmarkManagerWindow(
        ObservableCollection<BookmarkItem> bookmarks,
        ObservableCollection<BookmarkFolderItem> folders,
        BookmarkSortMode sortMode,
        BookmarkManagerWindowSizeSettings? windowSize)
    {
        InitializeComponent();
        ApplyWindowSize(windowSize);
        Bookmarks = new ObservableCollection<BookmarkItem>(bookmarks.Select(BookmarkDataSnapshot.Clone));
        Folders = new ObservableCollection<BookmarkFolderItem>(folders.Select(BookmarkDataSnapshot.Clone));
        SortMode = sortMode;
        _initialData = BookmarkDataSnapshot.Clone(CreateBookmarkData());
        BookmarkView = CollectionViewSource.GetDefaultView(Bookmarks);
        BookmarkView.Filter = FilterBookmark;
        DataContext = this;
        SortModeBox.ItemsSource = SortModes;
        SortModeBox.SelectedValue = SortMode;
        ApplyBookmarkSort();
        RefreshBookmarkFolders();
        UpdateStatus();
    }

    public ObservableCollection<BookmarkItem> Bookmarks { get; }

    public ObservableCollection<BookmarkFolderItem> Folders { get; }

    public ObservableCollection<BookmarkFolderFilterItem> BookmarkFolders { get; } = [];

    public ICollectionView BookmarkView { get; }

    public IReadOnlyList<BookmarkSortOption> SortModes { get; } =
    [
        new("手動順", BookmarkSortMode.Manual),
        new("タイトル", BookmarkSortMode.Title),
        new("フォルダ", BookmarkSortMode.Folder),
        new("URL", BookmarkSortMode.Url),
        new("開いた回数", BookmarkSortMode.OpenCount),
        new("最近開いた順", BookmarkSortMode.Recent)
    ];

    public BookmarkSortMode SortMode { get; private set; }

    public BookmarkManagerWindowSizeSettings? CapturedWindowSize { get; private set; }

    public BookmarkItem? SelectedBookmark { get; private set; }

    public bool IsAccepted { get; private set; }

    public bool DeletedBookmarks { get; private set; }

    public bool ImportedBookmarks { get; private set; }

    public bool EditedBookmarks { get; private set; }

    public bool ChangedBookmarks => DeletedBookmarks || ImportedBookmarks || EditedBookmarks;

    internal BookmarkChangeSet CreateChangeSet()
        => BookmarkChangeSet.Create(_initialData, CreateBookmarkData());

    private bool _isAllFoldersSelected = true;
    private bool _isRefreshingBookmarkFolders;
    private BookmarkItem? _bookmarkDragSource;
    private BookmarkFolderFilterItem? _folderDragSource;
    private BookmarkFolderFilterItem? _bookmarkDropFeedbackFolder;
    private BookmarkFolderFilterItem? _folderDropFeedbackFolder;
    private CancellationTokenSource? _bookmarkCheckCancellation;
    private bool _isCheckingBookmarks;
    private Point _bookmarkDragStartPoint;
    private Point _folderDragStartPoint;
    private string _selectedFolderPath = string.Empty;

    private void BookmarkGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selectedCount = GetSelectedBookmarks().Count;
        OpenButton.IsEnabled = selectedCount == 1;
        EditButton.IsEnabled = selectedCount == 1;
        MoveSelectedButton.IsEnabled = selectedCount > 0;
        DeleteButton.IsEnabled = selectedCount > 0;
    }

    private void FolderList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var folder = FolderList.SelectedItem as BookmarkFolderFilterItem;
        UpdateFolderActionButtons(folder);

        if (_isRefreshingBookmarkFolders
            || folder is null)
        {
            return;
        }

        ApplyFolderFilter(folder);
        BookmarkGrid.SelectedItems.Clear();
        BookmarkView.Refresh();
        UpdateStatus();
    }

    private void FolderList_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetFolderDragItem(e, out var sourceFolderPath)
            && TryGetDropFolder(e, out var targetFolder))
        {
            UpdateFolderDropFeedback(sourceFolderPath, targetFolder);
            e.Effects = CanReorderFolder(sourceFolderPath, targetFolder)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }
        else if (TryGetBookmarkDragItems(e, out var bookmarks)
            && bookmarks.Count > 0
            && TryGetDropFolder(e, out var bookmarkTargetFolder))
        {
            UpdateBookmarkDropFeedback(bookmarkTargetFolder, bookmarks.Count);
            e.Effects = bookmarkTargetFolder.CanAcceptBookmarkDrop
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }
        else
        {
            ClearBookmarkDropFeedback(updateStatus: false);
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void FolderList_DragLeave(object sender, DragEventArgs e)
    {
        ClearBookmarkDropFeedback(updateStatus: true);
        ClearFolderDropFeedback(updateStatus: true);
        e.Handled = true;
    }

    private void FolderList_Drop(object sender, DragEventArgs e)
    {
        if (TryGetFolderDragItem(e, out var sourceFolderPath)
            && TryGetDropFolder(e, out var targetFolder))
        {
            if (CanReorderFolder(sourceFolderPath, targetFolder))
            {
                ClearFolderDropFeedback(updateStatus: false);
                ReorderFolder(sourceFolderPath, targetFolder.FolderPath ?? string.Empty);
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                ClearFolderDropFeedback(updateStatus: true);
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
            return;
        }

        if (!TryGetBookmarkDragItems(e, out var bookmarks)
            || bookmarks.Count == 0
            || !TryGetDropFolder(e, out var dropFolder)
            || !dropFolder.CanAcceptBookmarkDrop)
        {
            ClearBookmarkDropFeedback(updateStatus: true);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ClearBookmarkDropFeedback(updateStatus: false);
        MoveBookmarksToFolder(bookmarks, dropFolder.FolderPath ?? string.Empty);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void FolderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _folderDragStartPoint = e.GetPosition(this);
        _folderDragSource = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as BookmarkFolderFilterItem;
    }

    private void FolderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_folderDragSource is null || !_folderDragSource.CanEdit || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _folderDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _folderDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var sourceFolderPath = _folderDragSource.FolderPath;
        if (string.IsNullOrWhiteSpace(sourceFolderPath))
        {
            _folderDragSource = null;
            return;
        }

        var data = new DataObject();
        data.SetData(FolderDragFormat, sourceFolderPath);
        try
        {
            DragDrop.DoDragDrop(FolderList, data, DragDropEffects.Move);
        }
        finally
        {
            ClearFolderDropFeedback(updateStatus: true);
            _folderDragSource = null;
        }
    }

    private async void FolderCreateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BookmarkFolderInputWindow(
            "新規フォルダ",
            "作成するフォルダ名を入力してください。`親 / 子` のように入力すると階層を作れます。",
            _isAllFoldersSelected ? string.Empty : _selectedFolderPath,
            allowEmptyValue: false,
            candidateFolderPaths: GetFolderCandidatePaths())
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        var folderPath = BookmarkService.NormalizeFolderPath(dialog.EditedValue);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            StatusText.Text = "フォルダ名を入力してください";
            return;
        }

        if (HasFolder(folderPath))
        {
            StatusText.Text = "同じフォルダが既にあります";
            return;
        }

        EnsureFolderPath(folderPath);
        EditedBookmarks = true;
        _isAllFoldersSelected = false;
        _selectedFolderPath = folderPath;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"「{folderPath}」を作成しました";
    }

    private void FolderDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not BookmarkFolderFilterItem folder || !folder.CanDelete)
        {
            StatusText.Text = "削除するフォルダを選択してください";
            return;
        }

        var bookmarks = GetBookmarksInFolder(folder);
        var folderCount = CountFolderRecords(folder.FolderPath ?? string.Empty);
        var message = bookmarks.Count == 0
            ? $"空フォルダ「{folder.FolderPath}」を削除します。"
            : $"「{folder.FolderPath}」配下のブックマーク {bookmarks.Count} 件とフォルダを削除します。\nこの操作は元に戻せません。";
        var result = MessageBox.Show(
            this,
            message,
            "フォルダ削除の確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            StatusText.Text = "フォルダ削除をキャンセルしました";
            return;
        }

        foreach (var bookmark in bookmarks)
        {
            Bookmarks.Remove(bookmark);
        }

        RemoveFolderRecords(folder.FolderPath ?? string.Empty);
        DeletedBookmarks = true;
        _isAllFoldersSelected = true;
        _selectedFolderPath = string.Empty;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = bookmarks.Count == 0
            ? $"空フォルダ「{folder.DisplayName}」を削除しました"
            : $"「{folder.DisplayName}」配下のブックマーク {bookmarks.Count} 件とフォルダ {folderCount} 件を削除しました";
    }

    private async void FolderRenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not BookmarkFolderFilterItem folder || !folder.CanEdit)
        {
            StatusText.Text = "名前を変更するフォルダを選択してください";
            return;
        }

        var dialog = new BookmarkFolderInputWindow(
            "フォルダ名変更",
            $"「{folder.FolderPath}」の新しいフォルダ名を入力してください。",
            folder.DisplayName,
            allowEmptyValue: false)
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        if (!HasFolder(folder.FolderPath ?? string.Empty))
        {
            StatusText.Text = "対象フォルダは既にありません";
            return;
        }

        var newName = GetLastFolderSegment(dialog.EditedValue);
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "フォルダ名を入力してください";
            return;
        }

        var parentPath = GetParentFolderPath(folder.FolderPath);
        var newFolderPath = CombineFolderPath(parentPath, newName);
        if (string.Equals(folder.FolderPath, newFolderPath, StringComparison.CurrentCultureIgnoreCase))
        {
            StatusText.Text = "フォルダ名は変更されていません";
            return;
        }

        RenameOrMoveFolder(folder, newFolderPath, $"「{folder.DisplayName}」を「{newName}」に変更しました");
    }

    private async void FolderMoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not BookmarkFolderFilterItem folder || !folder.CanEdit)
        {
            StatusText.Text = "移動するフォルダを選択してください";
            return;
        }

        var dialog = new BookmarkFolderInputWindow(
            "フォルダ移動",
            $"「{folder.FolderPath}」の移動先親フォルダを選択または入力してください。空欄にするとルートへ移動します。",
            GetParentFolderPath(folder.FolderPath),
            allowEmptyValue: true,
            candidateFolderPaths: GetFolderMoveCandidatePaths(folder))
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        if (!HasFolder(folder.FolderPath ?? string.Empty))
        {
            StatusText.Text = "対象フォルダは既にありません";
            return;
        }

        var destinationParentPath = BookmarkService.NormalizeFolderPath(dialog.EditedValue);
        if (IsSameOrChildFolder(destinationParentPath, folder.FolderPath ?? string.Empty))
        {
            StatusText.Text = "選択フォルダ自身または配下へは移動できません";
            return;
        }

        var newFolderPath = CombineFolderPath(destinationParentPath, folder.DisplayName);
        if (string.Equals(folder.FolderPath, newFolderPath, StringComparison.CurrentCultureIgnoreCase))
        {
            StatusText.Text = "移動先は変更されていません";
            return;
        }

        RenameOrMoveFolder(folder, newFolderPath, $"「{folder.DisplayName}」を「{newFolderPath}」へ移動しました");
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        BookmarkView.Refresh();
        UpdateStatus();
    }

    private void SortModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SortModeBox.SelectedValue is not BookmarkSortMode mode || mode == SortMode)
        {
            return;
        }

        SortMode = mode;
        EditedBookmarks = true;
        ApplyBookmarkSort();
        BookmarkView.Refresh();
        UpdateStatus();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void BookmarkGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedBookmark();
    }

    private void BookmarkGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _bookmarkDragStartPoint = e.GetPosition(this);
        _bookmarkDragSource = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as BookmarkItem;
    }

    private void BookmarkGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_bookmarkDragSource is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _bookmarkDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _bookmarkDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var bookmarks = GetSelectedBookmarks();
        if (!bookmarks.Contains(_bookmarkDragSource))
        {
            bookmarks = [_bookmarkDragSource];
        }

        if (bookmarks.Count == 0)
        {
            _bookmarkDragSource = null;
            return;
        }

        var data = new DataObject();
        data.SetData(BookmarkDragFormat, bookmarks.Select(static x => x.Id).ToArray());
        try
        {
            DragDrop.DoDragDrop(BookmarkGrid, data, DragDropEffects.Move);
        }
        finally
        {
            ClearBookmarkDropFeedback(updateStatus: true);
            _bookmarkDragSource = null;
        }
    }

    private void BookmarkGrid_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetBookmarkDragItems(e, out var bookmarks)
            && bookmarks.Count > 0
            && TryGetDropBookmark(e, out var targetBookmark)
            && !bookmarks.Contains(targetBookmark))
        {
            e.Effects = DragDropEffects.Move;
            StatusText.Text = $"{bookmarks.Count} 件を「{targetBookmark.Title}」の前へ移動";
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void BookmarkGrid_Drop(object sender, DragEventArgs e)
    {
        if (TryGetBookmarkDragItems(e, out var bookmarks)
            && bookmarks.Count > 0
            && TryGetDropBookmark(e, out var targetBookmark)
            && !bookmarks.Contains(targetBookmark))
        {
            ReorderBookmarks(bookmarks, targetBookmark);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedBookmark();
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        await EditSelectedBookmarkAsync();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BookmarkImportWindow(Bookmarks)
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded || dialog.ImportedBookmarks.Count == 0)
        {
            return;
        }

        var existingUrls = Bookmarks
            .Select(static x => BookmarkService.NormalizeUrl(x.Url))
            .ToHashSet(BookmarkService.UrlComparer);

        var importedCount = 0;
        foreach (var bookmark in dialog.ImportedBookmarks)
        {
            var normalizedUrl = BookmarkService.NormalizeUrl(bookmark.Url);
            if (existingUrls.Contains(normalizedUrl))
            {
                continue;
            }

            bookmark.SortOrder = GetNextBookmarkSortOrder();
            bookmark.FolderPath = BookmarkService.NormalizeFolderPath(bookmark.FolderPath);
            EnsureFolderPath(bookmark.FolderPath);
            Bookmarks.Add(bookmark);
            existingUrls.Add(normalizedUrl);
            importedCount++;
        }

        if (importedCount == 0)
        {
            StatusText.Text = "新しく取り込めるブックマークはありませんでした";
            return;
        }

        ImportedBookmarks = true;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"{importedCount} 件のブックマークを取り込みました";
    }

    private void ExportHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Chrome/Edge 形式でエクスポート",
            Filter = "HTML ブックマーク (*.html)|*.html|すべてのファイル (*.*)|*.*",
            FileName = $"TabBrowser-Bookmarks-{DateTime.Now:yyyyMMdd-HHmmss}.html"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _bookmarkService.ExportHtml(dialog.FileName, CreateBookmarkData());
            StatusText.Text = $"HTML を出力しました: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "HTML を出力できません: " + ex.Message;
        }
    }

    private void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "ブックマークをバックアップ",
            Filter = "TabBrowser ブックマーク JSON (*.json)|*.json|すべてのファイル (*.*)|*.*",
            FileName = $"TabBrowser-Bookmarks-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _bookmarkService.SaveDataToFile(dialog.FileName, CreateBookmarkData());
            StatusText.Text = $"バックアップしました: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "バックアップできません: " + ex.Message;
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ブックマークを復元",
            Filter = "TabBrowser ブックマーク JSON (*.json)|*.json|すべてのファイル (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BookmarkData data;
        try
        {
            data = _bookmarkService.LoadDataFromFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = "復元ファイルを読み込めません: " + ex.Message;
            return;
        }

        var result = MessageBox.Show(
            this,
            $"現在のブックマーク {Bookmarks.Count} 件を、復元ファイルの {data.Bookmarks.Count} 件で置き換えます。\n続行しますか？",
            "ブックマーク復元の確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            StatusText.Text = "復元をキャンセルしました";
            return;
        }

        Bookmarks.Clear();
        foreach (var bookmark in data.Bookmarks)
        {
            Bookmarks.Add(bookmark);
        }

        Folders.Clear();
        foreach (var folder in data.Folders)
        {
            Folders.Add(folder);
        }

        SortMode = data.SortMode;
        SortModeBox.SelectedValue = SortMode;
        EditedBookmarks = true;
        _isAllFoldersSelected = true;
        _selectedFolderPath = string.Empty;
        ApplyBookmarkSort();
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"復元しました: {Bookmarks.Count} 件";
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var bookmarks = GetSelectedBookmarks();
        if (bookmarks.Count == 0)
        {
            StatusText.Text = "削除するブックマークを選択してください";
            return;
        }

        foreach (var bookmark in bookmarks)
        {
            Bookmarks.Remove(bookmark);
        }

        DeletedBookmarks = true;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = bookmarks.Count == 1
            ? $"{bookmarks[0].Title} を削除しました"
            : $"{bookmarks.Count} 件のブックマークを削除しました";
    }

    private async void MoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var bookmarks = GetSelectedBookmarks();
        if (bookmarks.Count == 0)
        {
            StatusText.Text = "移動するブックマークを選択してください";
            return;
        }

        var dialog = new BookmarkFolderInputWindow(
            "ブックマーク移動",
            $"{bookmarks.Count} 件の移動先フォルダを選択または入力してください。空欄にするとフォルダなしへ移動します。",
            GetInitialSelectedBookmarkMoveFolderPath(bookmarks),
            allowEmptyValue: true,
            candidateFolderPaths: GetFolderCandidatePaths())
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        bookmarks = bookmarks.Where(Bookmarks.Contains).ToList();
        if (bookmarks.Count == 0)
        {
            StatusText.Text = "移動対象のブックマークは既にありません";
            return;
        }

        var destinationFolderPath = BookmarkService.NormalizeFolderPath(dialog.EditedValue);
        if (bookmarks.All(bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath)
                .Equals(destinationFolderPath, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = "移動先は変更されていません";
            return;
        }

        MoveBookmarksToFolder(bookmarks, destinationFolderPath);
    }

    private void MergeDuplicatesButton_Click(object sender, RoutedEventArgs e)
    {
        var duplicateGroups = FindDuplicateBookmarkGroups();
        if (duplicateGroups.Count == 0)
        {
            StatusText.Text = "重複ブックマークはありません";
            return;
        }

        var removeCount = duplicateGroups.Sum(static group => group.Count - 1);
        var result = MessageBox.Show(
            this,
            $"重複 URL が {duplicateGroups.Count} グループあります。\n{removeCount} 件を統合して削除しますか？",
            "重複ブックマークの統合",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            StatusText.Text = "重複整理をキャンセルしました";
            return;
        }

        foreach (var group in duplicateGroups)
        {
            MergeDuplicateBookmarkGroup(group);
        }

        EditedBookmarks = true;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"{duplicateGroups.Count} グループ、{removeCount} 件の重複ブックマークを統合しました";
    }

    private async void CheckBookmarksButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCheckingBookmarks)
        {
            _bookmarkCheckCancellation?.Cancel();
            StatusText.Text = "存在確認をキャンセルしています";
            return;
        }

        var targets = Bookmarks
            .Where(static bookmark => !string.IsNullOrWhiteSpace(bookmark.Url))
            .ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "確認するブックマークがありません";
            return;
        }

        _isCheckingBookmarks = true;
        _bookmarkCheckCancellation = new CancellationTokenSource();
        CheckBookmarksButton.Content = "確認中止";
        MergeDuplicatesButton.IsEnabled = false;
        var completed = 0;
        var exists = 0;
        var missing = 0;
        var restricted = 0;
        var failed = 0;

        foreach (var bookmark in targets)
        {
            bookmark.CheckStatusText = "待機";
            bookmark.CheckStatusDetail = string.Empty;
        }

        BookmarkView.Refresh();
        StatusText.Text = $"{targets.Count} 件の存在確認を開始しました";

        try
        {
            using var semaphore = new SemaphoreSlim(BookmarkCheckConcurrency);
            var tasks = targets.Select(async bookmark =>
            {
                await semaphore.WaitAsync(_bookmarkCheckCancellation.Token);
                try
                {
                    await Dispatcher.InvokeAsync(() => bookmark.CheckStatusText = "確認中");
                    var result = await CheckBookmarkAsync(bookmark.Url, _bookmarkCheckCancellation.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        bookmark.CheckStatusText = result.StatusText;
                        bookmark.CheckStatusDetail = result.Detail;
                        completed++;
                        exists += result.Kind == BookmarkCheckKind.Exists ? 1 : 0;
                        missing += result.Kind == BookmarkCheckKind.Missing ? 1 : 0;
                        restricted += result.Kind == BookmarkCheckKind.Restricted ? 1 : 0;
                        failed += result.Kind == BookmarkCheckKind.Failed ? 1 : 0;
                        StatusText.Text = $"確認中 {completed} / {targets.Count} 件  存在 {exists} / なし {missing} / 制限 {restricted} / 失敗 {failed}";
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            StatusText.Text = $"存在確認完了: 存在 {exists} 件、なし {missing} 件、制限あり {restricted} 件、失敗 {failed} 件";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"存在確認をキャンセルしました: {completed} / {targets.Count} 件確認済み";
        }
        finally
        {
            _bookmarkCheckCancellation?.Dispose();
            _bookmarkCheckCancellation = null;
            _isCheckingBookmarks = false;
            CheckBookmarksButton.Content = "存在確認";
            MergeDuplicatesButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyWindowSize(BookmarkManagerWindowSizeSettings? windowSize)
    {
        if (windowSize is null
            || !IsValidWindowLength(windowSize.Width)
            || !IsValidWindowLength(windowSize.Height))
        {
            return;
        }

        Width = Math.Max(MinWidth, windowSize.Width);
        Height = Math.Max(MinHeight, windowSize.Height);
    }

    private void CaptureWindowSize()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (!IsValidWindowLength(bounds.Width) || !IsValidWindowLength(bounds.Height))
        {
            return;
        }

        CapturedWindowSize = new BookmarkManagerWindowSizeSettings
        {
            Width = Math.Max(MinWidth, bounds.Width),
            Height = Math.Max(MinHeight, bounds.Height)
        };
    }

    private static bool IsValidWindowLength(double value)
    {
        return double.IsFinite(value) && value >= 1 && value < 20000;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CaptureWindowSize();
        base.OnClosing(e);
    }

    private void OpenSelectedBookmark()
    {
        var bookmarks = GetSelectedBookmarks();
        if (bookmarks.Count == 0)
        {
            StatusText.Text = "開くブックマークを選択してください";
            return;
        }

        if (bookmarks.Count > 1)
        {
            StatusText.Text = "開くブックマークは1件だけ選択してください";
            return;
        }

        SelectedBookmark = bookmarks[0];
        IsAccepted = true;
        Close();
    }

    private async Task EditSelectedBookmarkAsync()
    {
        var bookmarks = GetSelectedBookmarks();
        if (bookmarks.Count == 0)
        {
            StatusText.Text = "編集するブックマークを選択してください";
            return;
        }

        if (bookmarks.Count > 1)
        {
            StatusText.Text = "編集するブックマークは1件だけ選択してください";
            return;
        }

        var bookmark = bookmarks[0];
        var dialog = new BookmarkEditWindow(bookmark)
        {
            Owner = this
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted || !IsLoaded)
        {
            return;
        }

        if (!Bookmarks.Contains(bookmark))
        {
            StatusText.Text = "編集対象のブックマークは既にありません";
            return;
        }

        var normalizedUrl = BookmarkService.NormalizeUrl(dialog.EditedUrl);
        var duplicate = Bookmarks.Any(x =>
            x != bookmark
            && BookmarkService.UrlComparer.Equals(x.Url, normalizedUrl));
        if (duplicate)
        {
            StatusText.Text = "同じ URL のブックマークが既にあります";
            return;
        }

        bookmark.Title = dialog.EditedTitle;
        bookmark.Url = normalizedUrl;
        bookmark.FolderPath = dialog.EditedFolderPath;
        EditedBookmarks = true;
        _isAllFoldersSelected = false;
        _selectedFolderPath = bookmark.FolderPath;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        BookmarkGrid.SelectedItem = bookmark;
        StatusText.Text = "ブックマークを更新しました";
    }

    private bool FilterBookmark(object item)
    {
        if (item is not BookmarkItem bookmark)
        {
            return false;
        }

        if (!MatchesFolderFilter(bookmark))
        {
            return false;
        }

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return bookmark.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (bookmark.FolderPath ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || bookmark.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<BookmarkCheckResult> CheckBookmarkAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Failed, "未対応", "HTTP/HTTPS 以外の URL です");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BookmarkCheckTimeout);

        try
        {
            var response = await SendBookmarkCheckRequestAsync(uri, HttpMethod.Head, timeout.Token);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                response = await SendBookmarkCheckRequestAsync(uri, HttpMethod.Get, timeout.Token);
            }

            using (response)
            {
                return CreateBookmarkCheckResult(response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Failed, "失敗", "タイムアウト");
        }
        catch (HttpRequestException ex)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Failed, "失敗", ex.Message);
        }
        catch (Exception ex)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Failed, "失敗", ex.Message);
        }
    }

    private static async Task<HttpResponseMessage> SendBookmarkCheckRequestAsync(
        Uri uri,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("TabBrowser/1.0");
        return await BookmarkCheckHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static BookmarkCheckResult CreateBookmarkCheckResult(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code is >= 200 and < 400)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Exists, "存在", $"{code} {statusCode}");
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Restricted, "制限あり", $"{code} {statusCode}");
        }

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new BookmarkCheckResult(BookmarkCheckKind.Missing, "なし", $"{code} {statusCode}");
        }

        return new BookmarkCheckResult(BookmarkCheckKind.Failed, "失敗", $"{code} {statusCode}");
    }

    private List<BookmarkItem> GetSelectedBookmarks()
    {
        return BookmarkGrid.SelectedItems
            .OfType<BookmarkItem>()
            .ToList();
    }

    private List<List<BookmarkItem>> FindDuplicateBookmarkGroups()
    {
        return Bookmarks
            .Where(static bookmark => !string.IsNullOrWhiteSpace(bookmark.Url))
            .GroupBy(static bookmark => BookmarkService.NormalizeUrl(bookmark.Url), BookmarkService.UrlComparer)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.ToList())
            .ToList();
    }

    private void MergeDuplicateBookmarkGroup(IReadOnlyList<BookmarkItem> group)
    {
        var primary = group
            .OrderByDescending(static bookmark => bookmark.OpenCount)
            .ThenByDescending(static bookmark => bookmark.LastOpenedAt ?? bookmark.CreatedAt)
            .ThenByDescending(static bookmark => string.IsNullOrWhiteSpace(bookmark.FolderPath) ? 0 : 1)
            .First();

        primary.OpenCount = group.Sum(static bookmark => bookmark.OpenCount);
        primary.CreatedAt = group.Min(static bookmark => bookmark.CreatedAt);
        primary.LastOpenedAt = group
            .Select(static bookmark => bookmark.LastOpenedAt)
            .Where(static value => value is not null)
            .DefaultIfEmpty()
            .Max();

        if (string.IsNullOrWhiteSpace(primary.Title))
        {
            primary.Title = group.FirstOrDefault(static bookmark => !string.IsNullOrWhiteSpace(bookmark.Title))?.Title
                ?? primary.Url;
        }

        if (string.IsNullOrWhiteSpace(primary.FolderPath))
        {
            primary.FolderPath = group.FirstOrDefault(static bookmark => !string.IsNullOrWhiteSpace(bookmark.FolderPath))?.FolderPath
                ?? string.Empty;
        }

        foreach (var duplicate in group.Where(bookmark => bookmark != primary).ToList())
        {
            Bookmarks.Remove(duplicate);
        }
    }

    private void MoveBookmarksToFolder(IReadOnlyList<BookmarkItem> bookmarks, string folderPath)
    {
        var destinationFolderPath = BookmarkService.NormalizeFolderPath(folderPath);
        if (bookmarks.Count == 0)
        {
            return;
        }

        if (bookmarks.All(bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath)
                .Equals(destinationFolderPath, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = "移動先は変更されていません";
            return;
        }

        foreach (var bookmark in bookmarks)
        {
            bookmark.FolderPath = destinationFolderPath;
        }

        EnsureFolderPath(destinationFolderPath);
        EditedBookmarks = true;
        _isAllFoldersSelected = false;
        _selectedFolderPath = destinationFolderPath;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = string.IsNullOrWhiteSpace(destinationFolderPath)
            ? $"{bookmarks.Count} 件のブックマークをフォルダなしへ移動しました"
            : $"{bookmarks.Count} 件のブックマークを「{destinationFolderPath}」へ移動しました";
    }

    private void RefreshBookmarkFolders()
    {
        var wasAllSelected = _isAllFoldersSelected;
        var selectedFolderPath = _selectedFolderPath;

        _isRefreshingBookmarkFolders = true;
        try
        {
            BookmarkFolders.Clear();
            BookmarkFolders.Add(new BookmarkFolderFilterItem("すべて", null, 0, Bookmarks.Count, isAll: true));

            var emptyFolderCount = Bookmarks.Count(static x => string.IsNullOrWhiteSpace(x.FolderPath));
            if (emptyFolderCount > 0)
            {
                BookmarkFolders.Add(new BookmarkFolderFilterItem("フォルダなし", string.Empty, 0, emptyFolderCount));
            }

            foreach (var folder in BuildFolderFilters())
            {
                BookmarkFolders.Add(folder);
            }

            var selectedFolder = wasAllSelected
                ? BookmarkFolders.FirstOrDefault(static x => x.IsAll)
                : BookmarkFolders.FirstOrDefault(x => string.Equals(x.FolderPath, selectedFolderPath, StringComparison.CurrentCultureIgnoreCase));

            selectedFolder ??= BookmarkFolders.FirstOrDefault();
            FolderList.SelectedItem = selectedFolder;
            ApplyFolderFilter(selectedFolder);
            UpdateFolderActionButtons(selectedFolder);
        }
        finally
        {
            _isRefreshingBookmarkFolders = false;
        }
    }

    private IEnumerable<BookmarkFolderFilterItem> BuildFolderFilters()
    {
        var folderCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var bookmark in Bookmarks)
        {
            var folderPath = BookmarkService.NormalizeFolderPath(bookmark.FolderPath);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                continue;
            }

            var currentPath = string.Empty;
            foreach (var segment in SplitFolderPath(folderPath))
            {
                currentPath = string.IsNullOrWhiteSpace(currentPath)
                    ? segment
                    : $"{currentPath} / {segment}";
                folderCounts[currentPath] = folderCounts.TryGetValue(currentPath, out var count)
                    ? count + 1
                : 1;
            }
        }

        var folderPaths = Folders
            .Select(static folder => BookmarkService.NormalizeFolderPath(folder.Path))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Concat(folderCounts.Keys)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new
            {
                Path = path,
                SortOrder = GetFolderSortOrder(path)
            })
            .OrderBy(static x => x.SortOrder)
            .ThenBy(static x => x.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return folderPaths
            .Select(x =>
            {
                var segments = SplitFolderPath(x.Path);
                return new BookmarkFolderFilterItem(
                    segments.LastOrDefault() ?? x.Path,
                    x.Path,
                    Math.Max(0, segments.Count - 1),
                    folderCounts.GetValueOrDefault(x.Path),
                    Bookmarks);
            })
            .ToList();
    }

    private void ApplyFolderFilter(BookmarkFolderFilterItem? folder)
    {
        _isAllFoldersSelected = folder?.IsAll != false;
        _selectedFolderPath = folder?.FolderPath ?? string.Empty;
    }

    private bool MatchesFolderFilter(BookmarkItem bookmark)
    {
        if (_isAllFoldersSelected)
        {
            return true;
        }

        var folderPath = BookmarkService.NormalizeFolderPath(bookmark.FolderPath);
        if (string.IsNullOrWhiteSpace(_selectedFolderPath))
        {
            return string.IsNullOrWhiteSpace(folderPath);
        }

        return folderPath.Equals(_selectedFolderPath, StringComparison.CurrentCultureIgnoreCase)
            || folderPath.StartsWith(_selectedFolderPath + " / ", StringComparison.CurrentCultureIgnoreCase);
    }

    private static List<BookmarkItem> GetBookmarksInFolder(BookmarkFolderFilterItem folder)
    {
        if (!folder.CanDelete || string.IsNullOrWhiteSpace(folder.FolderPath))
        {
            return [];
        }

        var selectedFolderPath = folder.FolderPath;
        if (string.IsNullOrWhiteSpace(selectedFolderPath))
        {
            return [];
        }

        return folder.Bookmarks
            .Where(bookmark =>
            {
                var folderPath = BookmarkService.NormalizeFolderPath(bookmark.FolderPath);
                return folderPath.Equals(selectedFolderPath, StringComparison.CurrentCultureIgnoreCase)
                    || folderPath.StartsWith(selectedFolderPath + " / ", StringComparison.CurrentCultureIgnoreCase);
            })
            .ToList();
    }

    private void RenameOrMoveFolder(BookmarkFolderFilterItem folder, string newFolderPath, string statusMessage)
    {
        var bookmarks = GetBookmarksInFolder(folder);
        if (string.IsNullOrWhiteSpace(folder.FolderPath))
        {
            StatusText.Text = "更新するフォルダを選択してください";
            RefreshBookmarkFolders();
            BookmarkView.Refresh();
            return;
        }

        var oldFolderPath = folder.FolderPath;
        var folders = GetFolderRecordsInFolder(oldFolderPath);
        if (bookmarks.Count == 0 && folders.Count == 0)
        {
            StatusText.Text = "更新できるフォルダがありません";
            RefreshBookmarkFolders();
            BookmarkView.Refresh();
            return;
        }

        foreach (var folderRecord in folders)
        {
            var currentFolderPath = BookmarkService.NormalizeFolderPath(folderRecord.Path);
            var suffix = currentFolderPath.Length == oldFolderPath.Length
                ? string.Empty
                : currentFolderPath[oldFolderPath.Length..].TrimStart(' ', '/');
            folderRecord.Path = BookmarkService.NormalizeFolderPath(CombineFolderPath(newFolderPath, suffix));
        }

        foreach (var bookmark in bookmarks)
        {
            var currentFolderPath = BookmarkService.NormalizeFolderPath(bookmark.FolderPath);
            var suffix = currentFolderPath.Length == oldFolderPath.Length
                ? string.Empty
                : currentFolderPath[oldFolderPath.Length..].TrimStart(' ', '/');
            bookmark.FolderPath = BookmarkService.NormalizeFolderPath(CombineFolderPath(newFolderPath, suffix));
        }

        EditedBookmarks = true;
        _isAllFoldersSelected = false;
        _selectedFolderPath = BookmarkService.NormalizeFolderPath(newFolderPath);
        EnsureFolderPath(_selectedFolderPath);
        RemoveDuplicateFolderRecords();
        UpdateFolderSortOrders();
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"{statusMessage} (ブックマーク {bookmarks.Count} 件、フォルダ {folders.Count} 件)";
    }

    private void UpdateFolderActionButtons(BookmarkFolderFilterItem? folder)
    {
        var canEdit = folder?.CanEdit == true;
        FolderRenameButton.IsEnabled = canEdit;
        FolderMoveButton.IsEnabled = canEdit;
        FolderDeleteButton.IsEnabled = folder?.CanDelete == true;
    }

    private static IReadOnlyList<string> SplitFolderPath(string folderPath)
    {
        return folderPath
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static string GetParentFolderPath(string? folderPath)
    {
        var segments = SplitFolderPath(BookmarkService.NormalizeFolderPath(folderPath));
        return segments.Count <= 1
            ? string.Empty
            : string.Join(" / ", segments.Take(segments.Count - 1));
    }

    private static string GetLastFolderSegment(string? folderPath)
    {
        return SplitFolderPath(BookmarkService.NormalizeFolderPath(folderPath)).LastOrDefault() ?? string.Empty;
    }

    private string GetInitialSelectedBookmarkMoveFolderPath(IReadOnlyList<BookmarkItem> bookmarks)
    {
        var distinctFolderPaths = bookmarks
            .Select(static bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (distinctFolderPaths.Count == 1)
        {
            return distinctFolderPaths[0];
        }

        return _isAllFoldersSelected
            ? string.Empty
            : _selectedFolderPath;
    }

    private IReadOnlyList<string> GetFolderCandidatePaths()
    {
        return Folders
            .Select(static folder => BookmarkService.NormalizeFolderPath(folder.Path))
            .Concat(Bookmarks.Select(static bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath)))
            .Where(static folderPath => !string.IsNullOrWhiteSpace(folderPath))
            .Prepend(string.Empty)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(GetFolderSortOrder)
            .ThenBy(static folderPath => folderPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> GetFolderMoveCandidatePaths(BookmarkFolderFilterItem folder)
    {
        var sourceFolderPath = folder.FolderPath ?? string.Empty;
        return GetFolderCandidatePaths()
            .Where(candidatePath => !IsSameOrChildFolder(candidatePath, sourceFolderPath))
            .ToList();
    }

    private bool TryGetBookmarkDragItems(DragEventArgs e, out List<BookmarkItem> bookmarks)
    {
        bookmarks = [];
        if (!e.Data.GetDataPresent(BookmarkDragFormat)
            || e.Data.GetData(BookmarkDragFormat) is not Guid[] bookmarkIds
            || bookmarkIds.Length == 0)
        {
            return false;
        }

        var idSet = bookmarkIds.ToHashSet();
        bookmarks = Bookmarks
            .Where(bookmark => idSet.Contains(bookmark.Id))
            .ToList();
        return bookmarks.Count > 0;
    }

    private static bool TryGetDropBookmark(DragEventArgs e, out BookmarkItem bookmark)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is BookmarkItem item)
        {
            bookmark = item;
            return true;
        }

        bookmark = default!;
        return false;
    }

    private void ReorderBookmarks(IReadOnlyList<BookmarkItem> bookmarks, BookmarkItem targetBookmark)
    {
        var movingSet = bookmarks.ToHashSet();
        var movingBookmarks = Bookmarks
            .Where(movingSet.Contains)
            .ToList();
        if (movingBookmarks.Count == 0 || !Bookmarks.Contains(targetBookmark))
        {
            StatusText.Text = "移動するブックマークが見つかりません";
            return;
        }

        foreach (var bookmark in movingBookmarks)
        {
            Bookmarks.Remove(bookmark);
        }

        var targetIndex = Bookmarks.IndexOf(targetBookmark);
        if (targetIndex < 0)
        {
            targetIndex = Bookmarks.Count;
        }

        for (var i = 0; i < movingBookmarks.Count; i++)
        {
            Bookmarks.Insert(Math.Min(targetIndex + i, Bookmarks.Count), movingBookmarks[i]);
        }

        UpdateBookmarkSortOrders();
        SortMode = BookmarkSortMode.Manual;
        SortModeBox.SelectedValue = SortMode;
        EditedBookmarks = true;
        ApplyBookmarkSort();
        BookmarkView.Refresh();
        BookmarkGrid.SelectedItems.Clear();
        foreach (var bookmark in movingBookmarks)
        {
            BookmarkGrid.SelectedItems.Add(bookmark);
        }

        StatusText.Text = $"{movingBookmarks.Count} 件を移動しました";
    }

    private void UpdateBookmarkDropFeedback(BookmarkFolderFilterItem folder, int bookmarkCount)
    {
        ClearFolderDropFeedback(updateStatus: false);
        if (_bookmarkDropFeedbackFolder != folder)
        {
            ClearBookmarkDropFeedback(updateStatus: false);
            _bookmarkDropFeedbackFolder = folder;
        }

        folder.IsDragTarget = folder.CanAcceptBookmarkDrop;
        folder.IsDropRejected = !folder.CanAcceptBookmarkDrop;
        StatusText.Text = folder.CanAcceptBookmarkDrop
            ? $"{bookmarkCount} 件を「{folder.DisplayName}」へ移動"
            : "この場所にはドロップできません";
    }

    private void ClearBookmarkDropFeedback(bool updateStatus)
    {
        if (_bookmarkDropFeedbackFolder is null)
        {
            return;
        }

        _bookmarkDropFeedbackFolder.IsDragTarget = false;
        _bookmarkDropFeedbackFolder.IsDropRejected = false;
        _bookmarkDropFeedbackFolder = null;
        if (updateStatus)
        {
            UpdateStatus();
        }
    }

    private void UpdateFolderDropFeedback(string sourceFolderPath, BookmarkFolderFilterItem folder)
    {
        ClearBookmarkDropFeedback(updateStatus: false);
        if (_folderDropFeedbackFolder != folder)
        {
            ClearFolderDropFeedback(updateStatus: false);
            _folderDropFeedbackFolder = folder;
        }

        var canReorder = CanReorderFolder(sourceFolderPath, folder);
        folder.IsDragTarget = canReorder;
        folder.IsDropRejected = !canReorder;
        StatusText.Text = canReorder
            ? $"「{sourceFolderPath}」を「{folder.DisplayName}」の前へ移動"
            : "この場所にはフォルダを移動できません";
    }

    private void ClearFolderDropFeedback(bool updateStatus)
    {
        if (_folderDropFeedbackFolder is null)
        {
            return;
        }

        _folderDropFeedbackFolder.IsDragTarget = false;
        _folderDropFeedbackFolder.IsDropRejected = false;
        _folderDropFeedbackFolder = null;
        if (updateStatus)
        {
            UpdateStatus();
        }
    }

    private static bool TryGetDropFolder(DragEventArgs e, out BookmarkFolderFilterItem folder)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is BookmarkFolderFilterItem item)
        {
            folder = item;
            return true;
        }

        folder = default!;
        return false;
    }

    private static bool TryGetFolderDragItem(DragEventArgs e, out string folderPath)
    {
        if (e.Data.GetDataPresent(FolderDragFormat)
            && e.Data.GetData(FolderDragFormat) is string path
            && !string.IsNullOrWhiteSpace(path))
        {
            folderPath = BookmarkService.NormalizeFolderPath(path);
            return true;
        }

        folderPath = string.Empty;
        return false;
    }

    private bool CanReorderFolder(string sourceFolderPath, BookmarkFolderFilterItem targetFolder)
    {
        var sourcePath = BookmarkService.NormalizeFolderPath(sourceFolderPath);
        var targetPath = BookmarkService.NormalizeFolderPath(targetFolder.FolderPath);
        return targetFolder.CanEdit
            && !string.IsNullOrWhiteSpace(sourcePath)
            && !string.IsNullOrWhiteSpace(targetPath)
            && !sourcePath.Equals(targetPath, StringComparison.CurrentCultureIgnoreCase)
            && !targetPath.StartsWith(sourcePath + " / ", StringComparison.CurrentCultureIgnoreCase);
    }

    private void ReorderFolder(string sourceFolderPath, string targetFolderPath)
    {
        var sourcePath = BookmarkService.NormalizeFolderPath(sourceFolderPath);
        var targetPath = BookmarkService.NormalizeFolderPath(targetFolderPath);
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
        {
            StatusText.Text = "移動するフォルダを選択してください";
            return;
        }

        EnsureFolderPath(sourcePath);
        EnsureFolderPath(targetPath);

        var sourceBlock = Folders
            .Where(folder => IsSameOrChildFolder(folder.Path, sourcePath))
            .ToList();
        if (sourceBlock.Count == 0)
        {
            StatusText.Text = "移動するフォルダが見つかりません";
            return;
        }

        if (sourceBlock.Any(folder => folder.Path.Equals(targetPath, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = "同じフォルダ内では並び順は変更されません";
            return;
        }

        foreach (var folder in sourceBlock)
        {
            Folders.Remove(folder);
        }

        var targetIndex = Folders
            .Select((folder, index) => new { folder, index })
            .FirstOrDefault(x => x.folder.Path.Equals(targetPath, StringComparison.CurrentCultureIgnoreCase))
            ?.index ?? Folders.Count;

        for (var i = 0; i < sourceBlock.Count; i++)
        {
            Folders.Insert(Math.Min(targetIndex + i, Folders.Count), sourceBlock[i]);
        }

        UpdateFolderSortOrders();
        EditedBookmarks = true;
        _isAllFoldersSelected = false;
        _selectedFolderPath = sourcePath;
        RefreshBookmarkFolders();
        BookmarkView.Refresh();
        StatusText.Text = $"「{sourcePath}」を「{targetPath}」の前へ移動しました";
    }

    private void ApplyBookmarkSort()
    {
        using (BookmarkView.DeferRefresh())
        {
            BookmarkView.SortDescriptions.Clear();
            switch (SortMode)
            {
                case BookmarkSortMode.Title:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Title), ListSortDirection.Ascending));
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Url), ListSortDirection.Ascending));
                    break;
                case BookmarkSortMode.Folder:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.FolderPath), ListSortDirection.Ascending));
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Title), ListSortDirection.Ascending));
                    break;
                case BookmarkSortMode.Url:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Url), ListSortDirection.Ascending));
                    break;
                case BookmarkSortMode.OpenCount:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.OpenCount), ListSortDirection.Descending));
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Title), ListSortDirection.Ascending));
                    break;
                case BookmarkSortMode.Recent:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.LastOpenedAt), ListSortDirection.Descending));
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.CreatedAt), ListSortDirection.Descending));
                    break;
                case BookmarkSortMode.Manual:
                default:
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.SortOrder), ListSortDirection.Ascending));
                    BookmarkView.SortDescriptions.Add(new SortDescription(nameof(BookmarkItem.Title), ListSortDirection.Ascending));
                    break;
            }
        }
    }

    private BookmarkData CreateBookmarkData()
    {
        return new BookmarkData
        {
            Bookmarks = Bookmarks.ToList(),
            Folders = Folders.ToList(),
            SortMode = SortMode
        };
    }

    private int GetNextBookmarkSortOrder()
    {
        return Bookmarks.Count == 0
            ? 0
            : Bookmarks.Max(static bookmark => bookmark.SortOrder) + 1;
    }

    private void UpdateBookmarkSortOrders()
    {
        for (var i = 0; i < Bookmarks.Count; i++)
        {
            Bookmarks[i].SortOrder = i;
        }
    }

    private int GetFolderSortOrder(string folderPath)
    {
        var normalizedPath = BookmarkService.NormalizeFolderPath(folderPath);
        return Folders.FirstOrDefault(folder =>
            folder.Path.Equals(normalizedPath, StringComparison.CurrentCultureIgnoreCase))?.SortOrder ?? int.MaxValue;
    }

    private bool HasFolder(string folderPath)
    {
        var normalizedPath = BookmarkService.NormalizeFolderPath(folderPath);
        return Folders.Any(folder => folder.Path.Equals(normalizedPath, StringComparison.CurrentCultureIgnoreCase))
            || Bookmarks.Any(bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath)
                .Equals(normalizedPath, StringComparison.CurrentCultureIgnoreCase));
    }

    private void EnsureFolderPath(string? folderPath)
    {
        foreach (var path in BookmarkService.GetFolderAndParents(folderPath))
        {
            if (Folders.Any(folder => folder.Path.Equals(path, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            Folders.Add(new BookmarkFolderItem
            {
                Path = path,
                SortOrder = Folders.Count,
                CreatedAt = DateTimeOffset.Now
            });
        }

        UpdateFolderSortOrders();
    }

    private int CountFolderRecords(string folderPath)
    {
        return GetFolderRecordsInFolder(folderPath).Count;
    }

    private List<BookmarkFolderItem> GetFolderRecordsInFolder(string folderPath)
    {
        var normalizedPath = BookmarkService.NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return [];
        }

        return Folders
            .Where(folder => IsSameOrChildFolder(folder.Path, normalizedPath))
            .ToList();
    }

    private void RemoveFolderRecords(string folderPath)
    {
        foreach (var folder in GetFolderRecordsInFolder(folderPath))
        {
            Folders.Remove(folder);
        }

        UpdateFolderSortOrders();
    }

    private void RemoveDuplicateFolderRecords()
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in Folders.ToList())
        {
            folder.Path = BookmarkService.NormalizeFolderPath(folder.Path);
            if (string.IsNullOrWhiteSpace(folder.Path) || !seen.Add(folder.Path))
            {
                Folders.Remove(folder);
            }
        }
    }

    private void UpdateFolderSortOrders()
    {
        RemoveDuplicateFolderRecordsWithoutOrdering();
        for (var i = 0; i < Folders.Count; i++)
        {
            Folders[i].SortOrder = i;
        }
    }

    private void RemoveDuplicateFolderRecordsWithoutOrdering()
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in Folders.ToList())
        {
            folder.Path = BookmarkService.NormalizeFolderPath(folder.Path);
            if (string.IsNullOrWhiteSpace(folder.Path) || !seen.Add(folder.Path))
            {
                Folders.Remove(folder);
            }
        }
    }

    private static string CombineFolderPath(string? parentPath, string? childPath)
    {
        var parent = BookmarkService.NormalizeFolderPath(parentPath);
        var child = BookmarkService.NormalizeFolderPath(childPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return child;
        }

        if (string.IsNullOrWhiteSpace(child))
        {
            return parent;
        }

        return BookmarkService.NormalizeFolderPath($"{parent} / {child}");
    }

    private static bool IsSameOrChildFolder(string candidatePath, string folderPath)
    {
        var normalizedCandidate = BookmarkService.NormalizeFolderPath(candidatePath);
        var normalizedFolderPath = BookmarkService.NormalizeFolderPath(folderPath);
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && (normalizedCandidate.Equals(normalizedFolderPath, StringComparison.CurrentCultureIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedFolderPath + " / ", StringComparison.CurrentCultureIgnoreCase));
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void UpdateStatus()
    {
        var visibleCount = BookmarkView.Cast<object>().Count();
        var folderPrefix = _isAllFoldersSelected || FolderList.SelectedItem is not BookmarkFolderFilterItem folder
            ? string.Empty
            : $"{folder.DisplayName}: ";
        StatusText.Text = string.IsNullOrWhiteSpace(SearchBox?.Text) && _isAllFoldersSelected
            ? (Bookmarks.Count == 0 ? "ブックマークはありません" : $"{Bookmarks.Count} 件のブックマーク")
            : $"{folderPrefix}{visibleCount} / {Bookmarks.Count} 件を表示";
    }

    public sealed class BookmarkSortOption(string displayName, BookmarkSortMode mode)
    {
        public string DisplayName { get; } = displayName;

        public BookmarkSortMode Mode { get; } = mode;
    }

    private enum BookmarkCheckKind
    {
        Exists,
        Missing,
        Restricted,
        Failed
    }

    private sealed record BookmarkCheckResult(
        BookmarkCheckKind Kind,
        string StatusText,
        string Detail);

    public sealed class BookmarkFolderFilterItem(
        string displayName,
        string? folderPath,
        int level,
        int count,
        IReadOnlyList<BookmarkItem>? bookmarks = null,
        bool isAll = false) : INotifyPropertyChanged
    {
        private bool _isDragTarget;
        private bool _isDropRejected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DisplayName { get; } = displayName;

        public string? FolderPath { get; } = folderPath;

        public int Count { get; } = count;

        public IReadOnlyList<BookmarkItem> Bookmarks { get; } = bookmarks ?? [];

        public bool IsAll { get; } = isAll;

        public bool CanDelete => !IsAll && !string.IsNullOrWhiteSpace(FolderPath);

        public bool CanEdit => CanDelete;

        public bool CanAcceptBookmarkDrop => !IsAll;

        public string CountText => Count.ToString("N0");

        public Thickness IndentMargin => new(Math.Max(0, level) * 12, 0, 0, 0);

        public bool IsDragTarget
        {
            get => _isDragTarget;
            set
            {
                if (_isDragTarget == value)
                {
                    return;
                }

                _isDragTarget = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragTarget)));
            }
        }

        public bool IsDropRejected
        {
            get => _isDropRejected;
            set
            {
                if (_isDropRejected == value)
                {
                    return;
                }

                _isDropRejected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropRejected)));
            }
        }
    }
}

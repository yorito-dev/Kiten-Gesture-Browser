using System.Windows;
using TabBrowser.Services;

namespace TabBrowser;

public partial class BookmarkFolderInputWindow : Window
{
    private readonly bool _allowEmptyValue;

    public BookmarkFolderInputWindow(
        string title,
        string description,
        string initialValue,
        bool allowEmptyValue,
        IEnumerable<string>? candidateFolderPaths = null)
    {
        InitializeComponent();
        _allowEmptyValue = allowEmptyValue;
        Title = title;
        HeadingText.Text = title;
        DescriptionText.Text = description;
        ValueBox.Text = initialValue;
        LoadCandidateFolders(candidateFolderPaths);
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    public string EditedValue { get; private set; } = string.Empty;

    public bool IsAccepted { get; private set; }

    private void CandidateList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is FolderCandidate candidate)
        {
            ValueBox.Text = candidate.FolderPath;
            ValueBox.SelectAll();
            ValueBox.Focus();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var value = BookmarkService.NormalizeFolderPath(ValueBox.Text);
        if (!_allowEmptyValue && string.IsNullOrWhiteSpace(value))
        {
            StatusText.Text = "フォルダ名を入力してください";
            return;
        }

        EditedValue = value;
        IsAccepted = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        Close();
    }

    private void LoadCandidateFolders(IEnumerable<string>? candidateFolderPaths)
    {
        var candidates = candidateFolderPaths?
            .Select(BookmarkService.NormalizeFolderPath)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(static x => x, StringComparer.CurrentCultureIgnoreCase)
            .Select(static x => new FolderCandidate(
                string.IsNullOrWhiteSpace(x) ? "フォルダなし / ルート" : x,
                x))
            .ToList()
            ?? [];

        CandidateList.ItemsSource = candidates;
        CandidateHost.Visibility = candidates.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private sealed class FolderCandidate(string displayName, string folderPath)
    {
        public string DisplayName { get; } = displayName;

        public string FolderPath { get; } = folderPath;
    }
}

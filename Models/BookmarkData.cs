using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TabBrowser.Models;

public sealed class BookmarkData
{
    public int Version { get; set; } = 3;

    public List<BookmarkItem> Bookmarks { get; set; } = [];

    public List<BookmarkFolderItem> Folders { get; set; } = [];

    public BookmarkSortMode SortMode { get; set; } = BookmarkSortMode.Manual;
}

public sealed class BookmarkItem : INotifyPropertyChanged
{
    private string _checkStatusText = "未確認";
    private string _checkStatusDetail = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string FolderPath { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastOpenedAt { get; set; }

    public int OpenCount { get; set; }

    [JsonIgnore]
    public string CheckStatusText
    {
        get => _checkStatusText;
        set => SetField(ref _checkStatusText, value);
    }

    [JsonIgnore]
    public string CheckStatusDetail
    {
        get => _checkStatusDetail;
        set => SetField(ref _checkStatusDetail, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class BookmarkFolderItem
{
    public string Path { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public enum BookmarkSortMode
{
    Manual,
    Title,
    Folder,
    Url,
    OpenCount,
    Recent
}

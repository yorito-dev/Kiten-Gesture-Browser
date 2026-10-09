using System.ComponentModel;
using System.Windows.Media;

namespace TabBrowser.Models;

/// <summary>
/// BrowserTabがRuntimeの表示状態を読み取るための最小境界です。
/// </summary>
internal interface ITabRuntimePresentation : INotifyPropertyChanged
{
    bool IsLoaded { get; }

    bool IsLoading { get; }

    bool IsSuspended { get; }

    bool IsMemoryReleased { get; }

    ImageSource? Thumbnail { get; }

    ImageSource? Favicon { get; }
}

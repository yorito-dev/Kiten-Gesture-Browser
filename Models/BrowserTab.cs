using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace TabBrowser.Models;

public enum TabAudioPan
{
    Stereo = 0,
    Left = -1,
    Right = 1
}

public sealed class BrowserTab : INotifyPropertyChanged
{
    private static readonly Brush TransparentBrush = CreateFrozenBrush(Colors.Transparent);
    private static readonly Brush[] SplitViewAccentBrushes =
    [
        CreateFrozenBrush(Color.FromRgb(111, 168, 220)),
        CreateFrozenBrush(Color.FromRgb(102, 178, 163)),
        CreateFrozenBrush(Color.FromRgb(155, 138, 203)),
        CreateFrozenBrush(Color.FromRgb(121, 181, 141)),
        CreateFrozenBrush(Color.FromRgb(209, 163, 95)),
        CreateFrozenBrush(Color.FromRgb(200, 131, 145)),
        CreateFrozenBrush(Color.FromRgb(111, 155, 194)),
        CreateFrozenBrush(Color.FromRgb(165, 139, 199)),
        CreateFrozenBrush(Color.FromRgb(100, 165, 173)),
        CreateFrozenBrush(Color.FromRgb(160, 173, 111)),
        CreateFrozenBrush(Color.FromRgb(201, 139, 104)),
        CreateFrozenBrush(Color.FromRgb(126, 143, 188))
    ];

    public static int SplitViewAccentPaletteSize => SplitViewAccentBrushes.Length;

    private string? _url;
    private ITabRuntimePresentation? _runtimePresentation;
    private string? _title;
    private string _pageDescription = string.Empty;
    private bool _isDragSource;
    private bool _isDropPreviewBefore;
    private bool _isDropPreviewAfter;
    private bool _isFolderFileDropTarget;
    private string _faviconPngBase64 = string.Empty;
    private bool _isWorkspaceTab;
    private bool _isFolderTab;
    private bool _isMuted;
    private bool _isPlayingAudio;
    private int _audioBoostDecibels;
    private bool _isPinned;
    private string _containerProfileId = ContainerProfile.DefaultId;
    private string _containerProfileName = ContainerProfile.DefaultName;
    private string _containerProfileColor = ContainerProfile.DefaultColor;
    private string? _splitViewId;
    private int _splitViewAccentIndex = -1;
    private bool _isActiveSplitViewTab;
    private TabAudioPan _audioPan = TabAudioPan.Stereo;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();

    public List<string> NavigationHistory { get; set; } = [];

    public int NavigationHistoryIndex { get; set; } = -1;

    public bool PreferSavedNavigationHistory { get; set; }

    public DateTimeOffset LastActivatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsMemoryReleased => _runtimePresentation?.IsMemoryReleased == true;

    public double? MemoryRestoreScrollX { get; set; }

    public double? MemoryRestoreScrollY { get; set; }

    public string? MemoryRestoreUrl { get; set; }

    internal int PendingSavedNavigationHistoryIndex { get; set; } = -1;

    public string? Url
    {
        get => _url;
        set
        {
            if (_url == value)
            {
                return;
            }

            _url = value;
            if (_pageDescription.Length > 0)
            {
                _pageDescription = string.Empty;
                OnPropertyChanged(nameof(PageDescription));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string? Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string PageDescription
    {
        get => _pageDescription;
        set
        {
            var normalized = value ?? string.Empty;
            if (_pageDescription == normalized)
            {
                return;
            }

            _pageDescription = normalized;
            OnPropertyChanged();
        }
    }

    public bool IsLoaded => _runtimePresentation?.IsLoaded == true;

    public bool IsLoading => _runtimePresentation?.IsLoading == true;

    public bool IsSuspended => _runtimePresentation?.IsSuspended == true;

    public bool IsDragSource
    {
        get => _isDragSource;
        set
        {
            if (_isDragSource == value)
            {
                return;
            }

            _isDragSource = value;
            OnPropertyChanged();
        }
    }

    public bool IsDropPreviewBefore
    {
        get => _isDropPreviewBefore;
        set
        {
            if (_isDropPreviewBefore == value)
            {
                return;
            }

            _isDropPreviewBefore = value;
            OnPropertyChanged();
        }
    }

    public bool IsDropPreviewAfter
    {
        get => _isDropPreviewAfter;
        set
        {
            if (_isDropPreviewAfter == value)
            {
                return;
            }

            _isDropPreviewAfter = value;
            OnPropertyChanged();
        }
    }

    public bool IsFolderFileDropTarget
    {
        get => _isFolderFileDropTarget;
        set
        {
            if (_isFolderFileDropTarget == value)
            {
                return;
            }

            _isFolderFileDropTarget = value;
            OnPropertyChanged();
        }
    }

    internal void BindRuntimePresentation(ITabRuntimePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        var current = Interlocked.CompareExchange(ref _runtimePresentation, presentation, null);
        if (current is not null && !ReferenceEquals(current, presentation))
        {
            throw new InvalidOperationException("BrowserTabには異なるTabRuntimeを再関連付けできません。");
        }

        if (current is null)
        {
            try
            {
                PropertyChangedEventManager.AddHandler(
                    presentation,
                    HandleRuntimePresentationPropertyChanged,
                    string.Empty);
            }
            catch
            {
                _ = Interlocked.CompareExchange(ref _runtimePresentation, null, presentation);
                throw;
            }
        }
    }

    private void HandleRuntimePresentationPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        _ = sender;
        switch (args.PropertyName)
        {
            case nameof(IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(DisplayTitle));
                break;
            case nameof(Thumbnail):
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(HasThumbnail));
                break;
            case nameof(Favicon):
                OnPropertyChanged(nameof(Favicon));
                OnPropertyChanged(nameof(HasFavicon));
                break;
            case nameof(IsLoaded):
            case nameof(IsSuspended):
            case nameof(IsMemoryReleased):
                OnPropertyChanged(args.PropertyName);
                break;
        }
    }

    public bool IsWorkspaceTab
    {
        get => _isWorkspaceTab;
        set
        {
            if (_isWorkspaceTab == value)
            {
                return;
            }

            _isWorkspaceTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNativeContentTab));
            OnPropertyChanged(nameof(NativeTabBadgeText));
            OnPropertyChanged(nameof(NativeTabBadgeVisibility));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(MuteButtonVisibility));
            OnPropertyChanged(nameof(CanSetAudioPan));
            OnPropertyChanged(nameof(CanRequestSplitView));
            OnPropertyChanged(nameof(ContainerProfileBadgeVisibility));
        }
    }

    public bool IsFolderTab
    {
        get => _isFolderTab;
        set
        {
            if (_isFolderTab == value)
            {
                return;
            }

            _isFolderTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNativeContentTab));
            OnPropertyChanged(nameof(NativeTabBadgeText));
            OnPropertyChanged(nameof(NativeTabBadgeVisibility));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(MuteButtonVisibility));
            OnPropertyChanged(nameof(CanSetAudioPan));
            OnPropertyChanged(nameof(CanRequestSplitView));
            OnPropertyChanged(nameof(ContainerProfileBadgeVisibility));
        }
    }

    public bool IsNativeContentTab => IsWorkspaceTab || IsFolderTab;

    public string NativeTabBadgeText => IsFolderTab ? "DIR" : "WS";

    public Visibility NativeTabBadgeVisibility => IsNativeContentTab
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value)
            {
                return;
            }

            _isMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteButtonText));
            OnPropertyChanged(nameof(MuteButtonToolTip));
            OnPropertyChanged(nameof(MuteButtonVisibility));
        }
    }

    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        set
        {
            if (_isPlayingAudio == value)
            {
                return;
            }

            _isPlayingAudio = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteButtonVisibility));
        }
    }

    public int AudioBoostDecibels
    {
        get => _audioBoostDecibels;
        set
        {
            var normalized = AudioBoostProfile.Normalize(value);
            if (_audioBoostDecibels == normalized)
            {
                return;
            }

            _audioBoostDecibels = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioBoostBadgeVisibility));
            OnPropertyChanged(nameof(MuteButtonToolTip));
            OnPropertyChanged(nameof(MuteButtonVisibility));
        }
    }

    public TabAudioPan AudioPan
    {
        get => _audioPan;
        set
        {
            if (_audioPan == value)
            {
                return;
            }

            _audioPan = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAudioPanStereo));
            OnPropertyChanged(nameof(IsAudioPanLeft));
            OnPropertyChanged(nameof(IsAudioPanRight));
            OnPropertyChanged(nameof(AudioPanStatusText));
        }
    }

    public bool IsAudioPanStereo => AudioPan == TabAudioPan.Stereo;

    public bool IsAudioPanLeft => AudioPan == TabAudioPan.Left;

    public bool IsAudioPanRight => AudioPan == TabAudioPan.Right;

    public bool CanSetAudioPan => !IsNativeContentTab;

    public bool CanRequestSplitView => IsFolderTab || !IsNativeContentTab;

    public bool IsAudioPanScriptInjected { get; set; }

    public string ContainerProfileId
    {
        get => _containerProfileId;
        set
        {
            var normalized = ContainerProfile.NormalizeId(value);
            if (string.Equals(_containerProfileId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _containerProfileId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDefaultContainerProfile));
            OnPropertyChanged(nameof(ContainerProfileBadgeVisibility));
            OnPropertyChanged(nameof(ContainerProfileToolTip));
        }
    }

    public string ContainerProfileName
    {
        get => _containerProfileName;
        private set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? ContainerProfile.DefaultName
                : value.Trim();
            if (string.Equals(_containerProfileName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _containerProfileName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContainerProfileToolTip));
        }
    }

    public string ContainerProfileColor
    {
        get => _containerProfileColor;
        private set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? ContainerProfile.DefaultColor
                : value.Trim();
            if (string.Equals(_containerProfileColor, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _containerProfileColor = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContainerProfileBrush));
            OnPropertyChanged(nameof(ContainerProfilePatternBrush));
        }
    }

    public bool IsDefaultContainerProfile => string.Equals(
        ContainerProfileId,
        ContainerProfile.DefaultId,
        StringComparison.OrdinalIgnoreCase);

    public Visibility ContainerProfileBadgeVisibility => !IsNativeContentTab && !IsDefaultContainerProfile
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Brush ContainerProfileBrush => CreateFrozenBrush(ParseContainerProfileColor(ContainerProfileColor));

    public Brush ContainerProfilePatternBrush => CreateContainerProfilePatternBrush(ParseContainerProfileColor(ContainerProfileColor));

    public string ContainerProfileToolTip => IsDefaultContainerProfile
        ? "コンテナ: 既定"
        : $"コンテナ: {ContainerProfileName}";

    public void ApplyContainerProfile(ContainerProfile profile)
    {
        ContainerProfileId = profile.Id;
        ContainerProfileName = profile.Name;
        ContainerProfileColor = profile.Color;
    }

    public string? SplitViewId
    {
        get => _splitViewId;
        set
        {
            if (_splitViewId == value)
            {
                return;
            }

            _splitViewId = value;
            SplitViewAccentIndex = -1;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInSplitView));
            OnPropertyChanged(nameof(SplitViewBadgeVisibility));
            OnPropertyChanged(nameof(SplitViewActiveMarkerVisibility));
            OnPropertyChanged(nameof(SplitViewAccentBrush));
        }
    }

    public string? SplitViewOrientation { get; set; }

    public double? SplitViewRatio { get; set; }

    public bool IsInSplitView => !string.IsNullOrWhiteSpace(SplitViewId);

    public int SplitViewAccentIndex
    {
        get => _splitViewAccentIndex;
        set
        {
            var normalized = value < 0
                ? -1
                : value % SplitViewAccentBrushes.Length;
            if (_splitViewAccentIndex == normalized)
            {
                return;
            }

            _splitViewAccentIndex = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SplitViewAccentBrush));
        }
    }

    public bool IsActiveSplitViewTab
    {
        get => _isActiveSplitViewTab;
        set
        {
            if (_isActiveSplitViewTab == value)
            {
                return;
            }

            _isActiveSplitViewTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SplitViewActiveMarkerVisibility));
        }
    }

    public Visibility SplitViewBadgeVisibility => IsInSplitView
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SplitViewActiveMarkerVisibility => IsActiveSplitViewTab
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Brush SplitViewAccentBrush => IsInSplitView
        ? SplitViewAccentBrushes[GetSplitViewPaletteIndex()]
        : TransparentBrush;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinButtonText));
            OnPropertyChanged(nameof(PinButtonToolTip));
        }
    }

    public string PinButtonText => IsPinned ? "解除" : "固定";

    public string PinButtonToolTip => IsPinned ? "固定を解除" : "タブを固定";

    public string? YouTubePlaybackVideoId { get; set; }

    public double? YouTubePlaybackPositionSeconds { get; set; }

    public double? YouTubePlaybackSpeed { get; set; }

    public double? PreviousYouTubePlaybackSpeed { get; set; }

    public bool? YouTubePlaybackIsPaused { get; set; }

    public bool? YouTubePlaybackIsLooping { get; set; }

    public bool? YouTubePlaybackIsTheaterMode { get; set; }

    public bool IsYouTubeLoadStoppedForInactivity { get; set; }

    public string AudioPanStatusText => AudioPan switch
    {
        TabAudioPan.Left => "左",
        TabAudioPan.Right => "右",
        _ => "ステレオ"
    };

    public string MuteButtonText => IsMuted ? "消" : "音";

    public string MuteButtonToolTip => IsMuted
        ? "ミュートを解除（右クリックで音量ブースト）"
        : "このタブをミュート（右クリックで音量ブースト）";

    public Visibility MuteButtonVisibility =>
        !IsNativeContentTab && (IsPlayingAudio || IsMuted || AudioBoostDecibels > 0)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AudioBoostBadgeVisibility => AudioBoostDecibels > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public ImageSource? Thumbnail => _runtimePresentation?.Thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    public ImageSource? Favicon => _runtimePresentation?.Favicon;

    public string FaviconPngBase64
    {
        get => _faviconPngBase64;
        set
        {
            var normalized = value ?? string.Empty;
            if (_faviconPngBase64 == normalized)
            {
                return;
            }

            _faviconPngBase64 = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFavicon));
        }
    }

    public bool HasFavicon => Favicon is not null || !string.IsNullOrWhiteSpace(FaviconPngBase64);

    public string DisplayTitle
    {
        get
        {
            if (IsWorkspaceTab)
            {
                return string.IsNullOrWhiteSpace(Title)
                    ? "Workspace OS"
                    : Title;
            }

            if (IsLoading && !string.IsNullOrWhiteSpace(Title))
            {
                return Title + " ...";
            }

            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title;
            }

            if (!string.IsNullOrWhiteSpace(Url))
            {
                return TryGetHost(Url) ?? Url;
            }

            return "新しいタブ";
        }
    }

    public SessionTabState ToSessionState()
    {
        return BrowserTabStateMapper.ToSessionTabState(BrowserTabStateMapper.Capture(this));
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private int GetSplitViewPaletteIndex()
    {
        if (SplitViewAccentIndex >= 0)
        {
            return SplitViewAccentIndex;
        }

        if (string.IsNullOrWhiteSpace(SplitViewId))
        {
            return 0;
        }

        unchecked
        {
            var hash = 17;
            foreach (var character in SplitViewId)
            {
                hash = (hash * 31) + character;
            }

            return (hash & int.MaxValue) % SplitViewAccentBrushes.Length;
        }
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateContainerProfilePatternBrush(Color color)
    {
        var fillBrush = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B));
        fillBrush.Freeze();

        var stripeBrush = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B));
        stripeBrush.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(fillBrush, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        drawing.Children.Add(new GeometryDrawing(stripeBrush, null, CreateContainerProfileStripeGeometry()));
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 12, 12),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private static Geometry CreateContainerProfileStripeGeometry()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(-2, 12), true, true);
            context.LineTo(new Point(1, 12), true, false);
            context.LineTo(new Point(13, 0), true, false);
            context.LineTo(new Point(10, 0), true, false);

            context.BeginFigure(new Point(10, 12), true, true);
            context.LineTo(new Point(13, 12), true, false);
            context.LineTo(new Point(25, 0), true, false);
            context.LineTo(new Point(22, 0), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Color ParseContainerProfileColor(string color)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(color);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString(ContainerProfile.DefaultColor);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

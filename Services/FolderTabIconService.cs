using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal sealed class FolderTabIconService
{
    internal const int MaximumCacheEntries = 512;
    internal const int MaximumConcurrentLoads = 4;
    internal const int ThumbnailWidth = 100;
    internal const int ThumbnailHeight = 52;

    private const uint ShellGetFileInfoIcon = 0x00000100;
    private const uint ShellGetFileInfoAddOverlays = 0x00000020;
    private const uint ShellGetFileInfoLargeIcon = 0x00000000;
    private const uint ShellGetFileInfoSmallIcon = 0x00000001;
    private const uint ShellGetFileInfoUseFileAttributes = 0x00000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const ShellItemImageFlags ApplicationIconFlags =
        ShellItemImageFlags.BiggerSizeOk | ShellItemImageFlags.IconOnly;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> CacheLru = [];
    private static readonly SemaphoreSlim LoadGate = new(MaximumConcurrentLoads);

    public async Task<ImageSource?> GetIconAsync(string path, CancellationToken cancellationToken)
    {
        return (await GetVisualAsync(path, cancellationToken)).Image;
    }

    public async Task<FolderTabVisual> GetVisualAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FolderTabVisual.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCachedVisual(path, out var cached))
        {
            return cached;
        }

        await LoadGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedVisual(path, out cached))
            {
                return cached;
            }

            var thumbnail = WorkspaceShellItemService.IsMediaPath(path)
                ? await WorkspaceShellItemService.GetMediaThumbnailImageAsync(
                    path,
                    ThumbnailWidth,
                    ThumbnailHeight,
                    cancellationToken)
                : null;
            var visual = thumbnail is not null
                ? new FolderTabVisual(thumbnail, true)
                : new FolderTabVisual(
                    await Task.Run(() => LoadShellIcon(path), CancellationToken.None),
                    false);
            cancellationToken.ThrowIfCancellationRequested();
            if (visual.Image is not null)
            {
                AddCachedVisual(path, visual);
            }

            return visual;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    public async Task<ImageSource?> GetTypeIconAsync(
        string path,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = GetTypeIconCacheKey(path, isDirectory);
        if (TryGetCachedVisual(cacheKey, out var cached))
        {
            return cached.Image;
        }

        await LoadGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedVisual(cacheKey, out cached))
            {
                return cached.Image;
            }

            var icon = await Task.Run(
                () => LoadShellTypeIcon(path, isDirectory),
                CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (icon is not null)
            {
                AddCachedVisual(cacheKey, new FolderTabVisual(icon, false));
            }

            return icon;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    internal static string GetTypeIconCacheKey(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "type|directory";
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension)
            ? "type|file"
            : $"type|{extension.ToLowerInvariant()}";
    }

    private static ImageSource? LoadShellIcon(string path)
    {
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            && LoadShellItemIcon(path) is { } shellItemIcon)
        {
            return shellItemIcon;
        }

        var result = SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetFileInfoIcon | ShellGetFileInfoAddOverlays | ShellGetFileInfoLargeIcon);
        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(fileInfo.IconHandle);
        }
    }

    private static ImageSource? LoadShellTypeIcon(string path, bool isDirectory)
    {
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var result = SHGetFileInfo(
            path,
            attributes,
            out var fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetFileInfoIcon | ShellGetFileInfoSmallIcon | ShellGetFileInfoUseFileAttributes);
        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(fileInfo.IconHandle);
        }
    }

    private static bool TryGetCachedVisual(string path, out FolderTabVisual visual)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(path, out var entry))
            {
                visual = FolderTabVisual.Empty;
                return false;
            }

            CacheLru.Remove(entry.Node);
            CacheLru.AddFirst(entry.Node);
            visual = entry.Visual;
            return true;
        }
    }

    private static void AddCachedVisual(string path, FolderTabVisual visual)
    {
        lock (CacheGate)
        {
            if (Cache.Remove(path, out var existing))
            {
                CacheLru.Remove(existing.Node);
            }

            var node = CacheLru.AddFirst(path);
            Cache[path] = new CacheEntry(visual, node);
            while (Cache.Count > MaximumCacheEntries && CacheLru.Last is { } oldest)
            {
                Cache.Remove(oldest.Value);
                CacheLru.RemoveLast();
            }
        }
    }

    private static ImageSource? LoadShellItemIcon(string path)
    {
        var interfaceId = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(
            path,
            IntPtr.Zero,
            ref interfaceId,
            out var imageFactory);
        if (result < 0 || imageFactory is null)
        {
            return null;
        }

        try
        {
            result = imageFactory.GetImage(
                new ShellSize(48, 48),
                ApplicationIconFlags,
                out var bitmapHandle);
            if (result < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(48, 48));
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                _ = DeleteObject(bitmapHandle);
            }
        }
        finally
        {
            if (Marshal.IsComObject(imageFactory))
            {
                _ = Marshal.FinalReleaseComObject(imageFactory);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            ShellSize size,
            ShellItemImageFlags flags,
            out IntPtr bitmapHandle);
    }

    [Flags]
    private enum ShellItemImageFlags : uint
    {
        BiggerSizeOk = 0x00000001,
        IconOnly = 0x00000004
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ShellSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private sealed record CacheEntry(FolderTabVisual Visual, LinkedListNode<string> Node);
}

internal sealed record FolderTabVisual(ImageSource? Image, bool IsThumbnail)
{
    public static FolderTabVisual Empty { get; } = new(null, false);
}

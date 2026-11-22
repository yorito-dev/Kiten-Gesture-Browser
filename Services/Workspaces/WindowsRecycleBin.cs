using System.IO;
using System.Runtime.InteropServices;

namespace TabBrowser.Services.Workspaces;

internal static class WindowsRecycleBin
{
    private static readonly Guid RecycleBinFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
    private static readonly Guid TransferHandler = new("D5E346A1-F753-4932-B403-4574800E2498");
    private static readonly Guid ShellItemInterface = typeof(IShellItem).GUID;
    private static readonly Guid TransferSourceInterface = typeof(ITransferSource).GUID;

    internal static Task<T> RunOnStaAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(operation());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "TabBrowser Recycle Bin"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool TryMove(string path)
    {
        IShellItem? item = null;
        IShellItem? parent = null;
        IShellItem? recycleBin = null;
        ITransferSource? transfer = null;
        IShellItem? recycled = null;
        try
        {
            // UNC・デバイスパス・ドライブのルートはShellの処理に渡さない。
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(fullPath);
            if (root is null || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                || new DriveInfo(root).DriveType == DriveType.Network)
            {
                return false;
            }

            if (SHCreateItemFromParsingName(fullPath, nint.Zero, ShellItemInterface, out item) < 0 || item is null
                || item.GetParent(out parent) < 0 || parent is null
                || parent.BindToHandler(nint.Zero, TransferHandler, TransferSourceInterface, out transfer) < 0 || transfer is null
                || SHGetKnownFolderItem(RecycleBinFolder, 0, nint.Zero, ShellItemInterface, out recycleBin) < 0 || recycleBin is null)
            {
                return false;
            }

            // フォルダーも1項目として回収する。失敗時に子項目を処理する経路は持たない。
            return transfer.RecycleItem(item, recycleBin, 0, out recycled) >= 0 && recycled is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or COMException)
        {
            return false;
        }
        finally
        {
            Release(recycled);
            Release(transfer);
            Release(recycleBin);
            Release(parent);
            Release(item);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null)
            Marshal.ReleaseComObject(value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out IShellItem? item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderItem(in Guid folderId, uint flags, nint token, in Guid iid, out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(nint bindContext, in Guid handlerId, in Guid iid, out ITransferSource? transfer);

        [PreserveSig]
        int GetParent(out IShellItem? parent);
    }

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        // Windows SDKと同じ仮想関数の順序を保つ。
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int OpenItem(IShellItem item, uint flags, in Guid iid, out nint result);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem? moved);
        [PreserveSig] int RecycleItem(IShellItem item, IShellItem destination, uint flags, out IShellItem? recycled);
    }
}

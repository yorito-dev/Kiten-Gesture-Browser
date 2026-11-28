using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TabBrowser.Views;

// 保存元が無くなったとき、この要求のWindows標準ダイアログだけを閉じる。
internal sealed class DownloadSaveDialog(string initialDirectory, string suggestedFileName) : CommonDialog
{
    private const uint CloseWindow = 0x0010; // WM_CLOSE
    private const uint GetOwner = 4; // GW_OWNER
    private const uint ForceFileSystem = 0x40;
    private const uint DefaultNoMiniMode = 0x20000000;
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
    private static readonly Guid FileSaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");

    private (IFileDialog Dialog, nint Owner)? _showingDialog;
    private bool _cancelRequested;

    internal string? SelectedPath { get; private set; }

    public override void Reset() => SelectedPath = null;

    internal void Cancel()
    {
        _cancelRequested = true;
        if (_showingDialog is { Dialog: IOleWindow window, Owner: var owner }
            && window.GetWindow(out var handle) >= 0 && handle != nint.Zero
            && handle != owner && GetWindow(handle, GetOwner) == owner
            && PostMessage(handle, CloseWindow, nint.Zero, nint.Zero))
        {
            _showingDialog = null;
        }
    }

    protected override bool RunDialog(nint hwndOwner)
    {
        Reset();
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileSaveDialogClass)!)!;
        try
        {
            // 従来と同じく、アプリ側で重複を回避するため上書き確認は付けない。
            dialog.SetOptions(ForceFileSystem | DefaultNoMiniMode);
            dialog.SetTitle("ダウンロード先を選択");
            dialog.SetFileName(suggestedFileName);
            dialog.SetFileTypes(1, new FileType("すべてのファイル (*.*)", "*.*"));
            if (SHCreateItemFromParsingName(initialDirectory, nint.Zero, typeof(IShellItem).GUID, out var folder) >= 0
                && folder is not null)
            {
                try
                {
                    dialog.SetDefaultFolder(folder);
                    dialog.SetFolder(folder);
                }
                finally
                {
                    Marshal.ReleaseComObject(folder);
                }
            }

            if (_cancelRequested)
                return false;

            _showingDialog = (dialog, hwndOwner);
            if (dialog.Show(hwndOwner) < 0 || _cancelRequested)
                return false;

            var result = dialog.GetResult();
            try
            {
                SelectedPath = result.GetDisplayName(FileSystemPath);
                return !string.IsNullOrEmpty(SelectedPath);
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            _showingDialog = null;
            Marshal.ReleaseComObject(dialog);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FileType(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Pattern = pattern;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out IShellItem? item);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint handle, uint command);

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out nint handle);
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // GetResultまで、Windows SDKと同じ仮想関数の順序を保つ。
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, in FileType fileTypes);
        void SetFileTypeIndex(uint fileType);
        uint GetFileTypeIndex();
        uint Advise(nint events);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        uint GetOptions();
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        IShellItem GetFolder();
        IShellItem GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        IShellItem GetResult();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        nint BindToHandler(nint bindContext, in Guid handler, in Guid iid);
        IShellItem GetParent();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetDisplayName(uint nameType);
    }
}

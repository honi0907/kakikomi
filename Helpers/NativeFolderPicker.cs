using System.Runtime.InteropServices;

namespace Kakikomi.Helpers;

/// <summary>
/// Unpackaged WinUI 向け。IFileOpenDialog（エクスプローラー型）でフォルダを選ぶ。
/// </summary>
internal static class NativeFolderPicker
{
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint SigdnFileSysPath = 0x80058000;
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidIFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static string? PickFolder(
        IntPtr ownerHwnd,
        string title = "ネタ動画フォルダを選択",
        string? initialPath = null)
    {
        var dialog = (IFileOpenDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ClsidFileOpenDialog)!)!;

        try
        {
            dialog.SetTitle(title);
            dialog.SetOptions(FosPickFolders | FosForceFileSystem | FosPathMustExist);

            var start = NormalizeExistingDirectory(initialPath);
            if (start is not null)
            {
                var shellItemId = IidIShellItem;
                if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref shellItemId, out var folder) == 0
                    && folder is not null)
                {
                    try
                    {
                        dialog.SetFolder(folder);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(folder);
                    }
                }
            }

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0)
                return null;

            dialog.GetResult(out var item);
            if (item is null)
                return null;

            try
            {
                item.GetDisplayName(SigdnFileSysPath, out var path);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static string? NormalizeExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}

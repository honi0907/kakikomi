using System.Runtime.InteropServices;

namespace Kakikomi.Helpers;

/// <summary>
/// Unpackaged WinUI 向け。IFileOpenDialog（エクスプローラー型）でフォルダ／動画ファイルを選ぶ。
/// </summary>
internal static class NativeFolderPicker
{
    private const uint FosAllowMultiSelect = 0x00000200;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint SigdnFileSysPath = 0x80058000;
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private static readonly (string Name, string Spec)[] VideoFilters =
    [
        ("動画", "*.mp4;*.mov;*.mkv;*.wmv;*.avi;*.m4v"),
        ("すべてのファイル", "*.*")
    ];

    public static string? PickFolder(
        IntPtr ownerHwnd,
        string title = "ネタ動画フォルダを選択",
        string? initialPath = null)
    {
        var dialog = CreateDialog();
        try
        {
            dialog.SetTitle(title);
            dialog.SetOptions(FosPickFolders | FosForceFileSystem | FosPathMustExist);
            TrySetInitialFolder(dialog, initialPath);

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0)
                return null;

            dialog.GetResult(out var item);
            if (item is null)
                return null;

            try
            {
                return GetItemPath(item);
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

    /// <summary>動画ファイルを 1 本選択。キャンセル時は null。</summary>
    public static string? PickSingleVideoFile(
        IntPtr ownerHwnd,
        string title = "素材ファイルを指定",
        string? initialPath = null)
    {
        var dialog = CreateDialog();
        var filterPtrs = Array.Empty<IntPtr>();
        try
        {
            dialog.SetTitle(title);
            dialog.SetOptions(FosForceFileSystem | FosPathMustExist | FosFileMustExist);
            TrySetInitialFolder(dialog, initialPath);
            filterPtrs = SetVideoFilters(dialog);

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0)
                return null;

            dialog.GetResult(out var item);
            if (item is null)
                return null;

            try
            {
                return GetItemPath(item);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            foreach (var p in filterPtrs)
            {
                if (p != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(p);
            }

            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>複数の動画ファイルを選択。キャンセル時は空配列。</summary>
    public static IReadOnlyList<string> PickVideoFiles(
        IntPtr ownerHwnd,
        string title = "ネタ動画を選択",
        string? initialPath = null)
    {
        var dialog = CreateDialog();
        var filterPtrs = Array.Empty<IntPtr>();
        try
        {
            dialog.SetTitle(title);
            dialog.SetOptions(
                FosAllowMultiSelect | FosForceFileSystem | FosPathMustExist | FosFileMustExist);
            TrySetInitialFolder(dialog, initialPath);
            filterPtrs = SetVideoFilters(dialog);

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0)
                return [];

            dialog.GetResults(out var results);
            if (results is null)
                return [];

            try
            {
                results.GetCount(out var count);
                if (count == 0)
                    return [];

                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    results.GetItemAt(i, out var item);
                    if (item is null)
                        continue;

                    try
                    {
                        var path = GetItemPath(item);
                        if (!string.IsNullOrWhiteSpace(path))
                            paths.Add(path);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }

                return paths;
            }
            finally
            {
                Marshal.ReleaseComObject(results);
            }
        }
        finally
        {
            foreach (var p in filterPtrs)
            {
                if (p != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(p);
            }

            Marshal.ReleaseComObject(dialog);
        }
    }

    private static IFileOpenDialog CreateDialog() =>
        (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOpenDialog)!)!;

    private static void TrySetInitialFolder(IFileOpenDialog dialog, string? initialPath)
    {
        var start = NormalizeExistingDirectory(initialPath);
        if (start is null)
            return;

        var shellItemId = IidIShellItem;
        if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref shellItemId, out var folder) != 0
            || folder is null)
        {
            return;
        }

        try
        {
            dialog.SetFolder(folder);
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    private static IntPtr[] SetVideoFilters(IFileOpenDialog dialog)
    {
        var specs = new ComDlgFilterSpec[VideoFilters.Length];
        var allocated = new IntPtr[VideoFilters.Length * 2];
        var allocIndex = 0;
        try
        {
            for (var i = 0; i < VideoFilters.Length; i++)
            {
                var namePtr = Marshal.StringToCoTaskMemUni(VideoFilters[i].Name);
                var specPtr = Marshal.StringToCoTaskMemUni(VideoFilters[i].Spec);
                allocated[allocIndex++] = namePtr;
                allocated[allocIndex++] = specPtr;
                specs[i] = new ComDlgFilterSpec
                {
                    pszName = namePtr,
                    pszSpec = specPtr
                };
            }

            dialog.SetFileTypes((uint)specs.Length, specs);
            dialog.SetFileTypeIndex(1);
            return allocated;
        }
        catch
        {
            for (var i = 0; i < allocated.Length; i++)
            {
                if (allocated[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(allocated[i]);
            }

            throw;
        }
    }

    private static string? GetItemPath(IShellItem item)
    {
        item.GetDisplayName(SigdnFileSysPath, out var path);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? NormalizeExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (Directory.Exists(full))
                return full;

            var parent = Path.GetDirectoryName(full);
            return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) ? parent : null;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ComDlgFilterSpec
    {
        public IntPtr pszName;
        public IntPtr pszSpec;
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] ComDlgFilterSpec[] rgFilterSpec);
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
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PropertyKey keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
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

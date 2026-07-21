using System.Runtime.InteropServices;
using System.Text;

namespace Kakikomi.Helpers;

/// <summary>
/// Unpackaged WinUI 向け。WinRT FolderPicker の代わりに SHBrowseForFolder を使う。
/// </summary>
internal static class NativeFolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const int MaxPath = 520;

    public static string? PickFolder(IntPtr ownerHwnd, string title = "ネタ動画フォルダを選択")
    {
        var displayName = Marshal.AllocHGlobal(MaxPath * 2);
        try
        {
            var bi = new BrowseInfo
            {
                hwndOwner = ownerHwnd,
                pidlRoot = IntPtr.Zero,
                pszDisplayName = displayName,
                lpszTitle = title,
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle,
                lpfn = IntPtr.Zero,
                lParam = IntPtr.Zero,
                iImage = 0
            };

            var pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero)
                return null;

            try
            {
                var sb = new StringBuilder(MaxPath);
                return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayName);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);
}

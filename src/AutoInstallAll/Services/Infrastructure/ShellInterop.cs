using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AutoInstallAll.Services.Infrastructure;

/// <summary>Tạo shortcut .lnk (IShellLinkW) và trích icon .exe ra PNG — chạy trên luồng STA riêng.</summary>
public static class ShellInterop
{
    public static Task CreateShortcutAsync(string lnkPath, string targetPath, string? arguments = null,
        string? workingDirectory = null, string? description = null) =>
        RunStaAsync(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(targetPath);
                link.SetWorkingDirectory(workingDirectory ?? Path.GetDirectoryName(targetPath)!);
                if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                link.SetIconLocation(targetPath, 0);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        });

    /// <summary>Trích icon lớn nhất (≤256px) của file .exe ra PNG. Trả false nếu file không có icon.</summary>
    public static Task<bool> ExtractIconToPngAsync(string exePath, string pngPath, int size = 256) =>
        RunStaAsync(() =>
        {
            var icons = new IntPtr[1];
            var ids = new uint[1];
            uint count = PrivateExtractIconsW(exePath, 0, size, size, icons, ids, 1, 0);
            if (count == 0 || count == uint.MaxValue || icons[0] == IntPtr.Zero)
                return false;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
                return true;
            }
            finally
            {
                DestroyIcon(icons[0]);
            }
        });

    private static Task RunStaAsync(Action action) => RunStaAsync(() => { action(); return true; });

    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "ShellInterop STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

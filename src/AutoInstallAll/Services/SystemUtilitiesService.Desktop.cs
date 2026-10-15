using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace AutoInstallAll.Services;

/// <summary>Một thay đổi Registry đã áp dụng (hiển thị trong hộp thoại kết quả và log).</summary>
public sealed record DesktopTweakResult(string Name, bool Success, string Message);

/// <summary>"Chuẩn hóa Desktop &amp; Menu" cho Windows 10/11.</summary>
public sealed partial class SystemUtilitiesService
{
    private const string DesktopCategory = "desktop";

    private const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

    /// <summary>Các icon hệ thống đưa ra Desktop (GUID → tên tiếng Việt).</summary>
    public static readonly IReadOnlyList<(string Guid, string Name)> DesktopIcons = new[]
    {
        ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "This PC"),
        ("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", "Thư mục người dùng"),
        ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Network"),
        ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "Control Panel"),
    };

    /// <summary>
    /// Hiện icon This PC / User's Files / Network / Control Panel, bật menu chuột phải đầy đủ (Windows 11),
    /// hiện đuôi file, rồi làm mới Explorer. Ghi vào hồ sơ của người đang đăng nhập (chủ explorer.exe) —
    /// đúng cả khi ứng dụng được nâng quyền bằng một tài khoản Admin khác.
    /// </summary>
    public async Task<IReadOnlyList<DesktopTweakResult>> ApplyDesktopTweaksAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<DesktopTweakResult>();
        var (root, owner) = OpenInteractiveUserHive();
        _log.Info(DesktopCategory, $"Chuẩn hóa Desktop & Menu cho {owner}.");

        try
        {
            progress?.Report("Đang hiện icon ra Desktop...");
            foreach (var (guid, name) in DesktopIcons)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(SetValue(root, HideDesktopIconsKey, guid, 0, RegistryValueKind.DWord, $"Icon {name}"));
            }

            progress?.Report("Đang bật menu chuột phải đầy đủ...");
            if (Environment.OSVersion.Version.Build >= 22000)
                results.Add(SetValue(root, ClassicMenuKey, "", "", RegistryValueKind.String, "Menu chuột phải đầy đủ"));
            else
                results.Add(new DesktopTweakResult("Menu chuột phải đầy đủ", true, "Windows 10 đã có sẵn — bỏ qua"));

            progress?.Report("Đang bật hiện đuôi file...");
            results.Add(SetValue(root, ExplorerAdvancedKey, "HideFileExt", 0, RegistryValueKind.DWord, "Hiện đuôi file"));
        }
        finally
        {
            if (root != Registry.CurrentUser) root.Dispose();
        }

        progress?.Report("Đang làm mới Explorer...");
        results.Add(await RefreshExplorerAsync(ct));

        foreach (var r in results)
        {
            if (r.Success) _log.Info(DesktopCategory, $"  {r.Name}: {r.Message}");
            else _log.Warn(DesktopCategory, $"  {r.Name}: {r.Message}");
        }
        return results;
    }

    private DesktopTweakResult SetValue(RegistryKey root, string subKey, string name, object value, RegistryValueKind kind, string label)
    {
        try
        {
            using var key = root.CreateSubKey(subKey, writable: true)
                            ?? throw new IOException($"Không tạo được khóa {subKey}");
            key.SetValue(name, value, kind);
            return new DesktopTweakResult(label, true, "Đã áp dụng");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new DesktopTweakResult(label, false, ex.Message);
        }
    }

    /// <summary>HKEY_USERS\&lt;SID chủ explorer.exe&gt;; không xác định được thì dùng HKCU của tiến trình.</summary>
    private (RegistryKey Root, string Owner) OpenInteractiveUserHive()
    {
        var sid = GetExplorerOwnerSid();
        var current = WindowsIdentity.GetCurrent().User;
        if (sid is not null && sid != current)
        {
            var hive = Registry.Users.OpenSubKey(sid.Value, writable: true);
            if (hive is not null)
            {
                string name;
                try { name = sid.Translate(typeof(NTAccount)).Value; }
                catch (IdentityNotMappedException) { name = sid.Value; }
                return (hive, name);
            }
            _log.Warn(DesktopCategory, $"Không mở được HKEY_USERS\\{sid.Value} → dùng HKCU của tiến trình.");
        }
        return (Registry.CurrentUser, Environment.UserName);
    }

    private static SecurityIdentifier? GetExplorerOwnerSid()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                if (p.SessionId != sessionId) continue;
                IntPtr token = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(p.Handle, TokenQuery, out token)) continue;
                    using var id = new WindowsIdentity(token);
                    if (id.User is { } user) return user;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    // explorer vừa thoát hoặc không đủ quyền đọc token → thử tiến trình kế tiếp.
                }
                finally
                {
                    if (token != IntPtr.Zero) CloseHandle(token);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Báo Shell cập nhật Desktop/đuôi file ngay, rồi khởi động lại explorer.exe để menu chuột phải mới có hiệu lực.
    /// Windows tự bật lại Shell (AutoRestartShell) dưới quyền người dùng; nếu sau 6 giây chưa có thì tự mở lại.
    /// </summary>
    private async Task<DesktopTweakResult> RefreshExplorerAsync(CancellationToken ct)
    {
        const string label = "Làm mới Explorer";
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "ShellState", SmtoAbortIfHung, 2000, out _);

            int sessionId = Process.GetCurrentProcess().SessionId;
            var explorers = Process.GetProcessesByName("explorer").Where(p => p.SessionId == sessionId).ToList();
            if (explorers.Count == 0)
                return new DesktopTweakResult(label, true, "Đã báo Shell cập nhật (Explorer không chạy)");

            foreach (var p in explorers)
            {
                using (p)
                {
                    try { p.Kill(); p.WaitForExit(5000); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }

            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(500, ct);
                if (Process.GetProcessesByName("explorer").Any(p => p.SessionId == sessionId))
                    return new DesktopTweakResult(label, true, "Đã khởi động lại Explorer");
            }

            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = true
            })?.Dispose();
            return new DesktopTweakResult(label, true, "Đã mở lại Explorer");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DesktopTweakResult(label, false, $"{ex.Message} — hãy đăng xuất rồi đăng nhập lại");
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
}

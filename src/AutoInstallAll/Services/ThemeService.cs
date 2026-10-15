using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AutoInstallAll.Models;
using Microsoft.Win32;

namespace AutoInstallAll.Services;

/// <summary>
/// Đổi giao diện Sáng / Tối / Theo Windows lúc đang chạy: hoán đổi bảng màu Themes/Colors.*.xaml
/// (mọi chỗ dùng màu là DynamicResource) và đổi thanh tiêu đề cửa sổ sang nền tối.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string LightSource = "pack://application:,,,/AutoInstallAll;component/Themes/Colors.Light.xaml";
    private const string DarkSource = "pack://application:,,,/AutoInstallAll;component/Themes/Colors.Dark.xaml";

    private UiThemeMode _mode = UiThemeMode.Light;
    private bool _listeningToSystem;

    public bool IsDark { get; private set; }

    /// <summary>Phát sau khi bảng màu đã đổi (để View làm mới các binding dùng converter màu).</summary>
    public event EventHandler? ThemeChanged;

    public void Apply(UiThemeMode mode)
    {
        _mode = mode;
        if (mode == UiThemeMode.System && !_listeningToSystem)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listeningToSystem = true;
        }
        else if (mode != UiThemeMode.System && _listeningToSystem)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listeningToSystem = false;
        }
        SetDark(mode switch
        {
            UiThemeMode.Dark => true,
            UiThemeMode.System => !IsWindowsLightTheme(),
            _ => false
        });
    }

    private void SetDark(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        var dictionaries = app.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Colors.", StringComparison.OrdinalIgnoreCase) == true);
        bool changed = current is null || IsDark != dark;
        IsDark = dark;
        if (changed)
        {
            var next = new ResourceDictionary { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute) };
            if (current is null) dictionaries.Insert(0, next);
            else dictionaries[dictionaries.IndexOf(current)] = next;
        }
        foreach (Window w in app.Windows)
            ApplyTitleBar(w);
        if (changed)
            ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Thanh tiêu đề tối/sáng theo chủ đề (Windows 10 2004+ / Windows 11; bản cũ hơn bỏ qua).</summary>
    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _mode == UiThemeMode.System)
            Application.Current?.Dispatcher.BeginInvoke(() => SetDark(!IsWindowsLightTheme()));
    }

    public void Dispose()
    {
        if (_listeningToSystem)
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listeningToSystem = false;
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

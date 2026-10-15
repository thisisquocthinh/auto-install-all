using System.Windows;
using System.Windows.Threading;
using AutoInstallAll.Models;
using AutoInstallAll.Services;
using AutoInstallAll.Services.Infrastructure;
using AutoInstallAll.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AutoInstallAll;

public partial class App : Application
{
    private ServiceProvider? _services;

    public IServiceProvider Services => _services ?? throw new InvalidOperationException("DI chưa khởi tạo.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _services?.GetService<LogService>()?.Error("app", "Lỗi luồng nền chưa xử lý — ứng dụng sẽ đóng", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.GetService<LogService>()?.Error("app", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            // 1) Cấu trúc USB Portable cạnh AutoInstallAll.exe
            var paths = new StoragePathManager();
            paths.EnsureStructure();
            var stale = paths.CleanupStaleDownloads();
            AppUpdateService.CleanupAfterUpdate(paths.RootDirectory);

            // 2) manifest.json
            var manifest = ManifestConfig.Load(paths.ManifestPath);
            foreach (var item in manifest.AllItems)
            {
                paths.AttachIcon(item);
                item.IsSelected = item.SelectedByDefault;
                item.State = paths.InstallerExists(item) ? SoftwareState.Ready : SoftwareState.Missing;
            }

            // 3) Dependency Injection
            _services = ServiceRegistration.Build(paths, manifest);
            _services.GetRequiredService<LogService>().Info("app",
                $"Khởi động AutoInstallAll từ {paths.RootDirectory} — {manifest.AllItems.Count()} mục, dọn {stale} file .downloading.");

            // 4) Giao diện Sáng / Tối trước khi dựng cửa sổ
            _services.GetRequiredService<ThemeService>().Apply(manifest.Settings.UiTheme);

            // 5) Cửa sổ chính
            var main = _services.GetRequiredService<MainWindow>();
            MainWindow = main;
            main.Show();
#if DEBUG
            if (Environment.GetEnvironmentVariable("AIA_STRESS") == "1")
            {
                // Mở trang Thư mục USB để danh sách Nhật ký đang hiển thị (đúng điều kiện gây lỗi).
                _services.GetRequiredService<ViewModels.MainViewModel>().CurrentPage = ViewModels.NavPage.Usb;
                RunStressTest(_services.GetRequiredService<LogService>());
            }
#endif
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không khởi động được Auto Install All:\n\n{ex.Message}", "Auto Install All",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool _showingError;

    /// <summary>
    /// Ghi log và báo lỗi giao diện. Chỉ hiện MỘT hộp thoại tại một thời điểm: trong lúc hộp thoại mở, Dispatcher vẫn
    /// chạy tiếp các việc trong hàng đợi — nếu lỗi lặp lại mà mỗi lần lại mở hộp thoại mới thì sẽ tràn ngăn xếp.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _services?.GetService<LogService>()?.Error("app", "Lỗi giao diện chưa xử lý", e.Exception);
        if (_showingError) return;
        _showingError = true;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\nChi tiết đã ghi vào thư mục Logs.", "Auto Install All — Lỗi",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

#if DEBUG
    /// <summary>
    /// Chỉ bản Debug: tái hiện kịch bản tràn ngăn xếp của v1.0.0 — log dồn dập từ nhiều luồng trong khi
    /// giao diện liên tục ném lỗi (mỗi lỗi mở hộp thoại + ghi log).
    /// </summary>
    private void RunStressTest(LogService log)
    {
        for (int t = 0; t < 4; t++)
        {
            int thread = t;
            Task.Run(() =>
            {
                for (int i = 0; i < 1500; i++)
                    log.Info("stress", $"luồng {thread} dòng {i}");
            });
        }
        for (int i = 0; i < 60; i++)
        {
            int n = i;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                throw new InvalidOperationException($"Lỗi giả lập #{n}")));
        }
    }
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}

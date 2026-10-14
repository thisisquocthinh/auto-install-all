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

            // 4) Cửa sổ chính
            var main = _services.GetRequiredService<MainWindow>();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không khởi động được Auto Install All:\n\n{ex.Message}", "Auto Install All",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<LogService>()?.Error("app", "Lỗi giao diện chưa xử lý", e.Exception);
        MessageBox.Show(e.Exception.Message, "Auto Install All — Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}

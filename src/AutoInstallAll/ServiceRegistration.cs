using AutoInstallAll.Models;
using AutoInstallAll.Services;
using AutoInstallAll.Services.Infrastructure;
using AutoInstallAll.ViewModels;
using AutoInstallAll.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AutoInstallAll;

/// <summary>Đăng ký Dependency Injection cho toàn bộ ứng dụng.</summary>
public static class ServiceRegistration
{
    public static ServiceProvider Build(StoragePathManager paths, ManifestConfig manifest)
    {
        var services = new ServiceCollection();

        // Lưu trữ USB + cấu hình (đã khởi tạo trước để báo lỗi sớm nếu USB khoá ghi / manifest hỏng)
        services.AddSingleton(paths);
        services.AddSingleton(manifest);

        // Hạ tầng
        services.AddSingleton<LogService>();
        services.AddSingleton(sp => new HttpDownloader(sp.GetRequiredService<ManifestConfig>().Settings.HttpUserAgent));
        services.AddSingleton<WingetClient>();

        // 4 Backend Services
        services.AddSingleton<AutoUpdaterService>();
        services.AddSingleton<SilentInstallService>();
        services.AddSingleton<AddApplicationService>();
        services.AddSingleton<SystemUtilitiesService>();
        services.AddSingleton<AppUpdateService>();
        services.AddSingleton<ThemeService>();

        // UI
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<AddAppViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}

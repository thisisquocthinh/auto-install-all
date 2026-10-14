using System.Diagnostics;
using System.Windows;
using AutoInstallAll.Models;
using AutoInstallAll.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AutoInstallAll.Views;

/// <summary>Cài đặt IDialogService bằng WPF: MessageBox, AddAppDialog, OpenFileDialog, Explorer.</summary>
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    public DialogService(IServiceProvider services) => _services = services;

    private static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public bool Confirm(string title, string message, bool danger = false) =>
        Show(message, title, MessageBoxButton.YesNo, danger ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);

    public void Error(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);

    public SoftwareItem? ShowAddApp()
    {
        var vm = _services.GetRequiredService<AddAppViewModel>();
        var dialog = new AddAppDialog(vm) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? vm.Result : null;
    }

    public string? PickInstallerFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn bộ cài đặt",
            Filter = "Bộ cài (*.exe;*.msi;*.zip)|*.exe;*.msi;*.zip|Tất cả (*.*)|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        var app = Application.Current;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(message, title, buttons, image, defaultResult));
        return Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image, defaultResult)
            : MessageBox.Show(message, title, buttons, image, defaultResult);
    }
}

using System.Windows.Threading;
using System.Windows;
using AutoInstallAll.Services;
using AutoInstallAll.ViewModels;

namespace AutoInstallAll.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        // Màu tính bằng converter (thẻ Pastel, chấm trạng thái) không tự đổi theo DynamicResource → gắn lại DataContext.
        theme.ThemeChanged += (_, _) =>
        {
            DataContext = null;
            DataContext = _vm;
            _vm.OnThemeChanged();
        };
        Loaded += OnLoaded;

        // Nhật ký tự cuộn xuống dòng mới nhất — chỉ khi danh sách đang hiển thị, sau khi layout xong,
        // không bao giờ cuộn đồng bộ bên trong CollectionChanged (nguyên nhân tràn ngăn xếp ở v1.0.0).
        vm.LogFlushed += (_, _) =>
        {
            if (!LogList.IsVisible || LogList.Items.Count == 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                try
                {
                    if (LogList.IsVisible && LogList.Items.Count > 0)
                        LogList.ScrollIntoView(LogList.Items[^1]);
                }
                catch (InvalidOperationException)
                {
                    // Danh sách đang được dựng lại — lần ghi log kế tiếp sẽ cuộn.
                }
            });
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Auto Install All", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

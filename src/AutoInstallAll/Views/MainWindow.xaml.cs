using System.Collections.Specialized;
using System.Windows;
using AutoInstallAll.ViewModels;

namespace AutoInstallAll.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += OnLoaded;

        // Activity Log tự cuộn xuống dòng mới nhất.
        ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
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

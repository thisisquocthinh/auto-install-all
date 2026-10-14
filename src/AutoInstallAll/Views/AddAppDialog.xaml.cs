using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AutoInstallAll.ViewModels;

namespace AutoInstallAll.Views;

public partial class AddAppDialog : Window
{
    private readonly AddAppViewModel _vm;
    private bool _closingFromViewModel;

    public AddAppDialog(AddAppViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _vm.IsIdle)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };
    }

    private void OnRequestClose(object? sender, bool success)
    {
        _vm.RequestClose -= OnRequestClose;
        _closingFromViewModel = true;
        DialogResult = success;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
            DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Không đóng giữa lúc đang tải — huỷ trước, người dùng đóng lại sau khi file .downloading được dọn.
        if (_vm.IsBusy && !_closingFromViewModel)
        {
            _vm.CancelPending();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace AutoInstallAll;

public static class Program
{
    /// <summary>
    /// Điểm vào: chặn chạy 2 bản cùng lúc từ cùng một USB (tránh 2 tiến trình cùng ghi manifest.json / .\Installers\).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir)))[..16];
        using var mutex = new Mutex(initiallyOwned: true, $@"Global\AutoInstallAll_{hash}", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Auto Install All đang chạy từ thư mục này.", "Auto Install All",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

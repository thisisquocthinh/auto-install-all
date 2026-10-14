using System.ComponentModel;
using System.Windows.Media;
using AutoInstallAll.Models;
using AutoInstallAll.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoInstallAll.ViewModels;

/// <summary>Một thẻ Pastel trên lưới 4 cột ở Trang chủ — mỗi thẻ đúng 1 phần mềm.</summary>
public sealed class AppCardViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<string, (string Description, ColorGroup Color)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google-chrome"] = ("Trình duyệt web", ColorGroup.Pink),
        ["unikey"] = ("Bộ gõ tiếng Việt", ColorGroup.Peach),
        ["winrar"] = ("Giải nén tập tin", ColorGroup.Peach),
        ["ultraviewer"] = ("Điều khiển từ xa", ColorGroup.Lavender),
        ["teamviewer"] = ("Hỗ trợ từ xa", ColorGroup.Lavender),
        ["zalo-pc"] = ("Nhắn tin & Gọi điện", ColorGroup.Mint),
        ["vlc"] = ("Xem phim & Nghe nhạc", ColorGroup.Pink),
        ["7zip"] = ("Nén & Giải nén nhẹ", ColorGroup.Mint),
    };

    /// <summary>Thứ tự hiển thị trên lưới: 8 phần mềm có sẵn trước, phần mềm thêm sau giữ thứ tự manifest.</summary>
    private static readonly string[] DisplayOrder =
        { "google-chrome", "unikey", "winrar", "ultraviewer", "teamviewer", "zalo-pc", "vlc", "7zip" };

    public static int OrderOf(SoftwareItem item)
    {
        var i = Array.FindIndex(DisplayOrder, id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? DisplayOrder.Length : i;
    }

    public AppCardViewModel(SoftwareItem item)
    {
        Item = item;
        item.PropertyChanged += OnItemChanged;
    }

    public SoftwareItem Item { get; }

    public string Description => Known.TryGetValue(Item.Id, out var k)
        ? k.Description
        : string.IsNullOrWhiteSpace(Item.Publisher) ? "Phần mềm bổ sung" : Item.Publisher!;

    public Brush Background => ColorGroupToBrushConverter.For(Known.TryGetValue(Item.Id, out var k) ? k.Color : Item.ColorGroup);

    public bool IsBusy => Item.IsBusy;
    public double Progress => Item.Progress;

    /// <summary>Nội dung nhãn viên thuốc trắng ở đáy thẻ.</summary>
    public string StatusText => Item.State switch
    {
        SoftwareState.Checking => "Đang kiểm tra bản mới...",
        SoftwareState.Downloading => $"Đang tải {Item.Progress:0}%",
        SoftwareState.Verifying => "Đang kiểm tra file...",
        SoftwareState.Installing => $"Đang cài đặt {Item.Progress:0}%",
        SoftwareState.Queued => "Đang chờ cài đặt",
        SoftwareState.Installed => $"✓ Đã cài xong{VersionSuffix}",
        SoftwareState.Failed => "✗ Lỗi – rê chuột để xem",
        SoftwareState.Missing => "↓ Chưa có trên USB",
        _ when Item.IsUpdateAvailable && Item.HasLocalInstaller && Item.LatestVersion is not null => $"⬆ Có bản mới v{Item.LatestVersion}",
        _ => $"✓ Bản v{Item.LocalVersion ?? "?"} (Sẵn sàng)"
    };

    private string VersionSuffix => string.IsNullOrWhiteSpace(Item.LocalVersion) ? "" : $" v{Item.LocalVersion}";

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SoftwareItem.State):
            case nameof(SoftwareItem.Progress):
            case nameof(SoftwareItem.LocalVersion):
            case nameof(SoftwareItem.LatestVersion):
            case nameof(SoftwareItem.LocalFile):
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(Progress));
                break;
            case nameof(SoftwareItem.ColorGroup):
                OnPropertyChanged(nameof(Background));
                break;
            case nameof(SoftwareItem.Publisher):
                OnPropertyChanged(nameof(Description));
                break;
        }
    }

    public void Dispose() => Item.PropertyChanged -= OnItemChanged;
}

/// <summary>Một thanh ngang Pastel trong thẻ Disk Partitioning.</summary>
public sealed record DiskBarViewModel(string Label, string Detail, double Percent, Brush Fill);

/// <summary>Một file trong .\Installers\ (trang USB Folder).</summary>
public sealed record InstallerFileViewModel(string Name, string SizeText, string Modified, string Owner, bool IsOrphan);

/// <summary>Một gói Appx trong danh sách Clean Bloatware.</summary>
public sealed partial class BloatwareItemViewModel : ObservableObject
{
    public BloatwareItemViewModel(string packageName) => PackageName = packageName;

    public string PackageName { get; }

    public string DisplayName => PackageName switch
    {
        "Microsoft.549981C3F5F10" => "Cortana",
        "MicrosoftCorporationII.MicrosoftFamily" => "Microsoft Family",
        "Clipchamp.Clipchamp" => "Clipchamp",
        _ => PackageName.Replace("Microsoft.", "").Replace("Microsoft", "")
    };

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool? _removed;
}

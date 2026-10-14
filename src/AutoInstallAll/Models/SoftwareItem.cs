using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoInstallAll.Models;

/// <summary>Nguồn tải / kiểm tra phiên bản của một ứng dụng.</summary>
public sealed class UpdateSource
{
    public UpdateSourceMode Mode { get; set; } = UpdateSourceMode.DirectUrl;
    public string? WingetId { get; set; }
    public string? DirectUrl { get; set; }

    /// <summary>"owner/repo", ví dụ "ip7z/7zip".</summary>
    public string? GithubRepo { get; set; }

    /// <summary>Regex khớp tên asset trong release, ví dụ "^7z\d+-x64\.exe$".</summary>
    public string? GithubAssetPattern { get; set; }
}

public sealed class FileCopyRule
{
    /// <summary>Đường dẫn tương đối so với thư mục gốc USB (vd: Config\rarreg.key).</summary>
    public string From { get; set; } = "";

    /// <summary>Đích trên máy khách, cho phép biến môi trường (%ProgramFiles%).</summary>
    public string To { get; set; } = "";

    /// <summary>Bỏ qua nếu file nguồn không tồn tại.</summary>
    public bool Optional { get; set; } = true;
}

public sealed class PostInstallActions
{
    public bool CreateDesktopShortcut { get; set; }
    public List<FileCopyRule> CopyFiles { get; set; } = new();

    /// <summary>Dành cho gói Zip/Portable: thư mục giải nén (cho phép biến môi trường).</summary>
    public string? ExtractTo { get; set; }

    /// <summary>File thực thi chính sau khi giải nén (để tạo shortcut / task).</summary>
    public string? MainExecutable { get; set; }

    /// <summary>Đăng ký Task Scheduler chạy khi đăng nhập với quyền cao nhất (UniKey).</summary>
    public bool RegisterStartupTask { get; set; }
}

/// <summary>
/// Một phần mềm trong manifest.json. Thuộc tính có setter công khai được lưu vào JSON;
/// thuộc tính [JsonIgnore] là trạng thái runtime để binding lên UI.
/// </summary>
public sealed partial class SoftwareItem : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _publisher;
    [ObservableProperty] private string _category = "Other";
    [ObservableProperty] private ColorGroup _colorGroup = ColorGroup.Mint;
    [ObservableProperty] private InstallerType _installerType = InstallerType.Exe;
    [ObservableProperty] private PackagerType _packagerType = PackagerType.Unknown;
    [ObservableProperty] private string _silentArgs = "";
    [ObservableProperty] private UpdateSource _source = new();

    /// <summary>Mẫu tên file, {version} được thay bằng phiên bản (vd: "Chrome_v{version}.msi").</summary>
    [ObservableProperty] private string _fileNameTemplate = "";

    /// <summary>Tên file (chỉ tên, không chứa thư mục) trong .\Installers\.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalInstaller))]
    private string? _localFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel), nameof(IsUpdateAvailable))]
    private string? _localVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel), nameof(IsUpdateAvailable))]
    private string? _latestVersion;

    /// <summary>SHA-256 dạng hex in hoa của file đang có trong .\Installers\.</summary>
    [ObservableProperty] private string? _sha256;
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private string? _etag;
    [ObservableProperty] private DateTimeOffset? _lastModified;
    [ObservableProperty] private string? _iconFile;
    [ObservableProperty] private bool _selectedByDefault = true;
    [ObservableProperty] private PostInstallActions _postInstall = new();

    // ---------- Runtime (không lưu) ----------

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(VersionLabel), nameof(IsBusy))]
    private SoftwareState _state = SoftwareState.Missing;

    [ObservableProperty][property: JsonIgnore] private bool _isSelected;

    /// <summary>0–100.</summary>
    [ObservableProperty][property: JsonIgnore] private double _progress;

    [ObservableProperty][property: JsonIgnore] private string? _statusMessage;

    /// <summary>Đường dẫn tuyệt đối tới icon trong .\Icons\ (được StoragePathManager gán).</summary>
    [ObservableProperty][property: JsonIgnore] private string? _iconPath;

    /// <summary>URL tải bản mới phát hiện được ở bước Upstream Check.</summary>
    [ObservableProperty][property: JsonIgnore] private string? _pendingDownloadUrl;

    /// <summary>SHA-256 do upstream công bố (Winget / GitHub), dùng để xác minh file .downloading.</summary>
    [ObservableProperty][property: JsonIgnore] private string? _pendingSha256;

    [ObservableProperty][property: JsonIgnore] private long _pendingContentLength;

    [JsonIgnore] public bool HasLocalInstaller => !string.IsNullOrWhiteSpace(LocalFile);

    [JsonIgnore]
    public bool IsBusy => State is SoftwareState.Checking or SoftwareState.Downloading
        or SoftwareState.Verifying or SoftwareState.Installing;

    [JsonIgnore]
    public bool IsUpdateAvailable =>
        !string.IsNullOrWhiteSpace(LatestVersion) &&
        (string.IsNullOrWhiteSpace(LocalVersion) || CompareVersions(LatestVersion, LocalVersion) > 0);

    /// <summary>Nhãn viên thuốc: "Latest", "v0.1 → v0.2", "Not in USB"...</summary>
    [JsonIgnore]
    public string VersionLabel => State switch
    {
        SoftwareState.Missing => "Chưa có trên USB",
        SoftwareState.UpdateAvailable or SoftwareState.Downloading or SoftwareState.Verifying
            => $"v{LocalVersion ?? "?"} → v{LatestVersion ?? "?"}",
        SoftwareState.Latest => $"Mới nhất v{LocalVersion}",
        _ => string.IsNullOrWhiteSpace(LocalVersion) ? "Sẵn sàng" : $"v{LocalVersion}"
    };

    /// <summary>Tạo tên file chuẩn từ FileNameTemplate, đã lọc ký tự không hợp lệ.</summary>
    public string BuildFileName(string version)
    {
        var template = string.IsNullOrWhiteSpace(FileNameTemplate)
            ? $"{Id}_v{{version}}{DefaultExtension()}"
            : FileNameTemplate;
        var raw = template.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();
        return new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public string DefaultExtension() => InstallerType switch
    {
        InstallerType.Msi => ".msi",
        InstallerType.Zip => ".zip",
        _ => ".exe"
    };

    /// <summary>
    /// So sánh phiên bản dạng số chấm ("126.0.6478.127", "7.01", "24.08").
    /// Các đoạn không phải số được so sánh theo chuỗi. Trả về &lt;0, 0, &gt;0.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        static string[] Split(string v) =>
            v.Trim().TrimStart('v', 'V').Split(['.', '-', '_', ' ', '+'], StringSplitOptions.RemoveEmptyEntries);

        var pa = Split(a);
        var pb = Split(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            int cmp = long.TryParse(sa, out var na) && long.TryParse(sb, out var nb)
                ? na.CompareTo(nb)
                : string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
        }
        return 0;
    }
}

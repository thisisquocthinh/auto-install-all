using System.Text.Json.Serialization;

namespace AutoInstallAll.Models;

/// <summary>Định dạng file bộ cài lưu trong .\Installers\.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstallerType>))]
public enum InstallerType
{
    Exe,
    Msi,
    Zip
}

/// <summary>Bộ đóng gói nhận diện từ PE Header, quyết định Silent Switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackagerType>))]
public enum PackagerType
{
    Unknown,
    InnoSetup,
    Nsis,
    Msi,
    InstallShield,
    WixBurn,
    WinRarSfx,
    DirectXRedist,
    Portable,
    Custom
}

/// <summary>Nguồn truy vấn phiên bản mới nhất (Upstream Check).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateSourceMode>))]
public enum UpdateSourceMode
{
    /// <summary>winget show --id ... (lấy Installer Url, Version, SHA-256).</summary>
    Winget,
    /// <summary>Link Evergreen cố định, phát hiện bản mới qua ETag / Last-Modified / Content-Length.</summary>
    DirectUrl,
    /// <summary>GitHub Releases API (/repos/{owner}/{repo}/releases/latest).</summary>
    GitHubRelease,
    /// <summary>File nhập thủ công từ máy, không tự cập nhật.</summary>
    LocalFile
}

/// <summary>Nhóm màu Pastel (Concept 2) — Concept 1 dùng chung một tông kính mờ.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColorGroup>))]
public enum ColorGroup
{
    Pink,     // #F4C5C7 — Trình duyệt
    Peach,    // #FCE0C1 — Bộ gõ & Giải nén
    Lavender, // #D4D4FA — Điều khiển từ xa
    Mint      // #C3F0DC — Nhắn tin & Thêm ứng dụng
}

/// <summary>Giao diện: Sáng (Warm Pastel), Tối (dịu mắt) hoặc theo cài đặt Windows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UiThemeMode>))]
public enum UiThemeMode
{
    Light,
    Dark,
    System
}

/// <summary>Giá trị cũ của manifest (giữ để đọc được manifest.json đời đầu; không còn dùng).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    DarkGlass,
    WarmPastel
}

/// <summary>Trạng thái runtime của một thẻ phần mềm (không lưu vào manifest).</summary>
public enum SoftwareState
{
    Missing,          // Chưa có bộ cài trong .\Installers\
    Ready,            // Có bộ cài, chưa kiểm tra bản mới
    Latest,           // Đã là bản mới nhất
    UpdateAvailable,  // v0.1 -> v0.2
    Checking,
    Downloading,
    Verifying,
    Queued,
    Installing,
    Installed,
    Failed
}

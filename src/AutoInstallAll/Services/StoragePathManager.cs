using System.Diagnostics;
using System.Security.Cryptography;
using AutoInstallAll.Models;

namespace AutoInstallAll.Services;

/// <summary>
/// Quản lý toàn bộ đường dẫn tương đối trên USB Portable, gốc là thư mục chứa AutoInstallAll.exe:
/// <code>
/// .\AutoInstallAll.exe
/// .\manifest.json
/// .\Installers\   bộ cài offline (+ file tạm *.downloading)
/// .\Icons\        icon trích xuất (.png)
/// .\Logs\         nhật ký cài đặt / BitLocker / phân vùng
/// .\Config\       file phụ (rarreg.key ...)
/// </code>
/// Không bao giờ ghi vào %TEMP%.
/// </summary>
public sealed class StoragePathManager
{
    public const string InstallersFolderName = "Installers";
    public const string IconsFolderName = "Icons";
    public const string LogsFolderName = "Logs";
    public const string ConfigFolderName = "Config";
    public const string ManifestFileName = "manifest.json";
    public const string DownloadingExtension = ".downloading";

    private static readonly string[] InstallerExtensions = [".exe", ".msi", ".zip"];

    public string RootDirectory { get; }
    public string InstallersDirectory { get; }
    public string IconsDirectory { get; }
    public string LogsDirectory { get; }
    public string ConfigDirectory { get; }
    public string ManifestPath { get; }

    public StoragePathManager() : this(AppDomain.CurrentDomain.BaseDirectory) { }

    /// <param name="rootDirectory">Chỉ truyền khác BaseDirectory khi kiểm thử.</param>
    public StoragePathManager(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        EnsureNotTempDirectory(RootDirectory);

        InstallersDirectory = Path.Combine(RootDirectory, InstallersFolderName);
        IconsDirectory = Path.Combine(RootDirectory, IconsFolderName);
        LogsDirectory = Path.Combine(RootDirectory, LogsFolderName);
        ConfigDirectory = Path.Combine(RootDirectory, ConfigFolderName);
        ManifestPath = Path.Combine(RootDirectory, ManifestFileName);
    }

    /// <summary>Ổ đĩa chứa công cụ (vd "E:\").</summary>
    public string DriveRoot => Path.GetPathRoot(RootDirectory)!;

    public bool IsRunningFromRemovableDrive
    {
        get
        {
            try { return new DriveInfo(DriveRoot).DriveType == DriveType.Removable; }
            catch { return false; }
        }
    }

    /// <summary>Tạo .\Installers\, .\Icons\, .\Logs\, .\Config\ và kiểm tra quyền ghi trên USB.</summary>
    public void EnsureStructure()
    {
        foreach (var dir in new[] { InstallersDirectory, IconsDirectory, LogsDirectory, ConfigDirectory })
            Directory.CreateDirectory(dir);

        if (!IsWritable(InstallersDirectory))
            throw new UnauthorizedAccessException(
                $"Không ghi được vào '{InstallersDirectory}'. USB có thể đang bị khoá ghi (write-protect).");
    }

    /// <summary>Xoá file *.downloading còn sót do rút USB / mất mạng ở lần chạy trước.</summary>
    public int CleanupStaleDownloads()
    {
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(InstallersDirectory, "*" + DownloadingExtension))
        {
            try { File.Delete(file); count++; }
            catch (IOException) { /* file đang bị khoá bởi tiến trình khác — để lần sau */ }
        }
        return count;
    }

    // ---------------- Đường dẫn bộ cài ----------------

    /// <summary>Đường dẫn tuyệt đối tới file bộ cài trong .\Installers\ (chỉ nhận tên file).</summary>
    public string GetInstallerPath(string fileName) =>
        ResolveInside(InstallersDirectory, fileName);

    public string? GetInstallerPath(SoftwareItem item) =>
        string.IsNullOrWhiteSpace(item.LocalFile) ? null : GetInstallerPath(item.LocalFile);

    /// <summary>Ví dụ: .\Installers\Chrome_v0.2.exe.downloading</summary>
    public string GetDownloadingPath(string finalFileName) =>
        GetInstallerPath(finalFileName) + DownloadingExtension;

    public bool InstallerExists(SoftwareItem item) =>
        GetInstallerPath(item) is { } p && File.Exists(p);

    public IEnumerable<FileInfo> EnumerateInstallerFiles() =>
        new DirectoryInfo(InstallersDirectory)
            .EnumerateFiles()
            .Where(f => InstallerExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));

    /// <summary>Tổng dung lượng các bộ cài (cho widget "Portable USB Hub").</summary>
    public long GetInstallersTotalSize() => EnumerateInstallerFiles().Sum(f => f.Length);

    /// <summary>
    /// Hoàn tất cập nhật nguyên tử: file .downloading → tên chính thức, rồi xoá bộ cài cũ.
    /// Bản cũ chỉ bị xoá sau khi file mới đã nằm đúng vị trí.
    /// </summary>
    public string CommitDownload(string downloadingPath, string finalFileName, string? oldFileName, out bool oldDeleted)
    {
        var finalPath = GetInstallerPath(finalFileName);
        if (!downloadingPath.EndsWith(DownloadingExtension, StringComparison.OrdinalIgnoreCase) ||
            !IsInside(InstallersDirectory, downloadingPath))
            throw new ArgumentException("File tạm không hợp lệ.", nameof(downloadingPath));

        File.Move(downloadingPath, finalPath, overwrite: true);

        oldDeleted = false;
        if (!string.IsNullOrWhiteSpace(oldFileName) &&
            !string.Equals(oldFileName, finalFileName, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = GetInstallerPath(oldFileName);
            if (File.Exists(oldPath))
            {
                try
                {
                    File.SetAttributes(oldPath, FileAttributes.Normal);
                    File.Delete(oldPath);
                    oldDeleted = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File mới đã vào đúng chỗ; bản cũ bị khoá sẽ được dọn ở lần quét sau.
                }
            }
        }
        return finalPath;
    }

    /// <summary>Thư mục giải nén tạm trên USB (vd DirectX redist): .\Installers\_extract\{id}\</summary>
    public string GetExtractDirectory(string id) =>
        ResolveInside(InstallersDirectory, Path.Combine("_extract", SanitizeFileName(id)));

    // ---------------- Icons / Logs / Config ----------------

    public string GetIconPath(string fileName) => ResolveInside(IconsDirectory, fileName);

    public string GetDefaultIconFileName(SoftwareItem item) => SanitizeFileName(item.Id) + ".png";

    /// <summary>Gán IconPath runtime nếu file icon tồn tại trong .\Icons\.</summary>
    public void AttachIcon(SoftwareItem item)
    {
        var name = string.IsNullOrWhiteSpace(item.IconFile) ? GetDefaultIconFileName(item) : item.IconFile;
        var path = GetIconPath(name);
        item.IconPath = File.Exists(path) ? path : null;
    }

    /// <summary>.\Logs\install_2026-10-14.log</summary>
    public string GetLogPath(string category) =>
        ResolveInside(LogsDirectory, $"{SanitizeFileName(category)}_{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Đường dẫn file phụ trong cấu hình (vd "Config\rarreg.key"), luôn nằm trong thư mục gốc USB.</summary>
    public string ResolveRelative(string relativePath) => ResolveInside(RootDirectory, relativePath);

    // ---------------- Tiện ích ----------------

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unnamed" : cleaned;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>Đọc FileVersion/ProductVersion của .exe; .msi/.zip trả null (MSI đọc qua bảng Property ở Phase 2).</summary>
    public static string? ReadFileVersion(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        var v = !string.IsNullOrWhiteSpace(info.ProductVersion) ? info.ProductVersion : info.FileVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v.Split(' ')[0].Replace(',', '.').Trim();
    }

    public long GetAvailableFreeSpace() => new DriveInfo(DriveRoot).AvailableFreeSpace;

    /// <summary>Ghép đường dẫn và chặn path traversal ("..\", đường dẫn tuyệt đối).</summary>
    private static string ResolveInside(string baseDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new ArgumentException("Tên file rỗng.", nameof(relative));
        var full = Path.GetFullPath(Path.Combine(baseDir, relative));
        if (!IsInside(baseDir, full))
            throw new UnauthorizedAccessException($"Đường dẫn '{relative}' nằm ngoài '{baseDir}'.");
        return full;
    }

    private static bool IsInside(string baseDir, string path)
    {
        var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWritable(string dir)
    {
        var probe = Path.Combine(dir, $".write_probe_{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Đảm bảo công cụ không bị chạy từ %TEMP% (vd mở thẳng từ file nén), tránh lưu bộ cài vào đó.</summary>
    private static void EnsureNotTempDirectory(string root)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "AutoInstallAll đang chạy từ thư mục tạm (%TEMP%). Hãy giải nén toàn bộ công cụ ra USB rồi chạy lại.");
    }
}

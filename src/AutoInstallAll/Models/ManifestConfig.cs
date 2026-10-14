using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoInstallAll.Models;

public sealed class ManifestSettings
{
    public AppTheme Theme { get; set; } = AppTheme.DarkGlass;
    public int InstallTimeoutMinutes { get; set; } = 10;
    public bool VerifySha256 { get; set; } = true;
    public bool DeleteOldInstallerAfterUpdate { get; set; } = true;

    /// <summary>Dung lượng trống tối thiểu (GB) phải còn lại trên C: sau khi chia.</summary>
    public int MinFreeSpaceForSplitGB { get; set; } = 40;
    public int DefaultDataDriveSizeGB { get; set; } = 350;
    public string DefaultDataDriveLetter { get; set; } = "D";
    public string DefaultDataDriveLabel { get; set; } = "DATA";
    public string HttpUserAgent { get; set; } = "AutoInstallAll/1.0";
}

/// <summary>Nội dung file .\manifest.json nằm cạnh AutoInstallAll.exe.</summary>
public sealed class ManifestConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ToolVersion { get; set; } = "1.0.0";
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public ManifestSettings Settings { get; set; } = new();

    /// <summary>Ứng dụng thông dụng (khu vực thẻ trung tâm).</summary>
    public List<SoftwareItem> Apps { get; set; } = new();

    /// <summary>Gói hệ thống: Visual C++ AIO, DirectX (nút cài nhanh trong widget).</summary>
    public List<SoftwareItem> SystemTools { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<SoftwareItem> AllItems => Apps.Concat(SystemTools);

    public SoftwareItem? FindById(string id) =>
        AllItems.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Giữ nguyên tiếng Việt và ký tự '\' dễ đọc khi kỹ thuật viên mở file trên USB
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public static ManifestConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ManifestConfig();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var config = JsonSerializer.Deserialize<ManifestConfig>(stream, JsonOptions)
                     ?? throw new InvalidDataException($"manifest.json rỗng hoặc không hợp lệ: {path}");
        config.Validate();
        return config;
    }

    /// <summary>
    /// Ghi nguyên tử: ghi ra manifest.json.tmp trên chính USB rồi File.Replace,
    /// giữ manifest.json.bak để khôi phục nếu rút USB giữa chừng.
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Validate();
        await SaveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmp = path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(fs, this, JsonOptions, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    /// <summary>Kiểm tra trùng Id và Id rỗng; chuẩn hoá LocalFile chỉ còn tên file.</summary>
    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AllItems)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                throw new InvalidDataException($"Ứng dụng '{item.Name}' thiếu trường id trong manifest.json.");
            if (!seen.Add(item.Id))
                throw new InvalidDataException($"Trùng id '{item.Id}' trong manifest.json.");
            if (!string.IsNullOrWhiteSpace(item.LocalFile))
                item.LocalFile = Path.GetFileName(item.LocalFile);
        }
    }
}

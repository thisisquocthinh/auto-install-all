using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using AutoInstallAll.Models;
using AutoInstallAll.Services.Infrastructure;

namespace AutoInstallAll.Services;

public enum AddAppMode
{
    /// <summary>Chế độ 1: tìm theo tên trong kho Winget.</summary>
    Winget,
    /// <summary>Chế độ 2: dán link tải cố định (Evergreen Direct URL).</summary>
    DirectUrl,
    /// <summary>Chế độ 3: chọn file .exe/.msi/.zip có sẵn trên máy.</summary>
    LocalFile
}

/// <summary>Bản nháp hiển thị trong hộp thoại + Add App, người dùng có thể sửa trước khi lưu.</summary>
public sealed class AppDraft
{
    public AddAppMode Mode { get; init; }
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? WingetId { get; init; }
    public string? DirectUrl { get; init; }
    public string? LocalSourcePath { get; init; }
    public InstallerType InstallerType { get; set; } = InstallerType.Exe;
    public PackagerType PackagerType { get; set; } = PackagerType.Unknown;
    public string SilentArgs { get; set; } = "";
    public string? Version { get; set; }
    public string? ExpectedSha256 { get; init; }
    public long? ContentLength { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string Category { get; set; } = "Other";
    public ColorGroup ColorGroup { get; set; } = ColorGroup.Mint;
    public bool SelectedByDefault { get; set; } = true;
    public string Evidence { get; set; } = "";
    public List<string> Warnings { get; } = new();

    /// <summary>true nếu người dùng tự sửa Silent Switch — không ghi đè bằng gợi ý tự động sau khi tải.</summary>
    public bool SilentArgsEditedByUser { get; set; }
}

/// <summary>Xử lý hộp thoại "+ Add App": 3 chế độ thêm ứng dụng vào USB và manifest.json.</summary>
public sealed partial class AddApplicationService
{
    private const string Category = "addapp";
    private readonly StoragePathManager _paths;
    private readonly ManifestConfig _manifest;
    private readonly LogService _log;
    private readonly HttpDownloader _http;
    private readonly WingetClient _winget;

    public AddApplicationService(StoragePathManager paths, ManifestConfig manifest, LogService log,
        HttpDownloader http, WingetClient winget)
    {
        _paths = paths;
        _manifest = manifest;
        _log = log;
        _http = http;
        _winget = winget;
    }

    private TimeSpan PerAppTimeout => TimeSpan.FromMinutes(Math.Max(1, _manifest.Settings.InstallTimeoutMinutes));

    // =====================================================================
    // Chế độ 1: Winget
    // =====================================================================

    public Task<IReadOnlyList<WingetSearchResult>> SearchWingetAsync(string query, CancellationToken ct) =>
        _winget.SearchAsync(query, ct);

    public async Task<AppDraft> PrepareFromWingetAsync(string wingetId, CancellationToken ct)
    {
        var info = await _winget.ShowAsync(wingetId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Không đọc được thông tin gói '{wingetId}' từ winget.");
        if (string.IsNullOrWhiteSpace(info.InstallerUrl))
            throw new InvalidOperationException($"Gói '{wingetId}' không có Installer Url.");

        var type = info.InstallerType?.ToLowerInvariant();
        if (type is "msix" or "appx" or "msstore")
            throw new NotSupportedException($"Gói {wingetId} là {type} (Microsoft Store) — không hỗ trợ lưu offline trên USB.");
        if (type is not null && type.Contains("(zip)", StringComparison.Ordinal))
            throw new NotSupportedException($"Gói {wingetId} là bộ cài nén trong ZIP ({info.InstallerType}) — hãy dùng chế độ Dán link tải hoặc Import file.");

        var packager = InstallerInspector.FromWingetInstallerType(type);
        var installerType = type switch
        {
            "msi" or "wix" => InstallerType.Msi,
            "zip" => InstallerType.Zip,
            _ => InstallerTypeFromName(info.InstallerUrl) ?? InstallerType.Exe
        };

        var draft = new AppDraft
        {
            Mode = AddAppMode.Winget,
            Name = info.Name,
            Publisher = info.Publisher,
            WingetId = info.Id,
            DirectUrl = info.InstallerUrl,
            ExpectedSha256 = info.InstallerSha256,
            Version = info.Version,
            InstallerType = installerType,
            PackagerType = packager,
            SilentArgs = InstallerInspector.GetSilentArgs(packager),
            Evidence = $"winget Installer Type: {info.InstallerType ?? "?"}"
        };
        if (packager == PackagerType.Unknown)
            draft.Warnings.Add("Winget không ghi rõ bộ đóng gói — sẽ phân tích PE Header sau khi tải để gợi ý Silent Switch.");
        GuessCategory(draft);
        return draft;
    }

    // =====================================================================
    // Chế độ 2: Evergreen Direct URL
    // =====================================================================

    public async Task<AppDraft> PrepareFromDirectUrlAsync(string url, string? name, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Link tải phải là URL http(s) hợp lệ.", nameof(url));

        var remote = await _http.GetRemoteInfoAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
        var fileName = remote.FileName ?? Path.GetFileName(uri.AbsolutePath);
        var installerType = InstallerTypeFromName(fileName) ?? InstallerTypeFromContentType(remote.ContentType);

        var draft = new AppDraft
        {
            Mode = AddAppMode.DirectUrl,
            Name = string.IsNullOrWhiteSpace(name) ? PrettyNameFromFile(fileName) : name.Trim(),
            DirectUrl = uri.AbsoluteUri,
            ContentLength = remote.ContentLength,
            ETag = remote.ETag,
            LastModified = remote.LastModified,
            InstallerType = installerType ?? InstallerType.Exe,
            PackagerType = installerType switch
            {
                InstallerType.Msi => PackagerType.Msi,
                InstallerType.Zip => PackagerType.Portable,
                _ => PackagerType.Unknown
            },
            Evidence = $"HTTP: {fileName}, {remote.ContentType ?? "?"}, {(remote.ContentLength is { } l ? $"{l / 1024d / 1024:0.#} MB" : "không rõ dung lượng")}" +
                       $", ETag={(remote.ETag is null ? "không" : "có")}, Last-Modified={(remote.LastModified is null ? "không" : remote.LastModified.Value.ToString("yyyy-MM-dd"))}"
        };
        draft.SilentArgs = InstallerInspector.GetSilentArgs(draft.PackagerType);
        if (installerType is null)
            draft.Warnings.Add("Không xác định được loại file từ URL — sẽ kiểm tra chữ ký file sau khi tải.");
        if (remote.ETag is null && remote.LastModified is null)
            draft.Warnings.Add("Máy chủ không trả ETag / Last-Modified — tự phát hiện bản mới chỉ dựa vào Content-Length.");
        GuessCategory(draft);
        return draft;
    }

    // =====================================================================
    // Chế độ 3: Import file nội bộ
    // =====================================================================

    public AppDraft PrepareFromLocalFile(string path)
    {
        var full = Path.GetFullPath(path);
        var analysis = InstallerInspector.Analyze(full);
        var draft = new AppDraft
        {
            Mode = AddAppMode.LocalFile,
            Name = analysis.ProductName ?? PrettyNameFromFile(Path.GetFileName(full)),
            Publisher = analysis.Company,
            LocalSourcePath = full,
            InstallerType = analysis.InstallerType,
            PackagerType = analysis.Packager,
            SilentArgs = analysis.SuggestedSilentArgs,
            Version = analysis.Version,
            Evidence = analysis.Evidence
        };
        if (analysis.Packager is PackagerType.Unknown or PackagerType.Custom)
            draft.Warnings.Add("Không nhận diện chắc chắn bộ đóng gói — hãy kiểm tra lại Silent Switch (thử /S, /silent, /quiet).");
        GuessCategory(draft);
        _log.Info(Category, $"Phân tích {full}: {analysis.InstallerType}/{analysis.Packager} v{analysis.Version} — {analysis.Evidence}");
        return draft;
    }

    // =====================================================================
    // Lưu vào USB + manifest.json
    // =====================================================================

    public async Task<SoftwareItem> AddAsync(AppDraft draft, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("Tên ứng dụng không được để trống.");
        EnsureNotDuplicate(draft);

        var id = UniqueId(draft.Name);
        var baseName = FileStem(draft.Name);
        var item = new SoftwareItem
        {
            Id = id,
            Name = draft.Name.Trim(),
            Publisher = draft.Publisher,
            Category = draft.Category,
            ColorGroup = draft.ColorGroup,
            InstallerType = draft.InstallerType,
            PackagerType = draft.PackagerType,
            SilentArgs = draft.SilentArgs.Trim(),
            SelectedByDefault = draft.SelectedByDefault,
            Source = new UpdateSource
            {
                Mode = draft.Mode switch
                {
                    AddAppMode.Winget => UpdateSourceMode.Winget,
                    AddAppMode.DirectUrl => UpdateSourceMode.DirectUrl,
                    _ => UpdateSourceMode.LocalFile
                },
                WingetId = draft.WingetId,
                DirectUrl = draft.Mode == AddAppMode.DirectUrl ? draft.DirectUrl : null
            },
            IconFile = id + ".png"
        };
        item.FileNameTemplate = $"{baseName}_v{{version}}{item.DefaultExtension()}";

        _log.Info(Category, $"Thêm '{item.Name}' ({draft.Mode}) id={id}");
        string? tmpPath = null;
        try
        {
            item.State = SoftwareState.Downloading;
            string finalPath;
            if (draft.Mode == AddAppMode.LocalFile)
            {
                (finalPath, tmpPath) = await ImportLocalAsync(item, draft, progress, ct).ConfigureAwait(false);
            }
            else
            {
                (finalPath, tmpPath) = await DownloadNewAsync(item, draft, progress, ct).ConfigureAwait(false);
            }
            tmpPath = null;

            // Tinh chỉnh bằng phân tích file thật (Winget "exe" / URL chưa rõ bộ đóng gói).
            var analysis = InstallerInspector.Analyze(finalPath);
            item.InstallerType = analysis.InstallerType;
            if (item.PackagerType is PackagerType.Unknown || draft.Mode == AddAppMode.LocalFile)
                item.PackagerType = analysis.Packager;
            if (!draft.SilentArgsEditedByUser && string.IsNullOrWhiteSpace(item.SilentArgs))
                item.SilentArgs = analysis.SuggestedSilentArgs;
            if (item.InstallerType == InstallerType.Zip)
                item.PostInstall = new PostInstallActions
                {
                    ExtractTo = Path.Combine("%ProgramFiles%", StoragePathManager.SanitizeFileName(item.Name)),
                    CreateDesktopShortcut = true
                };

            item.Sha256 = await StoragePathManager.ComputeSha256Async(finalPath, ct).ConfigureAwait(false);
            item.FileSize = new FileInfo(finalPath).Length;

            if (item.InstallerType == InstallerType.Exe)
            {
                try { await ShellInterop.ExtractIconToPngAsync(finalPath, _paths.GetIconPath(item.IconFile!)).ConfigureAwait(false); }
                catch (Exception ex) { _log.Warn(Category, $"Không trích được icon {item.Name}", ex); }
            }
            _paths.AttachIcon(item);

            _manifest.Apps.Add(item);
            await _manifest.SaveAsync(_paths.ManifestPath, CancellationToken.None).ConfigureAwait(false);

            item.State = SoftwareState.Latest;
            item.IsSelected = item.SelectedByDefault;
            item.Progress = 100;
            item.StatusMessage = $"Đã thêm {item.LocalFile}";
            _log.Info(Category, $"Đã thêm {item.Name}: {item.LocalFile}, {item.PackagerType}, args '{item.SilentArgs}', SHA-256 {item.Sha256}");
            return item;
        }
        catch (Exception ex)
        {
            if (tmpPath is not null)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException) { }
            }
            if (ex is OperationCanceledException)
                _log.Warn(Category, $"Huỷ thêm {draft.Name}.");
            else
                _log.Error(Category, $"Thêm {draft.Name} thất bại", ex);
            throw;
        }
    }

    private async Task<(string FinalPath, string? Tmp)> DownloadNewAsync(SoftwareItem item, AppDraft draft, IProgress<double>? progress, CancellationToken ct)
    {
        var url = draft.DirectUrl ?? throw new InvalidOperationException("Thiếu link tải.");
        var version = draft.Version ?? (draft.LastModified ?? DateTimeOffset.Now).ToString("yyyy.MM.dd");
        var finalName = item.BuildFileName(version);
        var tmp = _paths.GetDownloadingPath(finalName);

        var needed = (draft.ContentLength ?? 0) + 16 * 1024 * 1024;
        if (_paths.GetAvailableFreeSpace() < needed)
            throw new IOException("USB không đủ dung lượng trống.");

        var reporter = new Progress<double>(p => { item.Progress = p; progress?.Report(p); });
        try
        {
            var dl = await _http.DownloadAsync(url, tmp, reporter, PerAppTimeout, ct).ConfigureAwait(false);

            if (draft.ExpectedSha256 is not null && !string.Equals(draft.ExpectedSha256, dl.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Sai SHA-256: {dl.Sha256} ≠ {draft.ExpectedSha256}.");

            var detected = InstallerInspector.DetectFileType(tmp)
                           ?? throw new InvalidDataException("File tải về không phải .exe / .msi / .zip (có thể là trang HTML).");
            if (detected != item.InstallerType)
            {
                // URL không có đuôi rõ ràng: đổi đuôi theo chữ ký thật.
                item.InstallerType = detected;
                item.FileNameTemplate = $"{FileStem(item.Name)}_v{{version}}{item.DefaultExtension()}";
            }

            if (draft.Version is null && detected == InstallerType.Exe && StoragePathManager.ReadFileVersion(tmp) is { } v)
                version = v;
            finalName = item.BuildFileName(version);

            var final = _paths.CommitDownload(tmp, finalName, null, out _);
            item.LocalFile = finalName;
            item.LocalVersion = version;
            item.LatestVersion = version;
            item.Etag = draft.ETag;
            item.LastModified = draft.LastModified;
            return (final, null);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<(string FinalPath, string? Tmp)> ImportLocalAsync(SoftwareItem item, AppDraft draft, IProgress<double>? progress, CancellationToken ct)
    {
        var source = draft.LocalSourcePath ?? throw new InvalidOperationException("Chưa chọn file.");
        if (!File.Exists(source))
            throw new FileNotFoundException("File nguồn không còn tồn tại.", source);

        var version = draft.Version ?? File.GetLastWriteTime(source).ToString("yyyy.MM.dd");
        var finalName = item.BuildFileName(version);

        // File đã nằm sẵn trong .\Installers\ → dùng tại chỗ, không chép.
        var installersRoot = _paths.InstallersDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(source).StartsWith(installersRoot, StringComparison.OrdinalIgnoreCase))
        {
            item.LocalFile = Path.GetFileName(source);
            item.LocalVersion = version;
            item.LatestVersion = version;
            progress?.Report(100);
            return (source, null);
        }

        var size = new FileInfo(source).Length;
        if (_paths.GetAvailableFreeSpace() < size + 16 * 1024 * 1024)
            throw new IOException("USB không đủ dung lượng trống.");

        var tmp = _paths.GetDownloadingPath(finalName);
        try
        {
            await CopyWithProgressAsync(source, tmp, size, p => { item.Progress = p; progress?.Report(p); }, ct).ConfigureAwait(false);
            var final = _paths.CommitDownload(tmp, finalName, null, out _);
            item.LocalFile = finalName;
            item.LocalVersion = version;
            item.LatestVersion = version;
            return (final, null);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task CopyWithProgressAsync(string source, string dest, long total, Action<double> report, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            long copied = 0;
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                report(total == 0 ? 100 : copied * 100.0 / total);
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            if (copied != total)
                throw new IOException($"Chép chưa đủ: {copied:N0}/{total:N0} byte.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------- Tiện ích ----------------

    private void EnsureNotDuplicate(AppDraft draft)
    {
        foreach (var existing in _manifest.AllItems)
        {
            if (draft.WingetId is not null && string.Equals(existing.Source.WingetId, draft.WingetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{existing.Name}' đã có trong danh sách (Winget ID {draft.WingetId}).");
            if (draft.Mode == AddAppMode.DirectUrl && draft.DirectUrl is not null &&
                string.Equals(existing.Source.DirectUrl, draft.DirectUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{existing.Name}' đã dùng link tải này.");
            if (string.Equals(existing.Name, draft.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Đã có ứng dụng tên '{existing.Name}'.");
        }
    }

    private string UniqueId(string name)
    {
        var slug = Slugify(name);
        var id = slug;
        for (int i = 2; _manifest.FindById(id) is not null; i++)
            id = $"{slug}-{i}";
        return id;
    }

    /// <summary>"Zalo PC" → "zalo-pc"; bỏ dấu tiếng Việt.</summary>
    public static string Slugify(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }
        var slug = MultiDashRegex().Replace(sb.ToString(), "-").Trim('-');
        return slug.Length == 0 ? "app" : slug[..Math.Min(slug.Length, 48)];
    }

    /// <summary>"Zalo PC" → "ZaloPC" (tiền tố tên file bộ cài).</summary>
    private static string FileStem(string name)
    {
        var parts = Slugify(name).Split('-', StringSplitOptions.RemoveEmptyEntries);
        var stem = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return stem.Length == 0 ? "App" : stem;
    }

    private static InstallerType? InstallerTypeFromName(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".exe" => InstallerType.Exe,
            ".msi" => InstallerType.Msi,
            ".zip" => InstallerType.Zip,
            _ => null
        };

    private static InstallerType? InstallerTypeFromContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/x-msi" or "application/x-ole-storage" => InstallerType.Msi,
        "application/zip" or "application/x-zip-compressed" => InstallerType.Zip,
        "application/x-msdownload" or "application/x-dosexec" or "application/vnd.microsoft.portable-executable" => InstallerType.Exe,
        _ => null
    };

    private static string PrettyNameFromFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = TrailingVersionRegex().Replace(stem, "");
        stem = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        stem = Regex.Replace(stem, @"\b(setup|installer|install|x64|x86|win64|win32|offline|full)\b", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(stem) ? "New App" : MultiSpaceRegex().Replace(stem, " ");
    }

    private static void GuessCategory(AppDraft draft)
    {
        var n = $"{draft.Name} {draft.WingetId}".ToLowerInvariant();
        (draft.Category, draft.ColorGroup) = n switch
        {
            _ when ContainsAny(n, "chrome", "firefox", "edge", "browser", "brave", "opera", "coccoc", "cốc cốc") => ("Browser", ColorGroup.Pink),
            _ when ContainsAny(n, "unikey", "evkey", "openkey") => ("InputMethod", ColorGroup.Peach),
            _ when ContainsAny(n, "rar", "zip", "7-zip", "bandizip", "peazip") => ("Archiver", ColorGroup.Peach),
            _ when ContainsAny(n, "teamviewer", "ultraviewer", "anydesk", "rustdesk", "remote") => ("RemoteControl", ColorGroup.Lavender),
            _ when ContainsAny(n, "zalo", "telegram", "discord", "messenger", "skype", "zoom", "teams", "slack") => ("Messaging", ColorGroup.Mint),
            _ when ContainsAny(n, "vlc", "player", "media", "potplayer", "k-lite", "spotify") => ("Media", ColorGroup.Peach),
            _ when ContainsAny(n, "reader", "foxit", "pdf", "office", "wps") => ("Office", ColorGroup.Lavender),
            _ => (draft.Category, draft.ColorGroup)
        };
    }

    private static bool ContainsAny(string s, params string[] keys) => keys.Any(k => s.Contains(k, StringComparison.Ordinal));

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDashRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[_\-\s]?v?\d+([._]\d+)+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionRegex();
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoInstallAll.Models;
using AutoInstallAll.Services.Infrastructure;

namespace AutoInstallAll.Services;

/// <summary>Thông tin bản mới nhất lấy từ Winget / GitHub Releases / HTTP Header.</summary>
public sealed record UpstreamInfo(
    string? Version,
    string DownloadUrl,
    string? Sha256,
    long? ContentLength,
    string? ETag,
    DateTimeOffset? LastModified,
    bool HeaderChanged,
    string Origin);

public sealed record UpdateCheckResult(
    SoftwareItem Item,
    bool UpdateAvailable,
    string? LocalVersion,
    string? LatestVersion,
    string Message,
    Exception? Error = null);

public sealed record UpdateProgress(SoftwareItem Item, string Stage, double Percent);

/// <summary>
/// Luồng nút "Check Latest Versions":
/// 1) Quét FileVersionInfo trong .\Installers\  2) Truy vấn upstream
/// 3) Tải về *.downloading + xác thực dung lượng / SHA-256 / chữ ký file
/// 4) Đổi tên chính thức, xoá bộ cài cũ, cập nhật manifest.json.
/// </summary>
public sealed partial class AutoUpdaterService
{
    private const string Category = "update";
    private readonly StoragePathManager _paths;
    private readonly ManifestConfig _manifest;
    private readonly LogService _log;
    private readonly HttpDownloader _http;
    private readonly WingetClient _winget;
    private readonly ConcurrentDictionary<string, UpstreamInfo> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public event EventHandler<UpdateCheckResult>? ItemChecked;

    public AutoUpdaterService(StoragePathManager paths, ManifestConfig manifest, LogService log,
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
    // Luồng tổng: Check Latest Versions
    // =====================================================================

    public async Task<IReadOnlyList<UpdateCheckResult>> CheckLatestVersionsAsync(
        bool downloadUpdates,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        _log.Info(Category, "===== Check Latest Versions =====");
        await ScanLocalAsync(ct).ConfigureAwait(false);

        var items = _manifest.AllItems.ToList();
        var results = new ConcurrentBag<UpdateCheckResult>();

        // Truy vấn upstream song song (winget / HTTP chậm), tải tuần tự để không nghẽn ghi USB.
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
            async (item, token) =>
            {
                progress?.Report(new UpdateProgress(item, "Checking", 0));
                results.Add(await CheckItemAsync(item, token).ConfigureAwait(false));
            }).ConfigureAwait(false);

        _manifest.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await _manifest.SaveAsync(_paths.ManifestPath, ct).ConfigureAwait(false);

        var ordered = items.Select(i => results.First(r => r.Item == i)).ToList();
        if (!downloadUpdates)
            return ordered;

        var final = new List<UpdateCheckResult>();
        foreach (var r in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (!r.UpdateAvailable || r.Error is not null)
            {
                final.Add(r);
                continue;
            }
            try
            {
                var item = r.Item;
                var p = new Progress<double>(pct => progress?.Report(new UpdateProgress(item, "Downloading", pct)));
                await DownloadLatestAsync(item, p, ct).ConfigureAwait(false);
                final.Add(r with { Message = $"Đã cập nhật lên v{item.LocalVersion}" });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                final.Add(r with { Message = $"Tải thất bại: {ex.Message}", Error = ex });
            }
        }
        _log.Info(Category, $"Hoàn tất: {final.Count(x => x.Error is null && x.UpdateAvailable)} cập nhật, {final.Count(x => x.Error is not null)} lỗi.");
        return final;
    }

    // =====================================================================
    // Bước 1: Quét bộ cài trên USB
    // =====================================================================

    public async Task ScanLocalAsync(CancellationToken ct)
    {
        foreach (var item in _manifest.AllItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ScanItemAsync(item, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn(Category, $"Quét {item.Name} lỗi", ex);
            }
        }
    }

    private async Task ScanItemAsync(SoftwareItem item, CancellationToken ct)
    {
        var path = _paths.InstallerExists(item) ? _paths.GetInstallerPath(item) : FindInstallerByTemplate(item);
        if (path is null)
        {
            if (item.LocalFile is not null)
                _log.Warn(Category, $"{item.Name}: manifest ghi '{item.LocalFile}' nhưng file không còn trên USB.");
            item.LocalFile = null;
            item.State = SoftwareState.Missing;
            item.StatusMessage = "Chưa có bộ cài trên USB";
            return;
        }

        var file = new FileInfo(path);
        var sizeChanged = file.Length != item.FileSize;
        item.LocalFile = file.Name;
        item.LocalVersion = ParseVersionFromFileName(item, file.Name) ?? ReadEmbeddedVersion(path) ?? item.LocalVersion;
        item.FileSize = file.Length;
        if (item.Sha256 is null || sizeChanged)
            item.Sha256 = await StoragePathManager.ComputeSha256Async(path, ct).ConfigureAwait(false);

        item.State = SoftwareState.Ready;
        item.StatusMessage = $"{file.Name} ({file.Length / 1024d / 1024:0.#} MB)";
    }

    /// <summary>Tìm file khớp FileNameTemplate (vd Chrome_v*.msi), chọn bản có version cao nhất.</summary>
    private string? FindInstallerByTemplate(SoftwareItem item)
    {
        var template = string.IsNullOrWhiteSpace(item.FileNameTemplate) ? $"{item.Id}_v{{version}}{item.DefaultExtension()}" : item.FileNameTemplate;
        var idx = template.IndexOf("{version}", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return File.Exists(_paths.GetInstallerPath(template)) ? _paths.GetInstallerPath(template) : null;
        var prefix = template[..idx];
        var suffix = template[(idx + "{version}".Length)..];

        return _paths.EnumerateInstallerFiles()
            .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        f.Name.Length > prefix.Length + suffix.Length)
            .Select(f => (f.FullName, Version: f.Name[prefix.Length..^suffix.Length]))
            .OrderByDescending(x => x.Version, Comparer<string>.Create(SoftwareItem.CompareVersions))
            .Select(x => x.FullName)
            .FirstOrDefault();
    }

    private static string? ParseVersionFromFileName(SoftwareItem item, string fileName)
    {
        var template = item.FileNameTemplate;
        var idx = template.IndexOf("{version}", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var prefix = template[..idx];
            var suffix = template[(idx + "{version}".Length)..];
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                fileName.Length > prefix.Length + suffix.Length)
                return fileName[prefix.Length..^suffix.Length];
        }
        var m = FileNameVersionRegex().Match(fileName);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ReadEmbeddedVersion(string path)
    {
        try
        {
            return InstallerInspector.DetectFileType(path) switch
            {
                InstallerType.Exe => StoragePathManager.ReadFileVersion(path),
                InstallerType.Msi => InstallerInspector.ReadMsiProperty(path, "ProductVersion"),
                _ => null
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    // =====================================================================
    // Bước 2: Truy vấn upstream
    // =====================================================================

    public async Task<UpdateCheckResult> CheckItemAsync(SoftwareItem item, CancellationToken ct)
    {
        var previousState = item.State;
        item.State = SoftwareState.Checking;
        item.StatusMessage = "Đang kiểm tra bản mới...";
        UpdateCheckResult result;
        try
        {
            var upstream = await QueryUpstreamAsync(item, ct).ConfigureAwait(false);
            if (upstream is null)
            {
                item.State = previousState == SoftwareState.Checking ? SoftwareState.Ready : previousState;
                item.StatusMessage = "Ứng dụng nhập thủ công — không tự cập nhật";
                result = new UpdateCheckResult(item, false, item.LocalVersion, item.LocalVersion, item.StatusMessage);
            }
            else
            {
                bool hasLocal = _paths.InstallerExists(item);
                bool update;
                if (!hasLocal)
                    update = true;
                else if (upstream.Version is not null && item.LocalVersion is not null && upstream.Origin != "http-header")
                    update = SoftwareItem.CompareVersions(upstream.Version, item.LocalVersion) > 0;
                else
                    update = upstream.HeaderChanged;

                item.LatestVersion = upstream.Version ?? (update ? DateVersion(upstream.LastModified) : item.LocalVersion);

                if (update)
                {
                    _pending[item.Id] = upstream;
                    item.PendingDownloadUrl = upstream.DownloadUrl;
                    item.PendingSha256 = upstream.Sha256;
                    item.PendingContentLength = upstream.ContentLength ?? 0;
                    item.State = hasLocal ? SoftwareState.UpdateAvailable : SoftwareState.Missing;
                    item.StatusMessage = hasLocal
                        ? $"Có bản mới v{item.LocalVersion} → v{item.LatestVersion}"
                        : $"Chưa có trên USB — sẵn sàng tải v{item.LatestVersion}";
                }
                else
                {
                    _pending.TryRemove(item.Id, out _);
                    // Lưu mốc header lần đầu để những lần sau so sánh ETag / Last-Modified.
                    item.Etag ??= upstream.ETag;
                    item.LastModified ??= upstream.LastModified;
                    item.State = SoftwareState.Latest;
                    item.StatusMessage = "Latest (Mới nhất)";
                }

                _log.Info(Category, $"{item.Name}: local={item.LocalVersion ?? "-"} latest={item.LatestVersion ?? "-"} via {upstream.Origin} → {(update ? "UPDATE" : "latest")}");
                result = new UpdateCheckResult(item, update, item.LocalVersion, item.LatestVersion, item.StatusMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.State = previousState;
            throw;
        }
        catch (Exception ex)
        {
            item.State = _paths.InstallerExists(item) ? SoftwareState.Ready : SoftwareState.Missing;
            item.StatusMessage = $"Không kiểm tra được: {ex.Message}";
            _log.Warn(Category, $"Kiểm tra {item.Name} thất bại", ex);
            result = new UpdateCheckResult(item, false, item.LocalVersion, null, item.StatusMessage, ex);
        }

        ItemChecked?.Invoke(this, result);
        return result;
    }

    private async Task<UpstreamInfo?> QueryUpstreamAsync(SoftwareItem item, CancellationToken ct)
    {
        var src = item.Source;
        switch (src.Mode)
        {
            case UpdateSourceMode.LocalFile:
                return null;

            case UpdateSourceMode.Winget:
                if (!string.IsNullOrWhiteSpace(src.WingetId))
                {
                    var info = await _winget.ShowAsync(src.WingetId, ct).ConfigureAwait(false);
                    if (info?.InstallerUrl is not null)
                        return new UpstreamInfo(info.Version, info.InstallerUrl, info.InstallerSha256, null, null, null, false, "winget");
                }
                if (!string.IsNullOrWhiteSpace(src.DirectUrl))
                    return await QueryHeadersAsync(item, src.DirectUrl, null, ct).ConfigureAwait(false);
                throw new InvalidOperationException("Winget không khả dụng và không có Direct URL dự phòng.");

            case UpdateSourceMode.GitHubRelease:
                return await QueryGitHubAsync(item, ct).ConfigureAwait(false);

            case UpdateSourceMode.DirectUrl:
                if (string.IsNullOrWhiteSpace(src.DirectUrl))
                    throw new InvalidOperationException("Thiếu directUrl trong manifest.json.");
                string? version = null;
                if (!string.IsNullOrWhiteSpace(src.WingetId))
                {
                    try { version = (await _winget.ShowAsync(src.WingetId, ct).ConfigureAwait(false))?.Version; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn(Category, $"winget show {src.WingetId} lỗi", ex); }
                }
                return await QueryHeadersAsync(item, src.DirectUrl, version, ct).ConfigureAwait(false);

            default:
                throw new NotSupportedException($"Nguồn cập nhật không hỗ trợ: {src.Mode}");
        }
    }

    private async Task<UpstreamInfo> QueryHeadersAsync(SoftwareItem item, string url, string? knownVersion, CancellationToken ct)
    {
        var remote = await _http.GetRemoteInfoAsync(url, ct).ConfigureAwait(false);

        bool changed;
        if (item.Etag is not null && remote.ETag is not null)
            changed = !string.Equals(item.Etag, remote.ETag, StringComparison.Ordinal);
        else if (item.LastModified is not null && remote.LastModified is not null)
            changed = remote.LastModified > item.LastModified;
        else if (remote.ContentLength is > 0 && item.FileSize > 0)
            changed = remote.ContentLength != item.FileSize;
        else
            changed = false;

        // Có version từ winget → so sánh version; không có → so sánh header.
        return new UpstreamInfo(knownVersion, url, null, remote.ContentLength, remote.ETag, remote.LastModified,
            changed, knownVersion is null ? "http-header" : "winget+http");
    }

    private async Task<UpstreamInfo> QueryGitHubAsync(SoftwareItem item, CancellationToken ct)
    {
        var repo = item.Source.GithubRepo;
        if (string.IsNullOrWhiteSpace(repo) || !GitHubRepoRegex().IsMatch(repo))
            throw new InvalidOperationException($"githubRepo không hợp lệ: '{repo}'.");

        var json = await _http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct,
            ("Accept", "application/vnd.github+json"), ("X-GitHub-Api-Version", "2022-11-28")).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release thiếu tag_name.");
        var pattern = new Regex(item.Source.GithubAssetPattern ?? @"\.(exe|msi|zip)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!pattern.IsMatch(name)) continue;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..].ToUpperInvariant();

            return new UpstreamInfo(
                tag.TrimStart('v', 'V'),
                asset.GetProperty("browser_download_url").GetString()!,
                sha,
                asset.GetProperty("size").GetInt64(),
                null,
                asset.TryGetProperty("updated_at", out var upd) && upd.TryGetDateTimeOffset(out var dto) ? dto : null,
                false,
                "github");
        }
        throw new InvalidDataException($"Release {tag} của {repo} không có asset khớp '{pattern}'.");
    }

    // =====================================================================
    // Bước 3 + 4: Tải *.downloading → xác thực → thay thế bản cũ
    // =====================================================================

    /// <summary>Tải bản mới nhất vào .\Installers\, trả đường dẫn bộ cài chính thức.</summary>
    public async Task<string> DownloadLatestAsync(SoftwareItem item, IProgress<double>? progress, CancellationToken ct)
    {
        if (!_pending.TryGetValue(item.Id, out var up))
        {
            var check = await CheckItemAsync(item, ct).ConfigureAwait(false);
            if (check.Error is not null)
                throw new InvalidOperationException($"Không lấy được thông tin tải {item.Name}: {check.Error.Message}", check.Error);
            if (!_pending.TryGetValue(item.Id, out up))
                return _paths.GetInstallerPath(item) ?? throw new FileNotFoundException($"{item.Name}: không có bộ cài và không có nguồn tải.");
        }

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        var previousState = item.State;
        string? tmpPath = null;
        try
        {
            var version = up.Version ?? DateVersion(up.LastModified);
            var finalName = item.BuildFileName(version);
            tmpPath = _paths.GetDownloadingPath(finalName);

            var needed = (up.ContentLength ?? 0) + 16 * 1024 * 1024;
            if (_paths.GetAvailableFreeSpace() < needed)
                throw new IOException($"USB không đủ dung lượng trống ({needed / 1024d / 1024:0} MB cần thiết).");

            item.State = SoftwareState.Downloading;
            item.Progress = 0;
            item.StatusMessage = $"Đang tải {Path.GetFileName(tmpPath)}";
            _log.Info(Category, $"{item.Name}: tải {up.DownloadUrl} → {Path.GetFileName(tmpPath)}");

            var reporter = new Progress<double>(p =>
            {
                item.Progress = p;
                progress?.Report(p);
            });
            var dl = await _http.DownloadAsync(up.DownloadUrl, tmpPath, reporter, PerAppTimeout, ct).ConfigureAwait(false);

            // ---- Xác thực ----
            item.State = SoftwareState.Verifying;
            item.StatusMessage = "Đang xác thực SHA-256...";
            if (up.ContentLength is > 0 && dl.Bytes != up.ContentLength)
                throw new InvalidDataException($"Sai dung lượng: {dl.Bytes:N0} ≠ {up.ContentLength:N0} byte.");
            if (_manifest.Settings.VerifySha256 && up.Sha256 is not null &&
                !string.Equals(up.Sha256, dl.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Sai SHA-256: {dl.Sha256} ≠ {up.Sha256}.");
            if (!InstallerInspector.HasValidSignature(tmpPath, item.InstallerType))
                throw new InvalidDataException($"File tải về không phải {item.InstallerType} hợp lệ (máy chủ có thể trả về trang HTML).");

            // Evergreen URL không có version từ upstream → lấy version thật trong file .exe.
            if (up.Version is null && ReadEmbeddedVersion(tmpPath) is { } embedded)
            {
                version = embedded;
                var renamed = _paths.GetDownloadingPath(item.BuildFileName(version));
                if (!string.Equals(renamed, tmpPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(tmpPath, renamed, overwrite: true);
                    tmpPath = renamed;
                }
                finalName = item.BuildFileName(version);
            }

            // ---- Thay thế nguyên tử ----
            var oldFile = item.LocalFile;
            var oldVersion = item.LocalVersion;
            var finalPath = _paths.CommitDownload(tmpPath, finalName,
                _manifest.Settings.DeleteOldInstallerAfterUpdate ? oldFile : null, out var oldDeleted);
            tmpPath = null;

            item.LocalFile = finalName;
            item.LocalVersion = version;
            item.LatestVersion = version;
            item.Sha256 = dl.Sha256;
            item.FileSize = dl.Bytes;
            item.Etag = up.ETag ?? item.Etag;
            item.LastModified = up.LastModified ?? item.LastModified;
            item.PendingDownloadUrl = null;
            item.PendingSha256 = null;
            item.PendingContentLength = 0;
            _pending.TryRemove(item.Id, out _);

            await EnsureIconAsync(item, finalPath).ConfigureAwait(false);
            await _manifest.SaveAsync(_paths.ManifestPath, CancellationToken.None).ConfigureAwait(false);

            item.State = SoftwareState.Latest;
            item.Progress = 100;
            item.StatusMessage = oldFile is null ? $"Đã tải {finalName}" : $"v{oldVersion} → v{version}";
            _log.Info(Category, oldFile is null
                ? $"{item.Name}: đã lưu {finalName} ({dl.Bytes:N0} byte, SHA-256 {dl.Sha256})."
                : $"{item.Name}: {oldFile} → {finalName}; xoá bản cũ: {(oldDeleted ? "OK" : "không (bị khoá hoặc trùng tên)")}.");
            return finalPath;
        }
        catch (Exception ex)
        {
            if (tmpPath is not null)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException) { }
            }
            item.State = previousState is SoftwareState.Downloading or SoftwareState.Verifying ? SoftwareState.UpdateAvailable : previousState;
            item.Progress = 0;
            item.StatusMessage = ex is OperationCanceledException ? "Đã huỷ tải" : $"Lỗi tải: {ex.Message}";
            if (ex is OperationCanceledException)
                _log.Warn(Category, $"{item.Name}: đã huỷ tải, giữ nguyên bộ cài cũ.");
            else
                _log.Error(Category, $"{item.Name}: tải/xác thực thất bại, giữ nguyên bộ cài cũ.", ex);
            throw;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task EnsureIconAsync(SoftwareItem item, string installerPath)
    {
        try
        {
            item.IconFile ??= _paths.GetDefaultIconFileName(item);
            var iconPath = _paths.GetIconPath(item.IconFile);
            if (!File.Exists(iconPath) && item.InstallerType == InstallerType.Exe)
                await ShellInterop.ExtractIconToPngAsync(installerPath, iconPath).ConfigureAwait(false);
            _paths.AttachIcon(item);
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"{item.Name}: không trích được icon", ex);
        }
    }

    private static string DateVersion(DateTimeOffset? lastModified) =>
        (lastModified ?? DateTimeOffset.Now).ToString("yyyy.MM.dd");

    [GeneratedRegex(@"_v(\d[\w.\-]*)\.(exe|msi|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameVersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$")]
    private static partial Regex GitHubRepoRegex();
}

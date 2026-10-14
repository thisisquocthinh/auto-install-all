using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AutoInstallAll.Services.Infrastructure;

public sealed record WingetSearchResult(string Name, string Id, string Version, string? Source);

public sealed record WingetPackageInfo(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    string? InstallerType,
    string? InstallerUrl,
    string? InstallerSha256,
    string? Description);

/// <summary>Gọi winget.exe (App Installer) để tìm gói và đọc Installer Url / SHA-256 / phiên bản.</summary>
public sealed partial class WingetClient
{
    private const string Category = "winget";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private readonly LogService _log;
    private bool? _available;

    public WingetClient(LogService log) => _log = log;

    private static string Executable
    {
        get
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias) ? alias : "winget.exe";
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_available.HasValue) return _available.Value;
        try
        {
            var r = await ProcessRunner.RunAsync(Executable, "--version", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            _available = r.Success && r.StdOut.TrimStart().StartsWith('v');
        }
        catch (Win32Exception)
        {
            _available = false;
        }
        if (_available == false)
            _log.Warn(Category, "Không tìm thấy winget (App Installer) trên máy này.");
        return _available.Value;
    }

    public async Task<IReadOnlyList<WingetSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Replace("\"", "").Trim();
        if (query.Length == 0) return Array.Empty<WingetSearchResult>();
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Máy này chưa có winget. Hãy dùng chế độ Dán link tải hoặc Import file.");

        var r = await ProcessRunner.RunAsync(Executable,
            $"search --query {ProcessRunner.Quote(query)} --source winget --accept-source-agreements",
            CommandTimeout, ct).ConfigureAwait(false);
        if (r.TimedOut)
            throw new TimeoutException("winget search quá thời gian.");

        var results = ParseTable(r.StdOut);
        _log.Info(Category, $"search \"{query}\" → {results.Count} kết quả (exit {r.ExitCode}).");
        return results;
    }

    public async Task<WingetPackageInfo?> ShowAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"Winget ID không hợp lệ: {id}", nameof(id));
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return null;

        var baseArgs = $"show --id {ProcessRunner.Quote(id)} --exact --source winget --accept-source-agreements";
        var r = await ProcessRunner.RunAsync(Executable, baseArgs + " --architecture x64", CommandTimeout, ct).ConfigureAwait(false);
        if (!r.Success)
            r = await ProcessRunner.RunAsync(Executable, baseArgs, CommandTimeout, ct).ConfigureAwait(false);
        if (!r.Success)
        {
            _log.Warn(Category, $"show {id} thất bại (exit {r.ExitCode}, timeout={r.TimedOut}).");
            return null;
        }

        var info = ParseShow(id, r.StdOut);
        _log.Info(Category, $"show {id}: v{info.Version}, type={info.InstallerType}, url={info.InstallerUrl}");
        return info;
    }

    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    // ------------------------ Parsers ------------------------

    /// <summary>Bỏ ký tự spinner/tiến độ: winget ghi đè dòng bằng '\r'.</summary>
    private static IEnumerable<string> CleanLines(string output) =>
        output.Replace("\r\n", "\n").Split('\n')
            .Select(l => { var i = l.LastIndexOf('\r'); return i >= 0 ? l[(i + 1)..] : l; })
            .Select(l => AnsiRegex().Replace(l, ""));

    internal static List<WingetSearchResult> ParseTable(string output)
    {
        var lines = CleanLines(output).ToList();
        var dashIndex = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
        var results = new List<WingetSearchResult>();
        if (dashIndex <= 0) return results;

        var header = lines[dashIndex - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        if (starts.Count < 3) return results;

        int idStart = starts[1];
        bool hasSource = starts.Count >= 4;
        foreach (var line in lines.Skip(dashIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length <= idStart) continue;
            var name = line[..idStart].Trim();
            var rest = line[idStart..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length < 2) continue;
            var id = rest[0];
            if (id.Contains('…') || !IsValidId(id)) continue; // ID bị cắt do độ rộng cột
            var source = hasSource && rest.Length >= 3 ? rest[^1] : null;
            results.Add(new WingetSearchResult(name, id, rest[1], source));
        }
        return results;
    }

    internal static WingetPackageInfo ParseShow(string id, string output)
    {
        var lines = CleanLines(output).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        string name = id;
        var header = lines.Select(l => HeaderRegex().Match(l)).FirstOrDefault(m => m.Success);
        if (header is not null)
            name = header.Groups[1].Value.Trim();

        var top = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();
        foreach (var line in lines)
        {
            var m = KeyValueRegex().Match(line);
            if (!m.Success) continue;
            var key = m.Groups[2].Value.Trim();
            var value = m.Groups[3].Value.Trim();
            var target = m.Groups[1].Value.Length > 0 ? installer : top;
            target.TryAdd(key, value);
            if (m.Groups[1].Value.Length > 0 && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                urls.Add(value);
        }

        string? Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        var version = Get(top, "Version") ?? top.Values.FirstOrDefault(v => VersionRegex().IsMatch(v));
        var url = Get(installer, "Installer Url") ?? urls.LastOrDefault();
        var sha = Get(installer, "Installer SHA256") ?? installer.Values.FirstOrDefault(v => ShaRegex().IsMatch(v));

        return new WingetPackageInfo(
            id, name,
            Get(top, "Publisher"),
            version,
            Get(installer, "Installer Type") ?? Get(installer, "Type"),
            url,
            sha?.ToUpperInvariant(),
            Get(top, "Description"));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{1,127}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^\S.*?\s(.+)\s\[([^\]]+)\]\s*$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(\s*)([^:]{2,40}):\s*(.*)$")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"^\d+(\.\d+)+")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$")]
    private static partial Regex ShaRegex();
}

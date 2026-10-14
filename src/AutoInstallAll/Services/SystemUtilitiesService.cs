using System.Text.Json;
using System.Text.RegularExpressions;
using AutoInstallAll.Models;
using AutoInstallAll.Services.Infrastructure;

namespace AutoInstallAll.Services;

public sealed record BloatwareRemovalResult(string PackageName, bool Removed, string Message);

/// <summary>
/// Tiện ích hệ thống (yêu cầu quyền Admin): BitLocker, Quick Split C/D, VC++ AIO &amp; DirectX, gỡ Bloatware.
/// </summary>
public sealed partial class SystemUtilitiesService
{
    private const string BitLockerCategory = "bitlocker";
    private const string DiskCategory = "disk";
    private const string SystemCategory = "system";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    public const string VcRedistId = "vcredist-aio";
    public const string DirectXId = "directx-runtime";

    /// <summary>Ứng dụng Appx cài sẵn thường được gỡ trên máy mới (không gồm Store, Photos, Calculator, Media Player, Xbox).</summary>
    public static readonly IReadOnlyList<string> DefaultBloatware =
    [
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.People",
        "Microsoft.WindowsFeedbackHub",
        "Microsoft.WindowsMaps",
        "Microsoft.ZuneVideo",
        "Microsoft.SkypeApp",
        "Microsoft.Microsoft3DViewer",
        "Microsoft.MixedReality.Portal",
        "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop",
        "Microsoft.549981C3F5F10",
        "MicrosoftCorporationII.MicrosoftFamily",
        "Clipchamp.Clipchamp"
    ];

    private readonly StoragePathManager _paths;
    private readonly ManifestConfig _manifest;
    private readonly LogService _log;
    private readonly SilentInstallService _installer;

    public SystemUtilitiesService(StoragePathManager paths, ManifestConfig manifest, LogService log, SilentInstallService installer)
    {
        _paths = paths;
        _manifest = manifest;
        _log = log;
        _installer = installer;
    }

    // =====================================================================
    // BitLocker
    // =====================================================================

    public async Task<BitLockerStatus> GetBitLockerStatusAsync(string mountPoint, CancellationToken ct)
    {
        mountPoint = NormalizeMountPoint(mountPoint);
        try
        {
            var r = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("manage-bde.exe"), $"-status {mountPoint}", QueryTimeout, ct)
                .ConfigureAwait(false);
            if (!r.TimedOut)
            {
                var status = BitLockerStatus.ParseManageBde(mountPoint, r.StdOut + r.StdErr);
                if (status.Conversion != BitLockerConversionState.Unknown)
                    return status;
            }
            _log.Warn(BitLockerCategory, $"manage-bde -status {mountPoint} không phân tích được (exit {r.ExitCode}) → dùng WMI.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Warn(BitLockerCategory, "Không chạy được manage-bde.exe → dùng WMI.", ex);
        }

        return await GetBitLockerStatusViaWmiAsync(mountPoint, ct).ConfigureAwait(false);
    }

    /// <summary>Win32_EncryptableVolume — số liệu không phụ thuộc ngôn ngữ Windows (Win Home tiếng Việt...).</summary>
    private async Task<BitLockerStatus> GetBitLockerStatusViaWmiAsync(string mountPoint, CancellationToken ct)
    {
        var script = $$"""
            $v = Get-CimInstance -Namespace 'root/cimv2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -Filter "DriveLetter='{{mountPoint}}'" -ErrorAction SilentlyContinue
            if (-not $v) { '{"supported":false}'; exit 0 }
            $c = Invoke-CimMethod -InputObject $v -MethodName GetConversionStatus
            $p = Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus
            $m = Invoke-CimMethod -InputObject $v -MethodName GetEncryptionMethod
            [pscustomobject]@{ supported = $true; conversion = [int]$c.ConversionStatus; percent = [double]$c.EncryptionPercentage; protection = [int]$p.ProtectionStatus; method = [int]$m.EncryptionMethod } | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.RunPowerShellAsync(script, QueryTimeout, ct).ConfigureAwait(false);
        var json = LastJsonLine(r.StdOut);
        if (json is null)
        {
            _log.Warn(BitLockerCategory, $"WMI BitLocker không có kết quả: {r.StdErr.Trim()}");
            return BitLockerStatus.NotSupported(mountPoint, r.StdOut + r.StdErr);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.GetProperty("supported").GetBoolean())
            return BitLockerStatus.NotSupported(mountPoint, json);

        var conversion = root.GetProperty("conversion").GetInt32() switch
        {
            0 => BitLockerConversionState.FullyDecrypted,
            1 => BitLockerConversionState.FullyEncrypted,
            2 => BitLockerConversionState.EncryptionInProgress,
            3 => BitLockerConversionState.DecryptionInProgress,
            4 => BitLockerConversionState.EncryptionPaused,
            5 => BitLockerConversionState.DecryptionPaused,
            _ => BitLockerConversionState.Unknown
        };
        var protection = root.GetProperty("protection").GetInt32() switch
        {
            0 => BitLockerProtectionState.Off,
            1 => BitLockerProtectionState.On,
            _ => BitLockerProtectionState.Unknown
        };
        if (conversion == BitLockerConversionState.FullyEncrypted && protection == BitLockerProtectionState.Off)
            conversion = BitLockerConversionState.WaitingForActivation;

        return new BitLockerStatus
        {
            MountPoint = mountPoint,
            Conversion = conversion,
            Protection = protection,
            EncryptionPercentage = root.GetProperty("percent").GetDouble(),
            EncryptionMethod = root.GetProperty("method").GetInt32() switch
            {
                0 => "None",
                3 => "AES-128",
                4 => "AES-256",
                6 => "XTS-AES-128",
                7 => "XTS-AES-256",
                var n => $"Method {n}"
            },
            RawOutput = json
        };
    }

    /// <summary>
    /// Chạy manage-bde -off rồi theo dõi tiến độ giải mã định kỳ (Decrypting xx% → OFF).
    /// Huỷ <paramref name="ct"/> chỉ dừng theo dõi — Windows vẫn tiếp tục giải mã nền.
    /// </summary>
    public async Task<BitLockerStatus> DisableBitLockerAsync(
        string mountPoint,
        IProgress<BitLockerStatus>? progress,
        CancellationToken ct,
        TimeSpan? pollInterval = null)
    {
        mountPoint = NormalizeMountPoint(mountPoint);
        var status = await GetBitLockerStatusAsync(mountPoint, ct).ConfigureAwait(false);
        progress?.Report(status);
        _log.Info(BitLockerCategory, $"{mountPoint} trước khi tắt: {status.DisplayText} ({status.Conversion}, protection {status.Protection})");

        if (status.Conversion == BitLockerConversionState.NotSupported)
            throw new NotSupportedException($"Ổ {mountPoint} không hỗ trợ BitLocker / Device Encryption.");
        if (status.IsOff)
            return status;

        if (!status.IsDecrypting)
        {
            var r = await ProcessRunner.RunAsync(ProcessRunner.SystemExe("manage-bde.exe"), $"-off {mountPoint}", CommandTimeout, ct)
                .ConfigureAwait(false);
            _log.Info(BitLockerCategory, $"manage-bde -off {mountPoint} → exit {r.ExitCode}\n{r.StdOut.Trim()}");

            if (!r.Success)
            {
                _log.Warn(BitLockerCategory, "manage-bde -off thất bại → thử Disable-BitLocker.");
                var ps = await ProcessRunner.RunPowerShellAsync(
                    $"Disable-BitLocker -MountPoint {ProcessRunner.PsQuote(mountPoint)} | Out-Null", CommandTimeout, ct).ConfigureAwait(false);
                if (!ps.Success)
                    throw new InvalidOperationException($"Không tắt được BitLocker: {(r.StdOut + r.StdErr + ps.StdErr).Trim()}");
            }
        }
        else if (status.Conversion == BitLockerConversionState.DecryptionPaused)
        {
            // Tiếp tục giải mã bị tạm dừng.
            await ProcessRunner.RunAsync(ProcessRunner.SystemExe("manage-bde.exe"), $"-resume {mountPoint}", CommandTimeout, ct)
                .ConfigureAwait(false);
        }

        var interval = pollInterval ?? DefaultPollInterval;
        double lastLogged = -1;
        while (true)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            status = await GetBitLockerStatusAsync(mountPoint, ct).ConfigureAwait(false);
            progress?.Report(status);

            if (Math.Abs(status.DecryptionProgress - lastLogged) >= 5 || status.IsOff)
            {
                _log.Info(BitLockerCategory, $"{mountPoint}: {status.DisplayText}");
                lastLogged = status.DecryptionProgress;
            }
            if (status.IsOff)
                return status;
            if (status.Conversion is BitLockerConversionState.EncryptionInProgress or BitLockerConversionState.FullyEncrypted)
                throw new InvalidOperationException($"Ổ {mountPoint} chưa chuyển sang giải mã (trạng thái: {status.DisplayText}).");
        }
    }

    // =====================================================================
    // Ổ đĩa & Quick Split C/D
    // =====================================================================

    public async Task<DiskInfo> GetSystemDiskAsync(CancellationToken ct)
    {
        const string script = """
            $c = Get-Partition -DriveLetter C
            $d = Get-Disk -Number $c.DiskNumber
            $sup = Get-PartitionSupportedSize -DiskNumber $c.DiskNumber -PartitionNumber $c.PartitionNumber
            $parts = @(Get-Partition -DiskNumber $c.DiskNumber | Sort-Object Offset | ForEach-Object {
                $letter = $null; $vol = $null
                if ([int][char]$_.DriveLetter -ne 0) {
                    $letter = [string]$_.DriveLetter
                    $vol = Get-Volume -DriveLetter $_.DriveLetter -ErrorAction SilentlyContinue
                }
                $isC = ($_.PartitionNumber -eq $c.PartitionNumber)
                [pscustomobject]@{
                    DiskNumber = [int]$_.DiskNumber
                    PartitionNumber = [int]$_.PartitionNumber
                    DriveLetter = $letter
                    Label = $(if ($vol) { [string]$vol.FileSystemLabel } else { $null })
                    FileSystem = $(if ($vol) { [string]$vol.FileSystem } else { $null })
                    PartitionType = [string]$_.Type
                    IsBoot = [bool]$_.IsBoot
                    IsSystem = [bool]$_.IsSystem
                    Offset = [int64]$_.Offset
                    Size = [int64]$_.Size
                    Free = $(if ($vol) { [int64]$vol.SizeRemaining } else { [int64]0 })
                    SupMin = $(if ($isC) { [int64]$sup.SizeMin } else { [int64]0 })
                    SupMax = $(if ($isC) { [int64]$sup.SizeMax } else { [int64]0 })
                }
            })
            [pscustomobject]@{
                Number = [int]$d.Number
                FriendlyName = [string]$d.FriendlyName
                PartitionStyle = [string]$d.PartitionStyle
                BusType = [string]$d.BusType
                Size = [int64]$d.Size
                Allocated = [int64]$d.AllocatedSize
                Partitions = $parts
            } | ConvertTo-Json -Depth 4 -Compress
            """;
        var r = await ProcessRunner.RunPowerShellAsync(script, QueryTimeout, ct).ConfigureAwait(false);
        var json = LastJsonLine(r.StdOut);
        if (!r.Success || json is null)
            throw new InvalidOperationException($"Không đọc được thông tin ổ đĩa: {r.StdErr.Trim()}");

        var dto = JsonSerializer.Deserialize<DiskDto>(json, JsonOpts)
                  ?? throw new InvalidDataException("Dữ liệu ổ đĩa rỗng.");
        var disk = new DiskInfo
        {
            Number = dto.Number,
            FriendlyName = dto.FriendlyName,
            PartitionStyle = dto.PartitionStyle,
            BusType = dto.BusType,
            SizeBytes = dto.Size,
            UnallocatedBytes = Math.Max(0, dto.Size - dto.Allocated),
            Partitions = dto.Partitions.Select(p => new DiskPartitionInfo
            {
                DiskNumber = p.DiskNumber,
                PartitionNumber = p.PartitionNumber,
                DriveLetter = string.IsNullOrEmpty(p.DriveLetter) ? null : p.DriveLetter[0],
                Label = p.Label,
                FileSystem = p.FileSystem,
                PartitionType = p.PartitionType,
                IsBoot = p.IsBoot,
                IsSystem = p.IsSystem,
                OffsetBytes = p.Offset,
                SizeBytes = p.Size,
                FreeBytes = p.Free,
                SupportedMinBytes = p.SupMin,
                SupportedMaxBytes = p.SupMax
            }).ToList()
        };
        _log.Info(DiskCategory, $"Disk {disk.Number} {disk.FriendlyName} ({disk.PartitionStyle}, {disk.BusType}) {disk.SizeGB} GB: " +
                                string.Join(" | ", disk.Partitions.Select(p => $"#{p.PartitionNumber} {p.DriveLetter?.ToString() ?? "-"} {p.PartitionType} {p.SizeGB}GB")));
        return disk;
    }

    public SplitPlan CreateSplitPlan(DiskInfo disk, double dataSizeGB, char? driveLetter = null, string? label = null)
    {
        var c = disk.SystemDrive ?? throw new InvalidOperationException("Không tìm thấy phân vùng C:.");
        return new SplitPlan
        {
            Source = c,
            NewDataSizeBytes = (long)(dataSizeGB * DiskPartitionInfo.GB),
            NewDriveLetter = char.ToUpperInvariant(driveLetter ?? _manifest.Settings.DefaultDataDriveLetter.FirstOrDefault('D')),
            NewLabel = SanitizeLabel(label ?? _manifest.Settings.DefaultDataDriveLabel),
            MinFreeAfterSplitBytes = _manifest.Settings.MinFreeSpaceForSplitGB * DiskPartitionInfo.GB
        };
    }

    public IReadOnlyList<char> GetDriveLettersInUse() =>
        DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToList();

    /// <summary>
    /// Thu nhỏ C: (Resize-Partition) rồi tạo phân vùng mới ngay sau C: (New-Partition + Format-Volume NTFS).
    /// Không xoá / di chuyển dữ liệu — chỉ dùng vùng trống phía cuối C:.
    /// </summary>
    public async Task<DiskPartitionInfo> QuickSplitAsync(SplitPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        // Đọc lại số liệu thực tế ngay trước khi chia.
        progress?.Report("Đang kiểm tra ổ đĩa...");
        var disk = await GetSystemDiskAsync(ct).ConfigureAwait(false);
        var current = disk.SystemDrive ?? throw new InvalidOperationException("Không tìm thấy C:.");
        if (current.PartitionNumber != plan.Source.PartitionNumber || current.DiskNumber != plan.Source.DiskNumber)
            throw new InvalidOperationException("Cấu trúc phân vùng đã thay đổi — hãy tải lại thông tin ổ đĩa.");
        plan = plan with { Source = current };

        var errors = plan.Validate(GetDriveLettersInUse());
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        if (string.Equals(disk.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase) && disk.Partitions.Count >= 4)
            throw new InvalidOperationException("Ổ MBR đã đủ 4 phân vùng chính — không thể tạo thêm.");

        var bl = await GetBitLockerStatusAsync("C:", ct).ConfigureAwait(false);
        if (bl.Conversion is BitLockerConversionState.DecryptionInProgress or BitLockerConversionState.EncryptionInProgress)
            throw new InvalidOperationException($"BitLocker đang {bl.DisplayText} — chờ hoàn tất rồi mới chia ổ.");

        const long MB = 1024 * 1024;
        long newSystemSize = plan.NewSystemSizeBytes / MB * MB;
        _log.Info(DiskCategory, $"Quick Split: C: {current.SizeGB} GB → {newSystemSize / (double)DiskPartitionInfo.GB:0.#} GB, " +
                                $"tạo {plan.NewDriveLetter}: '{plan.NewLabel}' ~{plan.NewDataSizeGB} GB (đã dùng {current.UsedGB} GB).");

        var script = $"""
            $disk = {current.DiskNumber}
            $num = {current.PartitionNumber}
            Write-Output 'STEP:Đang thu nhỏ ổ C:...'
            Resize-Partition -DiskNumber $disk -PartitionNumber $num -Size {newSystemSize}
            $c = Get-Partition -DiskNumber $disk -PartitionNumber $num
            $start = [int64]$c.Offset + [int64]$c.Size
            $next = Get-Partition -DiskNumber $disk | Where-Object {"{"} $_.Offset -gt $c.Offset {"}"} | Sort-Object Offset | Select-Object -First 1
            if ($next) {"{"} $end = [int64]$next.Offset {"}"} else {"{"} $end = [int64](Get-Disk -Number $disk).Size - 1MB {"}"}
            $size = [int64]([math]::Floor(($end - $start) / 1MB) * 1MB)
            if ($size -lt 1GB) {"{"} throw "Vùng trống sau C: quá nhỏ ($size byte)." {"}"}
            Write-Output 'STEP:Đang tạo phân vùng {plan.NewDriveLetter}:...'
            $p = New-Partition -DiskNumber $disk -Offset $start -Size $size -DriveLetter {plan.NewDriveLetter}
            Write-Output 'STEP:Đang định dạng NTFS...'
            Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel {ProcessRunner.PsQuote(plan.NewLabel)} -Confirm:$false -Force | Out-Null
            Write-Output ('DONE:' + $p.PartitionNumber)
            """;

        var r = await ProcessRunner.RunPowerShellAsync(script, CommandTimeout, ct, line =>
        {
            if (line.StartsWith("STEP:", StringComparison.Ordinal))
            {
                progress?.Report(line[5..]);
                _log.Info(DiskCategory, line[5..]);
            }
        }).ConfigureAwait(false);

        if (r.TimedOut)
            throw new TimeoutException("Chia ổ quá 10 phút — kiểm tra Disk Management trước khi thử lại.");
        if (!r.Success || !r.StdOut.Contains("DONE:", StringComparison.Ordinal))
        {
            _log.Error(DiskCategory, $"Quick Split thất bại (exit {r.ExitCode}): {r.StdErr.Trim()}");
            throw new InvalidOperationException($"Chia ổ thất bại: {FirstErrorLine(r.StdErr)}");
        }

        progress?.Report("Hoàn tất");
        var after = await GetSystemDiskAsync(ct).ConfigureAwait(false);
        var created = after.Partitions.FirstOrDefault(p => p.DriveLetter == plan.NewDriveLetter)
                      ?? throw new InvalidOperationException("Đã chia nhưng không tìm thấy phân vùng mới.");
        _log.Info(DiskCategory, $"Quick Split OK: C: {after.SystemDrive?.SizeGB} GB, {created.DisplayName}.");
        return created;
    }

    // =====================================================================
    // Visual C++ AIO & DirectX
    // =====================================================================

    public Task<InstallResult> InstallVisualCppAsync(IProgress<double>? progress, CancellationToken ct) =>
        _installer.InstallAsync(RequireTool(VcRedistId), progress, ct);

    public Task<InstallResult> InstallDirectXAsync(IProgress<double>? progress, CancellationToken ct) =>
        _installer.InstallAsync(RequireTool(DirectXId), progress, ct);

    public Task<IReadOnlyList<InstallResult>> InstallRuntimesAsync(IProgress<InstallQueueProgress>? progress, CancellationToken ct) =>
        _installer.InstallQueueAsync([RequireTool(VcRedistId), RequireTool(DirectXId)], progress, ct);

    private SoftwareItem RequireTool(string id) =>
        _manifest.FindById(id) ?? throw new InvalidOperationException($"manifest.json thiếu mục '{id}' trong systemTools.");

    // =====================================================================
    // Clean Bloatware
    // =====================================================================

    public async Task<IReadOnlyList<BloatwareRemovalResult>> RemoveBloatwareAsync(
        IEnumerable<string>? packageNames,
        IProgress<BloatwareRemovalResult>? progress,
        CancellationToken ct)
    {
        var names = (packageNames ?? DefaultBloatware).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names)
            if (!PackageNameRegex().IsMatch(n))
                throw new ArgumentException($"Tên gói không hợp lệ: {n}");

        var list = string.Join(",", names.Select(ProcessRunner.PsQuote));
        var script = $$"""
            $names = @({{list}})
            $provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue)
            foreach ($n in $names) {
                $removed = $false; $msg = ''
                try {
                    $pk = @(Get-AppxPackage -AllUsers -Name $n -ErrorAction SilentlyContinue)
                    foreach ($p in $pk) { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop; $removed = $true }
                    $pp = @($provisioned | Where-Object { $_.DisplayName -eq $n })
                    foreach ($x in $pp) { Remove-AppxProvisionedPackage -Online -PackageName $x.PackageName -ErrorAction Stop | Out-Null; $removed = $true }
                    if ($pk.Count -eq 0 -and $pp.Count -eq 0) { $msg = 'Không có trên máy' } else { $msg = 'Đã gỡ' }
                } catch { $msg = $_.Exception.Message }
                [pscustomobject]@{ PackageName = $n; Removed = $removed; Message = $msg } | ConvertTo-Json -Compress
            }
            """;

        var results = new List<BloatwareRemovalResult>();
        _log.Info(SystemCategory, $"Clean Bloatware: {names.Count} gói.");
        var r = await ProcessRunner.RunPowerShellAsync(script, CommandTimeout, ct, line =>
        {
            if (!line.StartsWith('{')) return;
            try
            {
                var item = JsonSerializer.Deserialize<BloatwareRemovalResult>(line, JsonOpts);
                if (item is null) return;
                lock (results) results.Add(item);
                progress?.Report(item);
                _log.Info(SystemCategory, $"  {item.PackageName}: {item.Message}");
            }
            catch (JsonException ex)
            {
                _log.Warn(SystemCategory, $"Không đọc được kết quả: {line}", ex);
            }
        }).ConfigureAwait(false);

        if (r.TimedOut)
            _log.Warn(SystemCategory, "Clean Bloatware quá 10 phút — đã dừng.");
        else if (!r.Success)
            _log.Warn(SystemCategory, $"Clean Bloatware kết thúc với exit {r.ExitCode}: {FirstErrorLine(r.StdErr)}");

        lock (results)
        {
            _log.Info(SystemCategory, $"Clean Bloatware xong: gỡ {results.Count(x => x.Removed)}/{names.Count}.");
            return results.ToList();
        }
    }

    // ---------------- Tiện ích ----------------

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string NormalizeMountPoint(string mountPoint)
    {
        var m = mountPoint.Trim().TrimEnd('\\').ToUpperInvariant();
        if (m.Length == 1) m += ":";
        if (!MountPointRegex().IsMatch(m))
            throw new ArgumentException($"Ổ đĩa không hợp lệ: {mountPoint}", nameof(mountPoint));
        return m;
    }

    private static string SanitizeLabel(string label)
    {
        var cleaned = new string(label.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-').ToArray()).Trim();
        if (cleaned.Length > 32) cleaned = cleaned[..32];
        return cleaned.Length == 0 ? "DATA" : cleaned;
    }

    private static string? LastJsonLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{') && l.EndsWith('}'));

    private static string FirstErrorLine(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "không rõ lỗi";

    [GeneratedRegex("^[A-Z]:$")]
    private static partial Regex MountPointRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{1,127}$")]
    private static partial Regex PackageNameRegex();

    private sealed class DiskDto
    {
        public int Number { get; set; }
        public string? FriendlyName { get; set; }
        public string? PartitionStyle { get; set; }
        public string? BusType { get; set; }
        public long Size { get; set; }
        public long Allocated { get; set; }
        public List<PartitionDto> Partitions { get; set; } = new();
    }

    private sealed class PartitionDto
    {
        public int DiskNumber { get; set; }
        public int PartitionNumber { get; set; }
        public string? DriveLetter { get; set; }
        public string? Label { get; set; }
        public string? FileSystem { get; set; }
        public string? PartitionType { get; set; }
        public bool IsBoot { get; set; }
        public bool IsSystem { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        public long SupMin { get; set; }
        public long SupMax { get; set; }
    }
}

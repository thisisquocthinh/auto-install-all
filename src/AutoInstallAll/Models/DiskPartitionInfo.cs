namespace AutoInstallAll.Models;

/// <summary>Thông tin một phân vùng (map từ Get-Partition + Get-Volume).</summary>
public sealed record DiskPartitionInfo
{
    public const long GB = 1024L * 1024 * 1024;

    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public char? DriveLetter { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }

    /// <summary>GPT type / MBR type ("Basic", "System", "Recovery", "Reserved"...).</summary>
    public string? PartitionType { get; init; }

    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }

    public long SizeBytes { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>Kết quả Get-PartitionSupportedSize.</summary>
    /// <summary>Vị trí bắt đầu (byte) trên đĩa — dùng để tạo phân vùng ngay sau C:.</summary>
    public long OffsetBytes { get; init; }

    public long SupportedMinBytes { get; init; }
    public long SupportedMaxBytes { get; init; }

    public long UsedBytes => Math.Max(0, SizeBytes - FreeBytes);
    public double SizeGB => Math.Round(SizeBytes / (double)GB, 1);
    public double FreeGB => Math.Round(FreeBytes / (double)GB, 1);
    public double UsedGB => Math.Round(UsedBytes / (double)GB, 1);
    public double UsedPercent => SizeBytes == 0 ? 0 : UsedBytes * 100.0 / SizeBytes;

    public string DisplayName => DriveLetter is { } l ? $"Drive {l}: ({SizeGB:0} GB)" : $"Partition {PartitionNumber} ({SizeGB:0} GB)";
}

/// <summary>Thông tin ổ đĩa vật lý chứa C: và danh sách phân vùng.</summary>
public sealed record DiskInfo
{
    public int Number { get; init; }
    public string? FriendlyName { get; init; }
    public string? PartitionStyle { get; init; } // GPT / MBR
    public string? BusType { get; init; }        // NVMe / SATA / USB
    public long SizeBytes { get; init; }
    public long UnallocatedBytes { get; init; }
    public IReadOnlyList<DiskPartitionInfo> Partitions { get; init; } = Array.Empty<DiskPartitionInfo>();

    public double SizeGB => Math.Round(SizeBytes / (double)DiskPartitionInfo.GB, 1);

    public DiskPartitionInfo? SystemDrive =>
        Partitions.FirstOrDefault(p => p.DriveLetter is 'C' or 'c');
}

/// <summary>Kế hoạch Quick Split C/D và kết quả kiểm tra an toàn trước khi thực thi.</summary>
public sealed record SplitPlan
{
    public required DiskPartitionInfo Source { get; init; }
    public long NewDataSizeBytes { get; init; }
    public char NewDriveLetter { get; init; } = 'D';
    public string NewLabel { get; init; } = "DATA";
    public long MinFreeAfterSplitBytes { get; init; }

    /// <summary>Kích thước mới của C: sau khi thu nhỏ.</summary>
    public long NewSystemSizeBytes => Source.SizeBytes - NewDataSizeBytes;

    public double NewSystemSizeGB => Math.Round(NewSystemSizeBytes / (double)DiskPartitionInfo.GB, 1);
    public double NewDataSizeGB => Math.Round(NewDataSizeBytes / (double)DiskPartitionInfo.GB, 1);

    /// <summary>Trả về danh sách lỗi; rỗng nghĩa là kế hoạch an toàn.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<char> lettersInUse)
    {
        var errors = new List<string>();
        if (NewDataSizeBytes < DiskPartitionInfo.GB)
            errors.Add("Dung lượng ổ mới phải từ 1 GB trở lên.");
        if (NewSystemSizeBytes < Source.SupportedMinBytes)
            errors.Add($"Không thể thu nhỏ C: dưới {Source.SupportedMinBytes / (double)DiskPartitionInfo.GB:0.#} GB (giới hạn Get-PartitionSupportedSize).");
        if (NewSystemSizeBytes - Source.UsedBytes < MinFreeAfterSplitBytes)
            errors.Add($"Sau khi chia, C: chỉ còn trống {(NewSystemSizeBytes - Source.UsedBytes) / (double)DiskPartitionInfo.GB:0.#} GB, yêu cầu tối thiểu {MinFreeAfterSplitBytes / (double)DiskPartitionInfo.GB:0} GB.");
        if (!char.IsAsciiLetter(NewDriveLetter))
            errors.Add("Ký tự ổ đĩa không hợp lệ.");
        else if (lettersInUse.Any(l => char.ToUpperInvariant(l) == char.ToUpperInvariant(NewDriveLetter)))
            errors.Add($"Ký tự ổ {char.ToUpperInvariant(NewDriveLetter)}: đã được sử dụng.");
        return errors;
    }
}

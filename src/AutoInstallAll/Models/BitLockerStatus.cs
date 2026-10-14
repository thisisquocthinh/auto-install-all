using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoInstallAll.Models;

/// <summary>Map từ "Conversion Status" của manage-bde -status / VolumeStatus của Get-BitLockerVolume.</summary>
public enum BitLockerConversionState
{
    Unknown,
    FullyDecrypted,
    FullyEncrypted,
    EncryptionInProgress,
    DecryptionInProgress,
    EncryptionPaused,
    DecryptionPaused,
    /// <summary>Win11 Home "Device Encryption" chờ kích hoạt (đã mã hoá nhưng key chưa sao lưu).</summary>
    WaitingForActivation,
    NotSupported
}

public enum BitLockerProtectionState
{
    Unknown,
    Off,
    On
}

/// <summary>Trạng thái BitLocker của một volume, hiển thị trên widget "System Security &amp; BitLocker".</summary>
public sealed partial record BitLockerStatus
{
    public string MountPoint { get; init; } = "C:";
    public BitLockerConversionState Conversion { get; init; } = BitLockerConversionState.Unknown;
    public BitLockerProtectionState Protection { get; init; } = BitLockerProtectionState.Unknown;

    /// <summary>"Percentage Encrypted" 0–100.</summary>
    public double EncryptionPercentage { get; init; }

    public string? EncryptionMethod { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public string? RawOutput { get; init; }

    public bool IsOff => Conversion == BitLockerConversionState.FullyDecrypted;
    public bool IsDecrypting => Conversion is BitLockerConversionState.DecryptionInProgress or BitLockerConversionState.DecryptionPaused;
    public bool CanDisable => Conversion is BitLockerConversionState.FullyEncrypted
        or BitLockerConversionState.EncryptionInProgress
        or BitLockerConversionState.EncryptionPaused
        or BitLockerConversionState.WaitingForActivation;

    /// <summary>Tiến độ giải mã (100 - % đã mã hoá).</summary>
    public double DecryptionProgress => Math.Clamp(100 - EncryptionPercentage, 0, 100);

    /// <summary>Nhãn ngắn cho UI: "OFF", "Decrypting 42%", "ON".</summary>
    public string DisplayText => Conversion switch
    {
        BitLockerConversionState.FullyDecrypted => "OFF",
        BitLockerConversionState.DecryptionInProgress => $"Decrypting {DecryptionProgress:0}%",
        BitLockerConversionState.DecryptionPaused => $"Decrypt paused {DecryptionProgress:0}%",
        BitLockerConversionState.EncryptionInProgress => $"Encrypting {EncryptionPercentage:0}%",
        BitLockerConversionState.FullyEncrypted => "ON",
        BitLockerConversionState.WaitingForActivation => "ON (Device Encryption)",
        BitLockerConversionState.NotSupported => "Not supported",
        _ => "Unknown"
    };

    public static BitLockerStatus NotSupported(string mountPoint, string? raw = null) => new()
    {
        MountPoint = mountPoint,
        Conversion = BitLockerConversionState.NotSupported,
        Protection = BitLockerProtectionState.Off,
        RawOutput = raw
    };

    /// <summary>
    /// Phân tích output tiếng Anh của <c>manage-bde -status C:</c>.
    /// (Gọi manage-bde với chcp 437 / LANG mặc định của Windows là tiếng Anh cho nhãn trường.)
    /// </summary>
    public static BitLockerStatus ParseManageBde(string mountPoint, string output)
    {
        string? Field(string name)
        {
            var m = Regex.Match(output, $@"^\s*{Regex.Escape(name)}:\s*(.+?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        var conversionText = Field("Conversion Status") ?? "";
        var percentText = Field("Percentage Encrypted") ?? "";
        var protectionText = Field("Protection Status") ?? "";

        var conversion = conversionText.ToLowerInvariant() switch
        {
            var s when s.Contains("fully decrypted") => BitLockerConversionState.FullyDecrypted,
            var s when s.Contains("fully encrypted") => BitLockerConversionState.FullyEncrypted,
            var s when s.Contains("decryption in progress") => BitLockerConversionState.DecryptionInProgress,
            var s when s.Contains("encryption in progress") => BitLockerConversionState.EncryptionInProgress,
            var s when s.Contains("decryption paused") => BitLockerConversionState.DecryptionPaused,
            var s when s.Contains("encryption paused") => BitLockerConversionState.EncryptionPaused,
            "" when output.Contains("not support", StringComparison.OrdinalIgnoreCase) => BitLockerConversionState.NotSupported,
            _ => BitLockerConversionState.Unknown
        };

        var pctMatch = PercentRegex().Match(percentText);
        double pct = pctMatch.Success
            ? double.Parse(pctMatch.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
            : 0;

        var protection = protectionText.Contains("On", StringComparison.OrdinalIgnoreCase)
            ? BitLockerProtectionState.On
            : protectionText.Contains("Off", StringComparison.OrdinalIgnoreCase)
                ? BitLockerProtectionState.Off
                : BitLockerProtectionState.Unknown;

        if (conversion == BitLockerConversionState.FullyEncrypted &&
            protection == BitLockerProtectionState.Off &&
            output.Contains("Waiting for Activation", StringComparison.OrdinalIgnoreCase))
            conversion = BitLockerConversionState.WaitingForActivation;

        return new BitLockerStatus
        {
            MountPoint = mountPoint,
            Conversion = conversion,
            Protection = protection,
            EncryptionPercentage = pct,
            EncryptionMethod = Field("Encryption Method"),
            RawOutput = output
        };
    }

    [GeneratedRegex(@"([\d]+(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();
}

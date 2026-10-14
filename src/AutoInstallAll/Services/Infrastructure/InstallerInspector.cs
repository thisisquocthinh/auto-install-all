using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AutoInstallAll.Models;

namespace AutoInstallAll.Services.Infrastructure;

public sealed record InstallerAnalysis(
    InstallerType InstallerType,
    PackagerType Packager,
    string SuggestedSilentArgs,
    string? Version,
    string? ProductName,
    string? Company,
    string Evidence);

/// <summary>
/// Nhận diện bộ đóng gói từ chữ ký file: PE Header (section table + overlay) cho .exe,
/// OLE Compound File cho .msi, ZIP local header cho .zip.
/// </summary>
public static partial class InstallerInspector
{
    private const int HeadScanBytes = 16 * 1024 * 1024;
    private const int OverlayScanBytes = 2 * 1024 * 1024;

    private static readonly byte[] MsiMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] NsisMagic = [0xEF, 0xBE, 0xAD, 0xDE, (byte)'N', (byte)'u', (byte)'l', (byte)'l', (byte)'s', (byte)'o', (byte)'f', (byte)'t', (byte)'I', (byte)'n', (byte)'s', (byte)'t'];

    public static InstallerAnalysis Analyze(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Không tìm thấy file cài đặt.", path);

        var type = DetectFileType(path)
                   ?? throw new InvalidDataException("File không phải .exe / .msi / .zip hợp lệ (sai chữ ký đầu file).");

        switch (type)
        {
            case InstallerType.Msi:
                return new InstallerAnalysis(type, PackagerType.Msi, GetSilentArgs(PackagerType.Msi),
                    ReadMsiProperty(path, "ProductVersion"),
                    ReadMsiProperty(path, "ProductName"),
                    ReadMsiProperty(path, "Manufacturer"),
                    "OLE Compound File (Windows Installer)");
            case InstallerType.Zip:
                return new InstallerAnalysis(type, PackagerType.Portable, "", null,
                    Path.GetFileNameWithoutExtension(path), null, "ZIP archive (ứng dụng Portable)");
        }

        var vi = FileVersionInfo.GetVersionInfo(path);
        var (packager, evidence) = DetectExePackager(path, vi);
        var version = StoragePathManager.ReadFileVersion(path);
        return new InstallerAnalysis(InstallerType.Exe, packager, GetSilentArgs(packager), version,
            NullIfEmpty(vi.ProductName) ?? NullIfEmpty(vi.FileDescription), NullIfEmpty(vi.CompanyName), evidence);
    }

    public static string GetSilentArgs(PackagerType packager) => packager switch
    {
        PackagerType.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        PackagerType.Nsis => "/S",
        PackagerType.Msi => "/qn /norestart",
        PackagerType.InstallShield => "/s /v\"/qn /norestart\"",
        PackagerType.WixBurn => "/quiet /norestart",
        PackagerType.WinRarSfx => "/S",
        PackagerType.DirectXRedist => "/silent",
        _ => ""
    };

    /// <summary>Map "Installer Type" của winget sang PackagerType.</summary>
    public static PackagerType FromWingetInstallerType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "inno" => PackagerType.InnoSetup,
        "nullsoft" => PackagerType.Nsis,
        "msi" or "wix" => PackagerType.Msi,
        "burn" => PackagerType.WixBurn,
        "zip" or "portable" => PackagerType.Portable,
        _ => PackagerType.Unknown
    };

    public static InstallerType? DetectFileType(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int n = fs.Read(head);
        if (n >= 2 && head[0] == 'M' && head[1] == 'Z') return InstallerType.Exe;
        if (n >= 8 && head.SequenceEqual(MsiMagic)) return InstallerType.Msi;
        if (n >= 4 && head[..4].SequenceEqual(ZipMagic)) return InstallerType.Zip;
        return null;
    }

    /// <summary>Kiểm tra file tải về đúng định dạng mong đợi (chặn trường hợp máy chủ trả trang HTML lỗi).</summary>
    public static bool HasValidSignature(string path, InstallerType expected) => DetectFileType(path) == expected;

    // ------------------------ PE ------------------------

    private static (PackagerType, string) DetectExePackager(string path, FileVersionInfo vi)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pe = ReadPeLayout(fs);

        if (pe.SectionNames.Contains(".wixburn"))
            return (PackagerType.WixBurn, "PE section .wixburn (WiX Burn bundle)");

        var head = ReadBlock(fs, 0, (int)Math.Min(fs.Length, HeadScanBytes));
        var overlay = pe.OverlayOffset > 0 && pe.OverlayOffset < fs.Length
            ? ReadBlock(fs, pe.OverlayOffset, (int)Math.Min(fs.Length - pe.OverlayOffset, OverlayScanBytes))
            : Array.Empty<byte>();

        bool Has(string marker) =>
            Contains(head, Encoding.ASCII.GetBytes(marker)) || Contains(head, Encoding.Unicode.GetBytes(marker)) ||
            Contains(overlay, Encoding.ASCII.GetBytes(marker));

        var meta = $"{vi.Comments} {vi.FileDescription} {vi.ProductName} {vi.LegalCopyright}";

        if (Has("Inno Setup Setup Data") || Has("rDlPtS") || meta.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase))
            return (PackagerType.InnoSetup, "Chuỗi Inno Setup trong PE resource / overlay");
        if (Contains(overlay, NsisMagic) || Contains(head, NsisMagic) || Has("Nullsoft.NSIS") || Has("Nullsoft Install System"))
            return (PackagerType.Nsis, "Chữ ký NullsoftInst (NSIS) trong overlay");
        if (Has("InstallShield") || meta.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
            return (PackagerType.InstallShield, "Chuỗi InstallShield trong PE resource");
        if (Has("WinRAR SFX") || Has("WinRAR self-extracting") || meta.Contains("WinRAR", StringComparison.OrdinalIgnoreCase))
            return (PackagerType.WinRarSfx, "WinRAR SFX module");
        if (meta.Contains("DirectX", StringComparison.OrdinalIgnoreCase) && meta.Contains("redist", StringComparison.OrdinalIgnoreCase))
            return (PackagerType.DirectXRedist, "DirectX redistributable");
        if (Contains(overlay, SevenZipMagic))
            return (PackagerType.Custom, "7-Zip SFX (chưa có tham số silent chuẩn)");

        return (PackagerType.Unknown, $"Không nhận diện được bộ đóng gói ({pe.SectionNames.Count} section, overlay @ {pe.OverlayOffset:N0})");
    }

    private sealed record PeLayout(IReadOnlySet<string> SectionNames, long OverlayOffset);

    private static PeLayout ReadPeLayout(FileStream fs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        try
        {
            fs.Position = 0x3C;
            int lfanew = br.ReadInt32();
            if (lfanew <= 0 || lfanew > fs.Length - 24) return new PeLayout(names, 0);
            fs.Position = lfanew;
            if (br.ReadUInt32() != 0x00004550) return new PeLayout(names, 0); // "PE\0\0"
            fs.Position = lfanew + 6;
            ushort sectionCount = br.ReadUInt16();
            fs.Position = lfanew + 20;
            ushort optionalHeaderSize = br.ReadUInt16();
            long sectionTable = lfanew + 24 + optionalHeaderSize;

            long overlay = 0;
            for (int i = 0; i < sectionCount && i < 96; i++)
            {
                fs.Position = sectionTable + i * 40L;
                var name = Encoding.ASCII.GetString(br.ReadBytes(8)).TrimEnd('\0');
                fs.Position = sectionTable + i * 40L + 16;
                uint rawSize = br.ReadUInt32();
                uint rawPtr = br.ReadUInt32();
                names.Add(name);
                overlay = Math.Max(overlay, (long)rawPtr + rawSize);
            }
            return new PeLayout(names, overlay);
        }
        catch (EndOfStreamException)
        {
            return new PeLayout(names, 0);
        }
    }

    private static byte[] ReadBlock(FileStream fs, long offset, int count)
    {
        var buffer = new byte[count];
        fs.Position = offset;
        int total = 0;
        while (total < count)
        {
            int n = fs.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total == count ? buffer : buffer[..total];
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.Length >= needle.Length && haystack.AsSpan().IndexOf(needle) >= 0;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------ MSI (msi.dll) ------------------------

    public static string? ReadMsiProperty(string msiPath, string property)
    {
        if (!PropertyNameRegex().IsMatch(property))
            throw new ArgumentException("Tên property không hợp lệ.", nameof(property));

        IntPtr db = IntPtr.Zero, view = IntPtr.Zero, record = IntPtr.Zero;
        try
        {
            if (MsiOpenDatabase(msiPath, IntPtr.Zero /* MSIDBOPEN_READONLY */, out db) != 0) return null;
            if (MsiDatabaseOpenView(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{property}'", out view) != 0) return null;
            if (MsiViewExecute(view, IntPtr.Zero) != 0) return null;
            if (MsiViewFetch(view, out record) != 0) return null;

            uint size = 256;
            var sb = new StringBuilder((int)size);
            uint rc = MsiRecordGetString(record, 1, sb, ref size);
            if (rc == 234 /* ERROR_MORE_DATA */)
            {
                size++;
                sb = new StringBuilder((int)size);
                rc = MsiRecordGetString(record, 1, sb, ref size);
            }
            return rc == 0 ? NullIfEmpty(sb.ToString()) : null;
        }
        finally
        {
            if (record != IntPtr.Zero) MsiCloseHandle(record);
            if (view != IntPtr.Zero) { MsiViewClose(view); MsiCloseHandle(view); }
            if (db != IntPtr.Zero) MsiCloseHandle(db);
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex PropertyNameRegex();

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out IntPtr phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(IntPtr hDatabase, string szQuery, out IntPtr phView);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);

    [DllImport("msi.dll")]
    private static extern uint MsiViewClose(IntPtr hView);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(IntPtr hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr hAny);
}

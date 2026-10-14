using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using AutoInstallAll.Models;
using AutoInstallAll.Services;
using AutoInstallAll.Services.Infrastructure;
using AutoInstallAll.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoInstallAll.ViewModels;

public enum NavPage
{
    Home,
    Apps,
    Disk,
    Usb,
    Settings
}

/// <summary>ViewModel của cửa sổ chính: Sidebar, lưới Bento, cột System Panel và 4 trang phụ.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 600;

    private readonly StoragePathManager _paths;
    private readonly ManifestConfig _manifest;
    private readonly LogService _log;
    private readonly AutoUpdaterService _updater;
    private readonly SilentInstallService _installer;
    private readonly SystemUtilitiesService _system;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _ui;

    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _queueCts;
    private CancellationTokenSource? _bitLockerCts;
    private bool _initialized;

    public MainViewModel(
        StoragePathManager paths,
        ManifestConfig manifest,
        LogService log,
        AutoUpdaterService updater,
        SilentInstallService installer,
        SystemUtilitiesService system,
        IDialogService dialogs)
    {
        _paths = paths;
        _manifest = manifest;
        _log = log;
        _updater = updater;
        _installer = installer;
        _system = system;
        _dialogs = dialogs;
        _ui = Application.Current.Dispatcher;

        Apps = new ObservableCollection<SoftwareItem>(manifest.Apps);
        SystemTools = new ObservableCollection<SoftwareItem>(manifest.SystemTools);
        Apps.CollectionChanged += OnAppsChanged;
        foreach (var name in SystemUtilitiesService.DefaultBloatware)
            Bloatware.Add(new BloatwareItemViewModel(name));

        _log.EntryWritten += OnLogEntry;
        LoadSettingsFromManifest();
        RebuildCards();
        RefreshUsbSummary();
    }

    // =====================================================================
    // Collections
    // =====================================================================

    public ObservableCollection<SoftwareItem> Apps { get; }
    public ObservableCollection<SoftwareItem> SystemTools { get; }
    public ObservableCollection<AppCardViewModel> Cards { get; } = new();
    public ObservableCollection<DiskBarViewModel> DiskBars { get; } = new();
    public ObservableCollection<DiskPartitionInfo> Partitions { get; } = new();
    public ObservableCollection<InstallerFileViewModel> InstallerFiles { get; } = new();
    public ObservableCollection<BloatwareItemViewModel> Bloatware { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public SoftwareItem? VcRedist => _manifest.FindById(SystemUtilitiesService.VcRedistId);
    public SoftwareItem? DirectX => _manifest.FindById(SystemUtilitiesService.DirectXId);

    // =====================================================================
    // Trạng thái chung
    // =====================================================================

    [ObservableProperty] private NavPage _currentPage = NavPage.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunOperation))]
    private bool _isBusy;

    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private string _usbSummary = "";
    [ObservableProperty] private string _usbSizeText = "";
    [ObservableProperty] private string _lastCheckText = "Chưa kiểm tra bản mới";

    public string AllAppsLabel => $"Tất cả ({Apps.Count})";
    public int SelectedCount => Apps.Count(a => a.IsSelected);
    public string StartTitle => $"BẮT ĐẦU CÀI ĐẶT NGAY (Đã chọn {SelectedCount} phần mềm)";
    public string RootPath => _paths.RootDirectory;
    public string InstallersPath => _paths.InstallersDirectory;
    public string IconsPath => _paths.IconsDirectory;
    public string LogsPath => _paths.LogsDirectory;
    public string ModeText => _paths.IsRunningFromRemovableDrive ? "Chạy từ USB" : "Chạy từ ổ máy";
    public bool CanRunOperation => !IsBusy && !IsQueueRunning;

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    // =====================================================================
    // Khởi tạo
    // =====================================================================

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _log.Info("app", $"{ModeText} — {_paths.RootDirectory}");

        try
        {
            await _updater.ScanLocalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("app", "Quét .\\Installers\\ thất bại", ex);
        }
        RefreshUsbSummary();
        if (_manifest.LastUpdateCheckUtc is { } last)
            LastCheckText = $"Kiểm tra lần cuối: {last.ToLocalTime():dd/MM/yyyy HH:mm}";

        await Task.WhenAll(RefreshBitLockerAsync(), RefreshDiskAsync());
    }

    // =====================================================================
    // Thẻ Pastel & USB
    // =====================================================================

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(AllAppsLabel));
        RebuildCards();
        OnSelectionChanged();
        RefreshUsbSummary();
    }

    private void RebuildCards()
    {
        foreach (var card in Cards)
        {
            card.Item.PropertyChanged -= OnAppPropertyChanged;
            card.Dispose();
        }
        Cards.Clear();
        foreach (var app in Apps.OrderBy(AppCardViewModel.OrderOf))
        {
            app.PropertyChanged += OnAppPropertyChanged;
            Cards.Add(new AppCardViewModel(app));
        }
    }

    private void OnAppPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareItem.IsSelected))
            OnSelectionChanged();
        else if (e.PropertyName == nameof(SoftwareItem.LocalFile))
            _ui.BeginInvoke(RefreshUsbSummary);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StartTitle));
    }

    private void RefreshUsbSummary()
    {
        try
        {
            var files = _paths.EnumerateInstallerFiles().ToList();
            var gb = files.Sum(f => f.Length) / 1024d / 1024 / 1024;
            int ready = Apps.Count(a => a.HasLocalInstaller && _paths.InstallerExists(a));
            UsbSummary = ready == 0
                ? "Chưa có bộ cài nào • Bấm Kiểm tra bản mới để tải"
                : $"Đã lưu {ready} bộ cài • Sẵn sàng cài không cần mạng";
            UsbSizeText = $"{gb:0.0} GB · trống {_paths.GetAvailableFreeSpace() / 1024d / 1024 / 1024:0.#} GB";

            var owners = _manifest.AllItems
                .Where(i => i.LocalFile is not null)
                .ToDictionary(i => i.LocalFile!, i => i.Name, StringComparer.OrdinalIgnoreCase);
            InstallerFiles.Clear();
            foreach (var f in files.OrderBy(f => f.Name))
            {
                var owned = owners.TryGetValue(f.Name, out var owner);
                InstallerFiles.Add(new InstallerFileViewModel(
                    f.Name,
                    f.Length >= 1024 * 1024 ? $"{f.Length / 1024d / 1024:0.#} MB" : $"{f.Length / 1024d:0} KB",
                    f.LastWriteTime.ToString("dd/MM/yyyy HH:mm"),
                    owned ? owner! : "Không thuộc manifest",
                    !owned));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UsbSummary = "Không đọc được USB";
            UsbSizeText = ex.Message;
        }
    }

    /// <summary>Danh sách "Bộ cơ bản khuyên dùng" cho người mới.</summary>
    private static readonly string[] RecommendedIds = { "google-chrome", "unikey", "winrar", "ultraviewer", "zalo-pc" };

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var app in Apps)
            app.IsSelected = RecommendedIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool selectAll = !Apps.All(a => a.IsSelected);
        foreach (var app in Apps)
            app.IsSelected = selectAll;
    }

    // =====================================================================
    // Check Latest Versions (AutoUpdaterService)
    // =====================================================================

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task CheckLatestVersionsAsync() =>
        RunOperationAsync("Đang kiểm tra bản mới", async ct =>
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                BusyProgress = p.Percent;
                BusyMessage = p.Stage == "Downloading"
                    ? $"Đang tải {p.Item.Name} {p.Percent:0}%"
                    : $"Đang kiểm tra {p.Item.Name}...";
            });
            var results = await _updater.CheckLatestVersionsAsync(true, progress, ct);
            LastCheckText = $"Kiểm tra lần cuối: {DateTime.Now:dd/MM/yyyy HH:mm}";

            var updated = results.Where(r => r.UpdateAvailable && r.Error is null).ToList();
            var failed = results.Where(r => r.Error is not null).ToList();
            var msg = updated.Count == 0 && failed.Count == 0
                ? "Tất cả bộ cài trên USB đã là bản mới nhất."
                : string.Join(Environment.NewLine,
                    updated.Select(r => $"✓ {r.Item.Name}: {r.Message}")
                        .Concat(failed.Select(r => $"✗ {r.Item.Name}: {r.Message}")));
            _dialogs.Info("Kiểm tra bản mới", msg);
        });

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task DownloadItemAsync(SoftwareItem? item) =>
        item is null
            ? Task.CompletedTask
            : RunOperationAsync($"Đang tải {item.Name}", async ct =>
            {
                var p = new Progress<double>(v => { BusyProgress = v; BusyMessage = $"Đang tải {item.Name} {v:0}%"; });
                await _updater.DownloadLatestAsync(item, p, ct);
            });

    // =====================================================================
    // + Add App (AddApplicationService qua hộp thoại)
    // =====================================================================

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private void AddApp()
    {
        var item = _dialogs.ShowAddApp();
        if (item is null) return;
        if (!Apps.Contains(item))
            Apps.Add(item);
        _log.Info("addapp", $"Đã thêm {item.Name} vào danh sách.");
    }

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private async Task RemoveItemAsync(SoftwareItem? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm("Xoá ứng dụng",
                $"Xoá '{item.Name}' khỏi danh sách và xoá bộ cài {item.LocalFile ?? "(không có)"} trên USB?", danger: true))
            return;

        await RunOperationAsync($"Đang xoá {item.Name}", async ct =>
        {
            var installer = _paths.GetInstallerPath(item);
            if (installer is not null && File.Exists(installer))
                File.Delete(installer);
            if (item.IconPath is not null && File.Exists(item.IconPath))
                File.Delete(item.IconPath);

            _manifest.Apps.Remove(item);
            _manifest.SystemTools.Remove(item);
            await _manifest.SaveAsync(_paths.ManifestPath, ct);
            Apps.Remove(item);
            _log.Info("addapp", $"Đã xoá {item.Name} khỏi manifest.json.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task SaveManifestAsync() =>
        RunOperationAsync("Đang lưu manifest.json", async ct =>
        {
            await _manifest.SaveAsync(_paths.ManifestPath, ct);
            _log.Info("app", "Đã lưu manifest.json.");
        });

    // =====================================================================
    // Installation Queue (SilentInstallService)
    // =====================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunOperation))]
    private bool _isQueueRunning;

    [ObservableProperty] private double _queuePercent;
    [ObservableProperty] private string _queueTitle = "";
    [ObservableProperty] private string _queueStatus = IdleQueueStatus;

    private const string IdleQueueStatus = "Tự động cài đặt ngầm không cần bấm Next";

    partial void OnIsQueueRunningChanged(bool value) => RefreshCommands();

    /// <summary>Bấm khi đang chạy = huỷ hàng đợi.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartAutoSetupAsync()
    {
        if (IsQueueRunning)
        {
            if (_dialogs.Confirm("Huỷ cài đặt", "Dừng hàng đợi sau ứng dụng đang cài?"))
                _queueCts?.Cancel();
            return;
        }
        if (IsBusy) return;

        var selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Info("Bắt đầu cài đặt", "Hãy tích chọn ít nhất một phần mềm (bấm vào thẻ để chọn).");
            return;
        }
        var missing = selected.Where(a => !_paths.InstallerExists(a)).Select(a => a.Name).ToList();
        var message = $"Cài đặt tự động {selected.Count} phần mềm:\n\n{string.Join("\n", selected.Select(a => "• " + a.Name))}";
        if (missing.Count > 0)
            message += $"\n\nChưa có trên USB (sẽ tải nếu có mạng): {string.Join(", ", missing)}";
        if (!_dialogs.Confirm("Bắt đầu cài đặt", message))
            return;

        _queueCts = new CancellationTokenSource();
        IsQueueRunning = true;
        QueuePercent = 0;
        QueueTitle = "ĐANG CÀI ĐẶT... 0%";
        QueueStatus = $"Đang chuẩn bị {selected[0].Name}...";
        try
        {
            var progress = new Progress<InstallQueueProgress>(p =>
            {
                QueuePercent = p.OverallPercent;
                QueueTitle = $"ĐANG CÀI ĐẶT... {p.OverallPercent:0}%";
                QueueStatus = p.OverallPercent >= 100
                    ? "Hoàn tất"
                    : $"({p.Index + 1}/{p.Total}) Đang cài {p.Current.Name} — bấm để huỷ";
            });
            var results = await _installer.InstallQueueAsync(selected, progress, _queueCts.Token);

            var ok = results.Count(r => r.Success);
            QueuePercent = 100;
            QueueTitle = $"ĐÃ XONG — {ok}/{results.Count} phần mềm";
            QueueStatus = results.Any(r => r.RebootRequired) ? "Cần khởi động lại máy để hoàn tất" : "Bấm để cài lại";
            _dialogs.Info("Kết quả cài đặt", string.Join(Environment.NewLine,
                results.Select(r => $"{(r.Success ? "✓" : "✗")} {r.Item.Name}: {r.Message}")));
        }
        catch (OperationCanceledException)
        {
            QueueTitle = "";
            QueueStatus = "Đã huỷ — " + IdleQueueStatus;
        }
        catch (Exception ex)
        {
            QueueTitle = "";
            QueueStatus = ex.Message;
            _dialogs.Error("Cài đặt thất bại", ex.Message);
        }
        finally
        {
            _queueCts.Dispose();
            _queueCts = null;
            IsQueueRunning = false;
            QueuePercent = 0;
            if (QueueTitle.StartsWith("ĐANG", StringComparison.Ordinal)) QueueTitle = "";
            RefreshUsbSummary();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task InstallItemAsync(SoftwareItem? item) =>
        item is null || !_dialogs.Confirm("Cài đặt", $"Cài đặt tự động {item.Name}?")
            ? Task.CompletedTask
            : RunInstallAsync(item, ct => _installer.InstallAsync(item, new Progress<double>(v => BusyProgress = v), ct));

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task InstallVcRedistAsync() =>
        VcRedist is not { } vc ? Task.CompletedTask
            : RunInstallAsync(vc, ct => _system.InstallVisualCppAsync(new Progress<double>(v => BusyProgress = v), ct));

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task InstallDirectXAsync() =>
        DirectX is not { } dx ? Task.CompletedTask
            : RunInstallAsync(dx, ct => _system.InstallDirectXAsync(new Progress<double>(v => BusyProgress = v), ct));

    private Task RunInstallAsync(SoftwareItem item, Func<CancellationToken, Task<InstallResult>> install) =>
        RunOperationAsync($"Đang cài {item.Name}", async ct =>
        {
            var r = await install(ct);
            if (r.Success)
                _dialogs.Info(item.Name, r.RebootRequired ? $"{r.Message}\nHãy khởi động lại máy." : r.Message);
            else
                _dialogs.Error(item.Name, r.Message);
        });

    // =====================================================================
    // BitLocker (SystemUtilitiesService)
    // =====================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitLockerText), nameof(BitLockerIsOff), nameof(BitLockerGlyph))]
    private BitLockerStatus? _bitLocker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitLockerText))]
    private bool _isBitLockerBusy;

    public bool BitLockerIsOff => BitLocker?.IsOff == true;

    public string BitLockerGlyph => BitLocker switch
    {
        { IsOff: true } => "\uE73E",                 // CheckMark
        { IsDecrypting: true } => "\uE895",          // Sync
        { Conversion: BitLockerConversionState.NotSupported } => "\uE946", // Info
        null => "\uE895",
        _ => "\uE72E"                                // Lock
    };

    public string BitLockerText => BitLocker switch
    {
        null => "Đang đọc trạng thái BitLocker...",
        { IsOff: true } => "✓ Mã hóa BitLocker: Đã tắt (An toàn)",
        { IsDecrypting: true } s => $"Đang tắt BitLocker... {s.DecryptionProgress:0}%",
        { Conversion: BitLockerConversionState.NotSupported } => "Máy này không hỗ trợ BitLocker",
        { Conversion: BitLockerConversionState.Unknown } => "Chưa đọc được BitLocker – Bấm để thử lại",
        _ => "⚠ BitLocker đang bật – Bấm để tắt"
    };

    private async Task RefreshBitLockerAsync()
    {
        try
        {
            BitLocker = await _system.GetBitLockerStatusAsync("C:", CancellationToken.None);
            if (BitLocker.IsDecrypting && _bitLockerCts is null)
                _ = MonitorBitLockerAsync(alreadyRunning: true);
        }
        catch (Exception ex)
        {
            _log.Warn("bitlocker", "Không đọc được BitLocker", ex);
            BitLocker = new BitLockerStatus { Conversion = BitLockerConversionState.Unknown };
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleBitLockerAsync()
    {
        if (_bitLockerCts is not null || BitLocker is null) return;

        if (BitLocker.IsOff || BitLocker.Conversion is BitLockerConversionState.NotSupported or BitLockerConversionState.Unknown)
        {
            await RefreshBitLockerAsync();
            return;
        }
        if (!BitLocker.IsDecrypting &&
            !_dialogs.Confirm("Tắt BitLocker",
                "Tắt mã hoá BitLocker trên ổ C: (manage-bde -off C:).\n\n" +
                "Windows sẽ giải mã toàn bộ ổ đĩa ở chế độ nền (có thể mất 10–60 phút). " +
                "Có thể tiếp tục dùng máy và cài phần mềm trong lúc giải mã.\n\nTiếp tục?", danger: true))
            return;

        await MonitorBitLockerAsync(alreadyRunning: BitLocker.IsDecrypting);
    }

    private async Task MonitorBitLockerAsync(bool alreadyRunning)
    {
        _bitLockerCts = new CancellationTokenSource();
        IsBitLockerBusy = true;
        try
        {
            var progress = new Progress<BitLockerStatus>(s => BitLocker = s);
            BitLocker = await _system.DisableBitLockerAsync("C:", progress, _bitLockerCts.Token);
            if (!alreadyRunning)
                _dialogs.Info("BitLocker", "Ổ C: đã giải mã xong — BitLocker đã tắt.");
        }
        catch (OperationCanceledException)
        {
            _log.Info("bitlocker", "Dừng theo dõi BitLocker (Windows vẫn tiếp tục giải mã nền).");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Tắt BitLocker thất bại", ex.Message);
            await RefreshBitLockerAsync();
        }
        finally
        {
            _bitLockerCts?.Dispose();
            _bitLockerCts = null;
            IsBitLockerBusy = false;
        }
    }

    // =====================================================================
    // Disk Partitioning / Quick Split C/D
    // =====================================================================

    [ObservableProperty] private DiskInfo? _disk;
    [ObservableProperty] private string _diskTotalText = "— GB";
    [ObservableProperty] private string _diskSubtitle = "Đang đọc ổ đĩa...";
    [ObservableProperty] private double _splitMaxGB;
    [ObservableProperty] private string _splitPlanText = "";
    [ObservableProperty] private bool _isSplitPlanValid;
    [ObservableProperty] private string _systemSizeText = "Ổ C (Win): — GB";
    [ObservableProperty] private string _dataSizeText = "Ổ D (Dữ liệu): — GB";

    [ObservableProperty] private double _splitSizeGB;

    partial void OnSplitSizeGBChanged(double value) => UpdateSplitPlan();

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private Task RefreshSystemAsync() => Task.WhenAll(RefreshBitLockerAsync(), RefreshDiskAsync());

    private async Task RefreshDiskAsync()
    {
        try
        {
            var disk = await _system.GetSystemDiskAsync(CancellationToken.None);
            Disk = disk;
            DiskTotalText = $"{disk.SizeGB:0} GB";
            DiskSubtitle = $"{disk.FriendlyName} · {disk.PartitionStyle}";
            Partitions.Clear();
            foreach (var p in disk.Partitions) Partitions.Add(p);

            if (disk.SystemDrive is { } c)
            {
                var minFree = _manifest.Settings.MinFreeSpaceForSplitGB * DiskPartitionInfo.GB;
                var maxBytes = c.SizeBytes - Math.Max(c.SupportedMinBytes, c.UsedBytes + minFree);
                SplitMaxGB = Math.Max(0, Math.Floor(maxBytes / (double)DiskPartitionInfo.GB));
                SplitSizeGB = Math.Min(_manifest.Settings.DefaultDataDriveSizeGB, SplitMaxGB);
            }
            UpdateSplitPlan();
        }
        catch (Exception ex)
        {
            _log.Warn("disk", "Không đọc được ổ đĩa", ex);
            DiskTotalText = "— GB";
            DiskSubtitle = "Không đọc được ổ đĩa: " + ex.Message;
            IsSplitPlanValid = false;
        }
    }

    private void UpdateSplitPlan()
    {
        DiskBars.Clear();
        if (Disk?.SystemDrive is not { } c)
        {
            SplitPlanText = "";
            IsSplitPlanValid = false;
            return;
        }

        var plan = _system.CreateSplitPlan(Disk, Math.Round(SplitSizeGB));
        var errors = plan.Validate(_system.GetDriveLettersInUse());
        IsSplitPlanValid = errors.Count == 0 && SplitSizeGB >= 1;
        SystemSizeText = $"Ổ C (Win): {plan.NewSystemSizeGB:0} GB";
        DataSizeText = $"Ổ {plan.NewDriveLetter} (Dữ liệu): {plan.NewDataSizeGB:0} GB";
        double total = Disk.SizeBytes;

        if (IsSplitPlanValid)
        {
            SplitPlanText = $"Sau khi chia: C: {plan.NewSystemSizeGB:0} GB · {plan.NewDriveLetter}: {plan.NewDataSizeGB:0} GB";
            DiskBars.Add(new DiskBarViewModel($"Ổ C ({plan.NewSystemSizeGB:0}G)", $"đã dùng {c.UsedGB:0} GB",
                plan.NewSystemSizeBytes * 100 / total, Palette.Pink));
            DiskBars.Add(new DiskBarViewModel($"Ổ {plan.NewDriveLetter} ({plan.NewDataSizeGB:0}G)", "phân vùng mới",
                plan.NewDataSizeBytes * 100 / total, Palette.Mint));
        }
        else
        {
            SplitPlanText = SplitMaxGB < 1 ? "Ổ C: không đủ dung lượng trống để chia." : string.Join(" ", errors);
            var fills = new[] { Palette.Pink, Palette.Mint, Palette.Lavender, Palette.Peach };
            int i = 0;
            foreach (var p in Disk.Partitions.Where(p => p.DriveLetter is not null))
                DiskBars.Add(new DiskBarViewModel($"Ổ {p.DriveLetter} ({p.SizeGB:0}G)", $"đã dùng {p.UsedGB:0} GB",
                    p.SizeBytes * 100 / total, fills[i++ % fills.Length]));
        }
        QuickSplitCommand.NotifyCanExecuteChanged();
    }

    private bool CanQuickSplit() => CanRunOperation && IsSplitPlanValid;

    [RelayCommand(CanExecute = nameof(CanQuickSplit))]
    private async Task QuickSplitAsync()
    {
        if (Disk is null) return;
        var plan = _system.CreateSplitPlan(Disk, Math.Round(SplitSizeGB));
        if (!_dialogs.Confirm("Chia nhanh ổ C / D",
                $"Thu nhỏ ổ C: từ {plan.Source.SizeGB:0} GB xuống {plan.NewSystemSizeGB:0} GB và tạo ổ " +
                $"{plan.NewDriveLetter}: '{plan.NewLabel}' khoảng {plan.NewDataSizeGB:0} GB (NTFS).\n\n" +
                "Dữ liệu trên C: được giữ nguyên. Nên sao lưu dữ liệu quan trọng trước khi thực hiện.\n\nTiếp tục?", danger: true))
            return;

        await RunOperationAsync("Đang chia ổ cứng", async ct =>
        {
            var progress = new Progress<string>(m => BusyMessage = m);
            var created = await _system.QuickSplitAsync(plan, progress, ct);
            await RefreshDiskAsync();
            _dialogs.Info("Chia nhanh ổ C / D", $"Đã tạo {created.DisplayName}.");
        });
    }

    // =====================================================================
    // Clean Bloatware
    // =====================================================================

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private async Task RemoveBloatwareAsync()
    {
        var selected = Bloatware.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Info("Gỡ ứng dụng rác", "Chưa chọn gói nào.");
            return;
        }
        if (!_dialogs.Confirm("Gỡ ứng dụng rác",
                $"Gỡ {selected.Count} ứng dụng cài sẵn cho mọi người dùng trên máy này:\n\n" +
                string.Join(", ", selected.Select(b => b.DisplayName)), danger: true))
            return;

        foreach (var b in selected) { b.Status = "Đang chờ..."; b.Removed = null; }
        await RunOperationAsync("Đang gỡ Bloatware", async ct =>
        {
            var progress = new Progress<BloatwareRemovalResult>(r =>
            {
                var vm = Bloatware.FirstOrDefault(b => b.PackageName == r.PackageName);
                if (vm is null) return;
                vm.Status = r.Message;
                vm.Removed = r.Removed;
                BusyMessage = $"{vm.DisplayName}: {r.Message}";
            });
            var results = await _system.RemoveBloatwareAsync(selected.Select(b => b.PackageName), progress, ct);
            _dialogs.Info("Gỡ ứng dụng rác", $"Đã gỡ {results.Count(r => r.Removed)}/{results.Count} gói.");
        });
    }

    // =====================================================================
    // USB Folder
    // =====================================================================

    [RelayCommand]
    private void OpenFolder(string? which) =>
        _dialogs.OpenFolder(which switch
        {
            "installers" => _paths.InstallersDirectory,
            "icons" => _paths.IconsDirectory,
            "logs" => _paths.LogsDirectory,
            _ => _paths.RootDirectory
        });

    /// <summary>Mục "Thư mục USB" ở Sidebar: mở .\Installers\ trong Explorer và hiện trang USB.</summary>
    [RelayCommand]
    private void OpenUsbFolder()
    {
        CurrentPage = NavPage.Usb;
        _dialogs.OpenFolder(_paths.InstallersDirectory);
    }

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private async Task RescanUsbAsync()
    {
        await RunOperationAsync("Đang quét USB", async ct =>
        {
            var removed = _paths.CleanupStaleDownloads();
            if (removed > 0) _log.Info("app", $"Đã dọn {removed} file .downloading.");
            await _updater.ScanLocalAsync(ct);
            await _manifest.SaveAsync(_paths.ManifestPath, ct);
        });
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    private void OnLogEntry(object? sender, LogEntry e)
    {
        var line = $"{e.Time:HH:mm:ss}  [{e.Category}] {(e.Level == LogLevel.Info ? "" : e.Level.ToString().ToUpperInvariant() + ": ")}{e.Message}";
        _ui.BeginInvoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    // =====================================================================
    // Settings
    // =====================================================================

    [ObservableProperty] private int _settingTimeoutMinutes;
    [ObservableProperty] private bool _settingVerifySha256;
    [ObservableProperty] private bool _settingDeleteOldInstaller;
    [ObservableProperty] private int _settingMinFreeGB;
    [ObservableProperty] private int _settingDataSizeGB;
    [ObservableProperty] private string _settingDataLetter = "D";
    [ObservableProperty] private string _settingDataLabel = "DATA";

    private void LoadSettingsFromManifest()
    {
        var s = _manifest.Settings;
        SettingTimeoutMinutes = s.InstallTimeoutMinutes;
        SettingVerifySha256 = s.VerifySha256;
        SettingDeleteOldInstaller = s.DeleteOldInstallerAfterUpdate;
        SettingMinFreeGB = s.MinFreeSpaceForSplitGB;
        SettingDataSizeGB = s.DefaultDataDriveSizeGB;
        SettingDataLetter = s.DefaultDataDriveLetter;
        SettingDataLabel = s.DefaultDataDriveLabel;
    }

    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private async Task SaveSettingsAsync()
    {
        var letter = (SettingDataLetter ?? "").Trim().ToUpperInvariant();
        if (letter.Length != 1 || !char.IsAsciiLetterUpper(letter[0]) || letter[0] is 'A' or 'B' or 'C')
        {
            _dialogs.Error("Cài đặt", "Ký tự ổ dữ liệu phải là một chữ cái từ D đến Z.");
            return;
        }
        if (SettingTimeoutMinutes is < 1 or > 120)
        {
            _dialogs.Error("Cài đặt", "Timeout phải trong khoảng 1–120 phút.");
            return;
        }

        var s = _manifest.Settings;
        s.InstallTimeoutMinutes = SettingTimeoutMinutes;
        s.VerifySha256 = SettingVerifySha256;
        s.DeleteOldInstallerAfterUpdate = SettingDeleteOldInstaller;
        s.MinFreeSpaceForSplitGB = Math.Clamp(SettingMinFreeGB, 10, 500);
        s.DefaultDataDriveSizeGB = Math.Max(1, SettingDataSizeGB);
        s.DefaultDataDriveLetter = letter;
        s.DefaultDataDriveLabel = string.IsNullOrWhiteSpace(SettingDataLabel) ? "DATA" : SettingDataLabel.Trim();
        LoadSettingsFromManifest();

        await RunOperationAsync("Đang lưu cài đặt", async ct =>
        {
            await _manifest.SaveAsync(_paths.ManifestPath, ct);
            await RefreshDiskAsync();
            _dialogs.Info("Cài đặt", "Đã lưu cài đặt vào manifest.json.");
        });
    }

    // =====================================================================
    // Hạ tầng thao tác
    // =====================================================================

    [RelayCommand]
    private void CancelOperation()
    {
        _opCts?.Cancel();
        BusyMessage = "Đang huỷ...";
    }

    private async Task RunOperationAsync(string title, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _opCts = new CancellationTokenSource();
        IsBusy = true;
        BusyMessage = title;
        BusyProgress = 0;
        try
        {
            await work(_opCts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("app", $"{title}: đã huỷ.");
        }
        catch (Exception ex)
        {
            _log.Error("app", $"{title}: lỗi", ex);
            _dialogs.Error(title, ex.Message);
        }
        finally
        {
            _opCts.Dispose();
            _opCts = null;
            IsBusy = false;
            BusyMessage = "";
            BusyProgress = 0;
            RefreshUsbSummary();
        }
    }

    private void RefreshCommands()
    {
        CheckLatestVersionsCommand.NotifyCanExecuteChanged();
        DownloadItemCommand.NotifyCanExecuteChanged();
        AddAppCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
        SaveManifestCommand.NotifyCanExecuteChanged();
        InstallItemCommand.NotifyCanExecuteChanged();
        InstallVcRedistCommand.NotifyCanExecuteChanged();
        InstallDirectXCommand.NotifyCanExecuteChanged();
        RefreshSystemCommand.NotifyCanExecuteChanged();
        QuickSplitCommand.NotifyCanExecuteChanged();
        RemoveBloatwareCommand.NotifyCanExecuteChanged();
        RescanUsbCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }
}

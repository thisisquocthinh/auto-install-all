using System.Collections.ObjectModel;
using AutoInstallAll.Models;
using AutoInstallAll.Services;
using AutoInstallAll.Services.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoInstallAll.ViewModels;

/// <summary>Hộp thoại "+ Add App": 3 chế độ → bản nháp chỉnh sửa được → Save &amp; Download Latest.</summary>
public sealed partial class AddAppViewModel : ObservableObject
{
    private readonly AddApplicationService _service;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private AppDraft? _draft;
    private string _suggestedSilentArgs = "";

    public AddAppViewModel(AddApplicationService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
    }

    /// <summary>true = đóng với kết quả thành công.</summary>
    public event EventHandler<bool>? RequestClose;

    public SoftwareItem? Result { get; private set; }

    // ---------------- Chế độ ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    private AddAppMode _mode = AddAppMode.Winget;

    partial void OnModeChanged(AddAppMode value)
    {
        ClearDraft();
        ErrorText = "";
    }

    public string ModeHint => Mode switch
    {
        AddAppMode.Winget => "Nhập tên ứng dụng — công cụ tự tra Package ID, link tải chính chủ và tham số cài ngầm.",
        AddAppMode.DirectUrl => "Dán link tải cố định luôn trỏ tới bản mới nhất. ETag / Last-Modified dùng để tự phát hiện bản mới.",
        _ => @"Chọn bộ cài .exe / .msi / .zip trên máy — chép vào .\Installers\ và đọc PE Header để gợi ý cờ Silent."
    };

    // ---------------- Winget ----------------

    [ObservableProperty] private string _searchQuery = "";
    public ObservableCollection<WingetSearchResult> SearchResults { get; } = new();

    [ObservableProperty] private WingetSearchResult? _selectedResult;

    partial void OnSelectedResultChanged(WingetSearchResult? value)
    {
        if (value is not null)
            _ = RunAsync("Đang đọc thông tin gói...", async ct =>
                SetDraft(await _service.PrepareFromWingetAsync(value.Id, ct)));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SearchAsync() =>
        RunAsync("Đang tìm trong kho Winget...", async ct =>
        {
            ClearDraft();
            SearchResults.Clear();
            var results = await _service.SearchWingetAsync(SearchQuery, ct);
            foreach (var r in results.Take(30))
                SearchResults.Add(r);
            if (SearchResults.Count == 0)
                ErrorText = $"Không tìm thấy \"{SearchQuery}\" trong kho Winget.";
        });

    // ---------------- Direct URL ----------------

    [ObservableProperty] private string _directUrl = "";
    [ObservableProperty] private string _urlAppName = "";

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task AnalyzeUrlAsync() =>
        RunAsync("Đang đọc HTTP Header...", async ct =>
            SetDraft(await _service.PrepareFromDirectUrlAsync(DirectUrl, UrlAppName, ct)));

    // ---------------- Local file ----------------

    [ObservableProperty] private string _localPath = "";

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task BrowseAsync()
    {
        var path = _dialogs.PickInstallerFile();
        if (path is null) return;
        LocalPath = path;
        await RunAsync("Đang phân tích PE Header...", ct =>
        {
            SetDraft(_service.PrepareFromLocalFile(path));
            return Task.CompletedTask;
        });
    }

    // ---------------- Bản nháp ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _draftReady;

    public bool HasDraft => DraftReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _draftName = "";

    [ObservableProperty] private string _draftSilentArgs = "";
    [ObservableProperty] private ColorGroup _draftColorGroup = ColorGroup.Mint;
    [ObservableProperty] private string _draftPackager = "";
    [ObservableProperty] private string _draftVersion = "";
    [ObservableProperty] private string _draftSource = "";
    [ObservableProperty] private string _draftEvidence = "";
    public ObservableCollection<string> DraftWarnings { get; } = new();

    private void SetDraft(AppDraft draft)
    {
        _draft = draft;
        _suggestedSilentArgs = draft.SilentArgs;
        DraftName = draft.Name;
        DraftSilentArgs = draft.SilentArgs;
        DraftColorGroup = draft.ColorGroup;
        DraftPackager = $"{draft.InstallerType} · {draft.PackagerType}";
        DraftVersion = draft.Version ?? "(đọc sau khi tải)";
        DraftSource = draft.Mode switch
        {
            AddAppMode.Winget => $"winget: {draft.WingetId}",
            AddAppMode.DirectUrl => draft.DirectUrl ?? "",
            _ => draft.LocalSourcePath ?? ""
        };
        DraftEvidence = draft.Evidence;
        DraftWarnings.Clear();
        foreach (var w in draft.Warnings) DraftWarnings.Add(w);
        DraftReady = true;
    }

    private void ClearDraft()
    {
        _draft = null;
        DraftReady = false;
        DraftName = "";
        DraftSilentArgs = "";
        DraftPackager = "";
        DraftVersion = "";
        DraftSource = "";
        DraftEvidence = "";
        DraftWarnings.Clear();
    }

    // ---------------- Lưu ----------------

    private bool CanSave() => IsIdle && DraftReady && !string.IsNullOrWhiteSpace(DraftName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() =>
        RunAsync(Mode == AddAppMode.LocalFile ? @"Đang chép vào .\Installers\..." : @"Đang tải bản mới nhất về .\Installers\...", async ct =>
        {
            if (_draft is null) return;
            _draft.Name = DraftName.Trim();
            _draft.SilentArgs = DraftSilentArgs.Trim();
            _draft.SilentArgsEditedByUser = !string.Equals(_draft.SilentArgs, _suggestedSilentArgs, StringComparison.Ordinal);
            _draft.ColorGroup = DraftColorGroup;
            _draft.Category = DraftColorGroup switch
            {
                ColorGroup.Pink => "Browser",
                ColorGroup.Peach => "Utility",
                ColorGroup.Lavender => "RemoteControl",
                _ => "Messaging"
            };

            var progress = new Progress<double>(p =>
            {
                Progress = p;
                StatusText = $"{(Mode == AddAppMode.LocalFile ? "Đang chép" : "Đang tải")} {p:0}%";
            });
            Result = await _service.AddAsync(_draft, progress, ct);
            RequestClose?.Invoke(this, true);
        });

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            StatusText = "Đang huỷ...";
            return;
        }
        RequestClose?.Invoke(this, false);
    }

    // ---------------- Trạng thái ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(AnalyzeUrlCommand), nameof(BrowseCommand), nameof(SaveCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _progress;

    /// <summary>Huỷ thao tác đang chạy khi người dùng đóng cửa sổ.</summary>
    public void CancelPending() => _cts?.Cancel();

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = status;
        ErrorText = "";
        Progress = 0;
        try
        {
            await work(_cts.Token);
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã huỷ.";
        }
        catch (Exception ex)
        {
            StatusText = "";
            ErrorText = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            Progress = 0;
        }
    }
}

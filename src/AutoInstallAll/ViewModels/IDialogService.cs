using AutoInstallAll.Models;

namespace AutoInstallAll.ViewModels;

/// <summary>Tách ViewModel khỏi Window/MessageBox để VM không phụ thuộc View.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, bool danger = false);
    void Info(string title, string message);
    void Error(string title, string message);

    /// <summary>Mở hộp thoại + Add App; trả về ứng dụng đã thêm vào USB hoặc null nếu huỷ.</summary>
    SoftwareItem? ShowAddApp();

    string? PickInstallerFile();
    void OpenFolder(string path);
}

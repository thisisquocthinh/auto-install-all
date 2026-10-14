# Auto Install All — Windows PC Setup Toolkit

Công cụ **USB Portable** giúp kỹ thuật viên thiết lập máy Windows mới gần như tự động: cài hàng loạt phần mềm thông dụng ở chế độ âm thầm (silent), tự cập nhật bộ cài lưu trên USB, chia ổ C/D và tắt BitLocker chỉ với vài cú nhấp chuột.

> Trạng thái: **đang phát triển** (v1.0.0, chưa phát hành chính thức). Xem mục [Tiến độ phát triển](#tiến-độ-phát-triển).

## Tính năng

| Nhóm | Mô tả |
|---|---|
| **Cài đặt hàng loạt** | Hàng đợi cài tuần tự từ `.\Installers\`. `.exe`/`.msi` chạy với Silent Switch, `.zip` portable được giải nén, tạo shortcut và đăng ký Task Scheduler. Nếu thiếu bộ cài mà máy có mạng, công cụ tự tải về USB. |
| **Check Latest Versions** | Quét phiên bản bộ cài trên USB, so với bản mới nhất từ Winget, GitHub Releases hoặc HTTP Header (ETag/Last-Modified). Tải về file tạm `*.downloading`, kiểm tra dung lượng/SHA-256/chữ ký, đổi tên chính thức rồi mới xoá bản cũ. Rút USB hoặc rớt mạng giữa chừng thì bản cũ vẫn còn. |
| **+ Add App** | Thêm phần mềm mới vào USB và `manifest.json` mà không cần sửa code, theo 3 chế độ: tìm theo tên trong Winget, dán link tải cố định (Direct URL), hoặc chọn file `.exe`/`.msi`/`.zip` có sẵn trên máy. Công cụ gợi ý Silent Switch từ việc phân tích file. |
| **Quick Split C/D** | Thu nhỏ ổ C bằng `Resize-Partition`, tạo phân vùng mới (mặc định ổ `D`, nhãn `DATA`) bằng `New-Partition` và `Format-Volume` (NTFS). Chỉ dùng vùng trống cuối ổ C, không xoá hay di chuyển dữ liệu. |
| **Tắt BitLocker** | Đọc trạng thái qua `Win32_EncryptableVolume`, chạy `manage-bde -off` và theo dõi tiến độ giải mã (Decrypting xx% → Off). |
| **VC++ AIO & DirectX** | Cài Visual C++ Redistributable All-in-One và DirectX End-User Runtime (June 2010). |
| **Gỡ ứng dụng rác** | Gỡ danh sách Appx cài sẵn (bloatware) do người dùng chọn. Store, Photos, Calculator, Media Player và Xbox được giữ lại. |

### Phần mềm có sẵn trong `manifest.json`

Google Chrome, UniKey, WinRAR, UltraViewer, TeamViewer, VLC Media Player, 7-Zip, Zalo PC, Visual C++ Redistributable AIO, DirectX End-User Runtime.

## Kiến trúc USB Portable

Mọi đường dẫn đều tương đối so với thư mục chứa `AutoInstallAll.exe`. Công cụ **không ghi vào `%TEMP%`**.

```
E:\
├── AutoInstallAll.exe     # ứng dụng chính (tự chứa .NET runtime, yêu cầu quyền Admin)
├── manifest.json          # danh mục app, phiên bản, URL tải, SHA-256, Silent Switch
├── Installers\            # bộ cài offline (và file tạm *.downloading)
├── Icons\                 # icon .png của từng app (hiển thị khi offline)
├── Logs\                  # nhật ký cài đặt, BitLocker, phân vùng
└── Config\                # file phụ (ví dụ rarreg.key cho WinRAR)
```

`manifest.json` nằm cạnh file `.exe` (không nhúng vào) để kỹ thuật viên sửa trực tiếp trên USB.

## Công nghệ

- C# / **.NET 8**, **WPF**, mô hình MVVM với `CommunityToolkit.Mvvm`
- `Microsoft.Extensions.DependencyInjection`
- Publish **single-file, self-contained, ReadyToRun** cho `win-x64`
- Giao diện chủ đạo **Dark Glassmorphism** (theme `DarkGlass`)
- Single-instance (mutex) và UAC `requireAdministrator`

## Cấu trúc mã nguồn

```
src/AutoInstallAll/
├── Models/                # SoftwareItem, ManifestConfig, DiskPartitionInfo, BitLockerStatus, Enums
├── Services/
│   ├── SilentInstallService.cs      # hàng đợi cài đặt
│   ├── AutoUpdaterService.cs        # kiểm tra & thay bộ cài mới
│   ├── AddApplicationService.cs     # hộp thoại + Add App (3 chế độ)
│   ├── SystemUtilitiesService.cs    # BitLocker, Quick Split, VC++/DirectX, bloatware
│   ├── StoragePathManager.cs        # đường dẫn tương đối trên USB
│   └── Infrastructure/              # HttpDownloader, WingetClient, InstallerInspector,
│                                    # ProcessRunner, ShellInterop, LogService
├── ViewModels/            # MainViewModel, AppCardViewModel, AddAppViewModel
├── Views/                 # MainWindow, AddAppDialog, Converters
└── Themes/Theme.xaml
```

## Build & chạy

Yêu cầu: Windows 10/11 x64 (1809 trở lên), .NET 8 SDK.

```powershell
# Chạy thử
dotnet run --project src/AutoInstallAll -c Release

# Publish bản portable một file
dotnet publish src/AutoInstallAll -c Release -r win-x64 -o dist/AutoInstallAll
```

Sau khi publish, chép `AutoInstallAll.exe` và `manifest.json` vào gốc USB. Bấm **Check Latest Versions** (khi có mạng) để tải bộ cài về `.\Installers\`. Từ đó có thể dùng offline trên các máy khác.

> Thư mục `dist/` (kèm `Installers/`, `Logs/`) bị bỏ qua bởi `.gitignore` vì chứa file cài đặt dung lượng lớn.

## Tiến độ phát triển

Lộ trình ban đầu gồm 5 giai đoạn (chi tiết trong tài liệu kế hoạch).

| Giai đoạn | Nội dung | Trạng thái |
|---|---|---|
| 1. Đặc tả & kiến trúc | Cấu trúc USB, `manifest.json`, bảng Silent Switch | ✅ Xong (2 tài liệu trong repo) |
| 2. Core Engine & USB | Silent Install, đọc `FileVersionInfo`, tải/xoá bộ cài | ✅ Đã có mã nguồn |
| 3. Tiện ích hệ thống | Quick Split C/D, BitLocker, VC++/DirectX, gỡ bloatware | ✅ Đã có mã nguồn |
| 4. UI/UX | Giao diện Dark Glass, hộp thoại + Add App | ✅ Đã có mã nguồn |
| 5. Kiểm thử máy ảo | Chạy từ USB trên Windows 10/11 sạch, có mạng và ngắt mạng | 🔄 Đang thực hiện (đã build và chạy thử bản `dist`) |

### Còn lại / dự kiến

- Kiểm thử đầy đủ trên máy ảo Windows 10/11 sạch (online và offline), gồm các trường hợp timeout và UAC
- Đọc phiên bản `.msi` qua bảng Property (hiện `.msi` và `.zip` chưa đọc được version từ file)
- Bổ sung unit test và CI
- Phát hành bản Release đầu tiên

## Tài liệu

- [`Tai_Lieu_1_Ke_Hoach_Du_An_Va_Dac_Ta_Ky_Thuat_Auto_Install_All.docx`](Tai_Lieu_1_Ke_Hoach_Du_An_Va_Dac_Ta_Ky_Thuat_Auto_Install_All.docx): kế hoạch dự án và đặc tả kỹ thuật
- [`Tai_Lieu_2_Phan_Tich_Va_Thiet_Ke_UI_UX_Concept_Auto_Install_All.docx`](Tai_Lieu_2_Phan_Tich_Va_Thiet_Ke_UI_UX_Concept_Auto_Install_All.docx): phân tích và thiết kế UI/UX

## Lưu ý an toàn

Công cụ can thiệp phân vùng ổ đĩa và BitLocker. Hãy **sao lưu dữ liệu** trước khi dùng Quick Split hoặc tắt BitLocker trên máy đang có dữ liệu quan trọng.

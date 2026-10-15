@echo off
chcp 65001 >nul
title Khởi chạy Bộ Công Cụ Thiết Lập Máy Tính Mới (USB Portable)

:: 1. Tự động kiểm tra và yêu cầu quyền Administrator (UAC)
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Đang yêu cầu quyền Quản trị viên ^(Administrator^)...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

:: 2. Gỡ đánh dấu file tải từ vùng ngoài (Unblock-File) và thêm USB vào vùng an toàn Defender
echo [*] Đang cấu hình vùng an toàn cho thư mục USB...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-MpPreference -ExclusionPath '%~dp0' -ErrorAction SilentlyContinue; Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File -ErrorAction SilentlyContinue"

:: 3. Tự động nạp cấu hình Wi-Fi nếu trong USB có thư mục .\WiFi\ chứa file .xml
if exist "%~dp0WiFi\*.xml" (
    echo [*] Đang tự động nạp cấu hình Wi-Fi từ USB...
    for %%f in ("%~dp0WiFi\*.xml") do (
        netsh wlan add profile filename="%%f" user=all >nul 2>&1
    )
)

:: 4. Khởi chạy ứng dụng chính AutoInstallAll.exe
echo [*] Đang mở giao diện Thiết lập máy tính mới...
start "" "%~dp0AutoInstallAll.exe"
exit

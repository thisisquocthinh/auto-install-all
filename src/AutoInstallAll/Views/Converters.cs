using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoInstallAll.Models;

namespace AutoInstallAll.Views;

/// <summary>Đường dẫn icon trong .\Icons\ → BitmapImage (OnLoad, không khoá file trên USB).</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 64;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Enum ↔ bool cho RadioButton (ConverterParameter = tên giá trị enum).</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Enum == ConverterParameter → Visible.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>bool → Visibility; ConverterParameter="invert" để đảo.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ (parameter is string s && s == "invert");
}

/// <summary>null / chuỗi rỗng / số 0 / collection rỗng → Collapsed; ConverterParameter="invert" để đảo.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int n => n != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true
        };
        if (parameter is string p && p == "invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Chữ cái đầu tên app (icon dự phòng khi chưa trích được icon).</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? char.ToUpperInvariant(s.TrimStart()[0]).ToString() : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>ColorGroup → nền Pastel của Concept 2.</summary>
public sealed class ColorGroupToBrushConverter : IValueConverter
{
    public static Brush For(ColorGroup group) => group switch
    {
        ColorGroup.Pink => Palette.Pink,
        ColorGroup.Peach => Palette.Peach,
        ColorGroup.Lavender => Palette.Lavender,
        _ => Palette.Mint
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ColorGroup g ? For(g) : Palette.Mint;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Trạng thái → màu chữ nhãn (xanh lá thành công, đỏ lỗi, đen còn lại).</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SoftwareState.Installed or SoftwareState.Latest => Palette.Success,
        SoftwareState.Failed => Palette.Danger,
        SoftwareState.UpdateAvailable or SoftwareState.Missing => Palette.Warning,
        _ => Palette.Ink
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Bảng màu dùng trong code — đọc theo chủ đề Sáng/Tối hiện hành (Themes/Colors.*.xaml).</summary>
public static class Palette
{
    public static Brush Cream => Get("CreamBrush");
    public static Brush Beige => Get("BeigeBrush");
    public static Brush Ink => Get("InkBrush");
    public static Brush Muted => Get("MutedBrush");
    public static Brush Pink => Get("PinkBrush");
    public static Brush Peach => Get("PeachBrush");
    public static Brush Lavender => Get("LavenderBrush");
    public static Brush Mint => Get("MintBrush");
    public static Brush Success => Get("SuccessBrush");
    public static Brush Warning => Get("WarningBrush");
    public static Brush Danger => Get("DangerBrush");

    private static Brush Get(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Trạng thái → chữ tiếng Việt cho trang Kho cài đặt.</summary>
public sealed class StateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SoftwareState.Missing => "Chưa có trên USB",
        SoftwareState.Ready => "Sẵn sàng",
        SoftwareState.Latest => "Mới nhất",
        SoftwareState.UpdateAvailable => "Có bản mới",
        SoftwareState.Checking => "Đang kiểm tra",
        SoftwareState.Downloading => "Đang tải",
        SoftwareState.Verifying => "Đang xác minh",
        SoftwareState.Queued => "Đang chờ",
        SoftwareState.Installing => "Đang cài",
        SoftwareState.Installed => "Đã cài",
        SoftwareState.Failed => "Lỗi",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

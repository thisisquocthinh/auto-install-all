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

/// <summary>Bảng màu Concept 2 dùng trong code (đồng bộ với Themes/Theme.xaml).</summary>
public static class Palette
{
    public static readonly SolidColorBrush Cream = Make("#FAF7F5");
    public static readonly SolidColorBrush Beige = Make("#EFEAE4");
    public static readonly SolidColorBrush Ink = Make("#111111");
    public static readonly SolidColorBrush Muted = Make("#4B5563");
    public static readonly SolidColorBrush Pink = Make("#F4C5C7");
    public static readonly SolidColorBrush Peach = Make("#FCE0C1");
    public static readonly SolidColorBrush Lavender = Make("#D4D4FA");
    public static readonly SolidColorBrush Mint = Make("#C3F0DC");
    public static readonly SolidColorBrush Success = Make("#047857");
    public static readonly SolidColorBrush Warning = Make("#B45309");
    public static readonly SolidColorBrush Danger = Make("#B91C1C");

    private static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

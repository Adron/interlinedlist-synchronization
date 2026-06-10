using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InterlinedSync.UI.Converters;

/// <summary>
/// Maps <c>true</c> to <see cref="Visibility.Visible"/> and <c>false</c> to
/// <see cref="Visibility.Collapsed"/>. WPF ships one of these but reimplementing
/// it costs nothing and avoids pulling in PresentationFramework.Aero references
/// for the test project.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

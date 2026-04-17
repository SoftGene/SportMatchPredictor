using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.IO;

namespace SportMatchPredictor.App.Converters;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        try
        {
            return new BitmapImage(new Uri(Path.GetFullPath(path)));
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

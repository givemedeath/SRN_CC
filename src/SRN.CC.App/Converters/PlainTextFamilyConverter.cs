using System.Globalization;
using Avalonia.Data.Converters;
using SRN.CC.Core.Preview;

namespace SRN.CC.App.Converters;

public sealed class PlainTextFamilyConverter : IValueConverter
{
    public static readonly PlainTextFamilyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PreviewFamily family)
        {
            return true;
        }

        return family != PreviewFamily.Image && family != PreviewFamily.Audio;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

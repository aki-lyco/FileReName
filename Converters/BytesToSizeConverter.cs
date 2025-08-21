using System;
using System.Globalization;
using System.Windows.Data;

namespace Explore
{
    public sealed class BytesToSizeConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "";
            try
            {
                double bytes = value switch
                {
                    long l => l,
                    int i => i,
                    double d => d,
                    float f => f,
                    string s when double.TryParse(s, out var dv) => dv,
                    _ => 0d
                };

                if (bytes < 0) bytes = 0;
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                int idx = 0;
                while (bytes >= 1024 && idx < units.Length - 1)
                {
                    bytes /= 1024;
                    idx++;
                }
                return $"{bytes:0.#} {units[idx]}";
            }
            catch
            {
                return "";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

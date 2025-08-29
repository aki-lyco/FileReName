using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Explore.Converters
{
    /// <summary>
    /// フルパス(string) → ファイル名（または末端フォルダ名）だけに変換します。
    /// </summary>
    public sealed class PathToNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            try
            {
                // フォルダ末尾の区切りは除去
                s = s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // 通常はファイル名、取れない場合はディレクトリ名
                var name = Path.GetFileName(s);
                if (string.IsNullOrEmpty(name))
                    name = new DirectoryInfo(s).Name;

                return name;
            }
            catch
            {
                // 変換失敗時はそのまま
                return s ?? string.Empty;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing; // 完全修飾で曖昧さを解消
    }
}

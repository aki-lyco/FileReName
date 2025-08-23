using System;
using System.Globalization;
using System.Windows.Data;

namespace FileReName.Converters
{
    // FreshState → 絵文字
    public sealed class FreshStateToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "UpToDate" => "✅",
                "Stale" => "🟡",
                "Unindexed" => "➕",
                "Orphan" => "⛔",
                "Indexing" => "🔄",
                "Error" => "❗",
                _ => "?"
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // FreshState → ブラシ（完全修飾で曖昧さ回避）
    public sealed class FreshStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "UpToDate" => System.Windows.Media.Brushes.LimeGreen,
                "Stale" => System.Windows.Media.Brushes.Goldenrod,
                "Unindexed" => System.Windows.Media.Brushes.SteelBlue,
                "Orphan" => System.Windows.Media.Brushes.IndianRed,
                "Indexing" => System.Windows.Media.Brushes.SlateBlue,
                "Error" => System.Windows.Media.Brushes.OrangeRed,
                _ => System.Windows.Media.Brushes.Gray
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // FreshState → 説明
    public sealed class FreshStateToTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "UpToDate" => "最新の状態です",
                "Stale" => "内容が更新されています（再Index推奨）",
                "Unindexed" => "DBに未登録のファイルです（Indexで登録）",
                "Orphan" => "DBにはあるがファイルが存在しません",
                "Indexing" => "Index 実行中",
                "Error" => "アクセスまたは I/O エラー",
                _ => "不明"
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

using System;
using System.Globalization;
using System.Windows.Data;

namespace FileReName.Converters
{
    // （未使用でも残してOK）FreshState → 絵文字
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

    // FreshState → 背景色ブラシ
    public sealed class FreshStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "UpToDate" => System.Windows.Media.Brushes.LimeGreen,   // 最新
                "Stale" => System.Windows.Media.Brushes.Goldenrod,   // 差分あり
                "Unindexed" => System.Windows.Media.Brushes.SteelBlue,   // 未登録
                "Orphan" => System.Windows.Media.Brushes.IndianRed,   // 不整合
                "Indexing" => System.Windows.Media.Brushes.SlateBlue,   // 処理中
                "Error" => System.Windows.Media.Brushes.OrangeRed,   // エラー
                _ => System.Windows.Media.Brushes.Gray
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // ★ 更新：FreshState → 説明（ツールチップ用）
    public sealed class FreshStateToTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                // より簡潔で意図が伝わる文言に変更
                "UpToDate" => "DBと一致しています（再Index不要）",
                "Stale" => "ファイルが更新済みです。Indexを再実行すると一致します",
                "Unindexed" => "まだDBに登録されていません。Index実行で登録されます",
                "Orphan" => "DBにだけ残っています（ファイルは削除/移動済みの可能性）",
                "Indexing" => "Index/鮮度の計算を実行中です",
                "Error" => "アクセスできません。権限・パス・ロック状態を確認してください",
                _ => "不明な状態"
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // ★ 更新：FreshState → 一覧表示テキスト（マークは使わず短いラベル）
    public sealed class FreshStateToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString() ?? string.Empty;
            return s switch
            {
                "UpToDate" => "最新",
                "Stale" => "差分あり",
                "Unindexed" => "未登録",
                "Orphan" => "不整合",
                "Indexing" => "処理中",
                "Error" => "エラー",
                _ => s
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

using System;
using System.Globalization;
using System.Windows.Data;

namespace Explore
{
    /// <summary>
    /// Unix 時刻（秒 or ミリ秒を自動判別）を「◯分前/◯時間前/昨日/◯日前/日付」に変換します。
    /// - 60秒未満: たった今
    /// - 60分未満: n分前
    /// - 24時間未満: n時間前
    /// - 48時間未満: 昨日
    /// - 7日未満: n日前
    /// - それ以外: 年内は "M/d H:mm"、年が違えば "yyyy/M/d"
    /// </summary>
    public sealed class UnixToRelativeConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null) return "";

            try
            {
                // 入力を double → long 秒 に寄せる（ミリ秒も許容）
                double raw = value switch
                {
                    long l => l,
                    int i => i,
                    double d => d,
                    float f => f,
                    string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv) => dv,
                    _ => double.NaN
                };
                if (double.IsNaN(raw)) return "";

                // ミリ秒らしい大きな値は秒に変換
                // 10^11(≈ 1973年のms)より大きければ ms とみなす
                long seconds = raw > 1e11 ? (long)(raw / 1000d) : (long)Math.Round(raw);

                var local = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
                var now = DateTime.Now;
                var ts = now - local;

                if (ts.TotalSeconds < 0) ts = TimeSpan.Zero; // 未来は たった今 扱い

                if (ts.TotalSeconds < 60) return "たった今";
                if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}分前";
                if (ts.TotalHours < 24) return $"{(int)ts.TotalHours}時間前";

                // 昨日の判定（ローカル日付ベース）
                if ((now.Date - local.Date).TotalDays == 1) return "昨日";

                if (ts.TotalDays < 7) return $"{(int)ts.TotalDays}日前";

                var ja = new CultureInfo("ja-JP");
                if (now.Year == local.Year) return local.ToString("M/d H:mm", ja);
                return local.ToString("yyyy/M/d", ja);
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

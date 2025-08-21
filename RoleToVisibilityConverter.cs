using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Explore
{
    /// <summary>
    /// ChatMessage.Role (例: "user" / "assistant") を Visibility に変換します。
    /// - XAML で TargetRole プロパティ、または ConverterParameter で対象ロールを指定できます。
    /// - Invert=true で一致時に非表示、非一致時に表示に反転できます。
    /// - CollapseInsteadOfHidden=true のとき非表示は Collapsed、false のとき Hidden を返します。
    /// 例:
    ///   <local:RoleToVisibilityConverter x:Key="UserRoleVisible" TargetRole="user"/>
    ///   <TextBlock Visibility="{Binding Role, Converter={StaticResource UserRoleVisible}}"/>
    ///   <TextBlock Visibility="{Binding Role, Converter={StaticResource RoleToVis}, ConverterParameter=assistant}"/>
    /// </summary>
    [ValueConversion(typeof(string), typeof(Visibility))]
    public class RoleToVisibilityConverter : IValueConverter
    {
        /// <summary>表示対象のロール（例: "user", "assistant"）</summary>
        public string? TargetRole { get; set; }

        /// <summary>TargetRole/ConverterParameter との一致/不一致を反転</summary>
        public bool Invert { get; set; } = false;

        /// <summary>true: Collapsed / false: Hidden</summary>
        public bool CollapseInsteadOfHidden { get; set; } = true;

        /// <summary>互換用エイリアス（XAMLで Role="user" と書かれていても動くように）</summary>
        public string? Role { get => TargetRole; set => TargetRole = value; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var role = (value as string)?.Trim() ?? string.Empty;
            var target = (parameter as string)?.Trim() ?? (TargetRole ?? string.Empty);

            bool matched = !string.IsNullOrEmpty(target) &&
                           string.Equals(role, target, StringComparison.OrdinalIgnoreCase);

            if (Invert) matched = !matched;

            if (matched) return Visibility.Visible;
            return CollapseInsteadOfHidden ? Visibility.Collapsed : Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing; // ← 完全修飾で WPF の Binding を明示
    }
}

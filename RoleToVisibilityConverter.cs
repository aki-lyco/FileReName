using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Explore
{
    /// <summary>
    /// ChatMessage.Role (��: "user" / "assistant") �� Visibility �ɕϊ����܂��B
    /// - XAML �� TargetRole �v���p�e�B�A�܂��� ConverterParameter �őΏۃ��[�����w��ł��܂��B
    /// - Invert=true �ň�v���ɔ�\���A���v���ɕ\���ɔ��]�ł��܂��B
    /// - CollapseInsteadOfHidden=true �̂Ƃ���\���� Collapsed�Afalse �̂Ƃ� Hidden ��Ԃ��܂��B
    /// ��:
    ///   <local:RoleToVisibilityConverter x:Key="UserRoleVisible" TargetRole="user"/>
    ///   <TextBlock Visibility="{Binding Role, Converter={StaticResource UserRoleVisible}}"/>
    ///   <TextBlock Visibility="{Binding Role, Converter={StaticResource RoleToVis}, ConverterParameter=assistant}"/>
    /// </summary>
    [ValueConversion(typeof(string), typeof(Visibility))]
    public class RoleToVisibilityConverter : IValueConverter
    {
        /// <summary>�\���Ώۂ̃��[���i��: "user", "assistant"�j</summary>
        public string? TargetRole { get; set; }

        /// <summary>TargetRole/ConverterParameter �Ƃ̈�v/�s��v�𔽓]</summary>
        public bool Invert { get; set; } = false;

        /// <summary>true: Collapsed / false: Hidden</summary>
        public bool CollapseInsteadOfHidden { get; set; } = true;

        /// <summary>�݊��p�G�C���A�X�iXAML�� Role="user" �Ə�����Ă��Ă������悤�Ɂj</summary>
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
            => System.Windows.Data.Binding.DoNothing; // �� ���S�C���� WPF �� Binding �𖾎�
    }
}

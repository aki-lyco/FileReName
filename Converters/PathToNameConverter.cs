using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Explore.Converters
{
    /// <summary>
    /// �t���p�X(string) �� �t�@�C�����i�܂��͖��[�t�H���_���j�����ɕϊ����܂��B
    /// </summary>
    public sealed class PathToNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            try
            {
                // �t�H���_�����̋�؂�͏���
                s = s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // �ʏ�̓t�@�C�����A���Ȃ��ꍇ�̓f�B���N�g����
                var name = Path.GetFileName(s);
                if (string.IsNullOrEmpty(name))
                    name = new DirectoryInfo(s).Name;

                return name;
            }
            catch
            {
                // �ϊ����s���͂��̂܂�
                return s ?? string.Empty;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing; // ���S�C���ŞB����������
    }
}

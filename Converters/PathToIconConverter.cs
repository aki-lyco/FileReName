using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Explore.Converters
{
    /// <summary>
    /// �p�X(�܂��͊g���q)��Windows�V�F���A�C�R��(ImageSource) �ϊ��B
    /// ��)
    ///   �t�@�C��: Source="{Binding FullPath, Converter={StaticResource PathToIcon}}"
    ///   �t�H���_: Source="{Binding FullPath, Converter={StaticResource PathToIcon}, ConverterParameter=Folder}"
    /// </summary>
    public sealed class PathToIconConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path)) return DependencyProperty.UnsetValue;

            bool wantFolder = string.Equals(parameter as string, "Folder", StringComparison.OrdinalIgnoreCase)
                              || Directory.Exists(path);

            // �g���q�P�ʂŃL���b�V���i�t�H���_�͌Œ�L�[�j
            var key = wantFolder ? "__FOLDER__" : (Path.GetExtension(path) ?? "__NOEXT__").ToLowerInvariant();

            if (_cache.TryGetValue(key, out var hit)) return hit;

            var src = GetShellIcon(path, wantFolder);
            if (src != null)
            {
                src.Freeze(); // �X���b�h�Z�[�t
                _cache[key] = src;
                return src;
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        // ---- Win32 ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_SMALLICON = 0x000000001; // �傫���̂��ǂ���� SHGFI_LARGEICON

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private static ImageSource? GetShellIcon(string path, bool folder)
        {
            var info = new SHFILEINFO();

            // ���t�@�C�����J�����A��ʁi�g���q/�t�H���_�j����A�C�R���擾
            uint attr = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;

            // �t�@�C���͊g���q��n���Ɗ֘A�t���A�C�R���i��/�A�v���A�C�R���j���Ԃ�
            string query = folder ? path : (Path.GetExtension(path) is { Length: > 0 } ext ? ext : path);

            IntPtr r = SHGetFileInfo(query, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DestroyIcon(info.hIcon); // �n���h�����[�N�h�~
            }
        }
    }
}

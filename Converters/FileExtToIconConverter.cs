using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Windows.Interop; // ← これが必要（Imaging.CreateBitmapSourceFromHIcon）

namespace Explore
{
    public sealed class FileExtToIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var key = ToKey(value);
                if (string.IsNullOrEmpty(key)) return null;

                if (_cache.TryGetValue(key, out var cached))
                    return cached;

                var img = GetIconImage(key);
                if (img != null) _cache[key] = img;
                return img;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static string ToKey(object? value)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return "";

            s = s.Trim();

            if (Directory.Exists(s))
                return "<folder>";

            if (s.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                var ext = Path.GetExtension(s);
                if (string.IsNullOrEmpty(ext))
                    return "<folder>";
                return ext.StartsWith(".") ? ext : "." + ext;
            }

            if (s == "<folder>" || s.Equals("folder", StringComparison.OrdinalIgnoreCase))
                return "<folder>";

            if (!s.StartsWith("."))
                s = "." + s;

            return s;
        }

        private static ImageSource? GetIconImage(string key)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                SHFILEINFO shinfo = new();
                uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | SHGFI_SMALLICON;
                uint attr = (key == "<folder>") ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                string path = (key == "<folder>") ? "folder" : key;

                IntPtr ret = SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (ret == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                    return null;

                hIcon = shinfo.hIcon;
                using Icon icon = Icon.FromHandle(hIcon);
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
                bs.Freeze();
                return bs;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        #region P/Invoke

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_SMALLICON = 0x000000001;

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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

        [DllImport("Shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        #endregion
    }
}

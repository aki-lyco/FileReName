using System;
using System.Collections.Generic;
using System.IO;

namespace Explore
{
    public static class AutoIndexPolicy
    {
        // 上限
        public const int MaxFilesPerAutoIndex = 4000;
        public const long MaxBytesPerFile = 500L * 1024 * 1024; // 500MB

        // OS/内部のパス（前方一致）
        private static readonly string[] PathPrefixes = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
        };

        // ディレクトリ名で弾く
        private static readonly HashSet<string> BadDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",".svn",".vs",".idea",".cache","__pycache__",".venv","node_modules",
            "bin","obj","packages","$Recycle.Bin","System Volume Information","Temp","tmp"
        };

        // 拡張子（ドット付き / 小文字）
        private static readonly HashSet<string> GoodExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",".md",".pdf",".docx",".xlsx",".pptx",
            ".jpg",".jpeg",".png",".gif",".webp",".heic",
            ".mp3",".flac",".wav",".mp4",".mov",".mkv",
            ".zip",".7z",".rar"
        };

        public static bool ShouldSkipPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return true;

            // 大域パス（Windows/ProgramFilesなど）
            foreach (var p in PathPrefixes)
                if (!string.IsNullOrEmpty(p) && fullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;

            // AppData 以下・一時領域を雑に弾く
            var fp = fullPath.Replace('/', '\\');
            if (fp.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fp.IndexOf("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fp.IndexOf("\\System Volume Information\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 名前ベース
            foreach (var bad in BadDirNames)
                if (fp.IndexOf("\\" + bad + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            return false;
        }

        public static bool ShouldIndexFile(FileInfo fi)
        {
            try
            {
                if (!fi.Exists) return false;
                var attr = fi.Attributes;
                if ((attr & (FileAttributes.System | FileAttributes.Hidden)) != 0) return false;
                if ((attr & FileAttributes.ReparsePoint) != 0) return false; // ジャンクション等
                if (ShouldSkipPath(fi.FullName)) return false;
                if (fi.Length > MaxBytesPerFile) return false;

                // 拡張子ホワイトリスト
                return GoodExts.Contains(fi.Extension);
            }
            catch { return false; }
        }
    }
}

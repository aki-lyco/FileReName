using System;
using System.Collections.Generic;
using System.IO;

namespace Explore
{
    public static class AutoIndexPolicy
    {
        // ���
        public const int MaxFilesPerAutoIndex = 4000;
        public const long MaxBytesPerFile = 500L * 1024 * 1024; // 500MB

        // OS/�����̃p�X�i�O����v�j
        private static readonly string[] PathPrefixes = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
        };

        // �f�B���N�g�����Œe��
        private static readonly HashSet<string> BadDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",".svn",".vs",".idea",".cache","__pycache__",".venv","node_modules",
            "bin","obj","packages","$Recycle.Bin","System Volume Information","Temp","tmp"
        };

        // �g���q�i�h�b�g�t�� / �������j
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

            // ���p�X�iWindows/ProgramFiles�Ȃǁj
            foreach (var p in PathPrefixes)
                if (!string.IsNullOrEmpty(p) && fullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;

            // AppData �ȉ��E�ꎞ�̈���G�ɒe��
            var fp = fullPath.Replace('/', '\\');
            if (fp.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fp.IndexOf("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fp.IndexOf("\\System Volume Information\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // ���O�x�[�X
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
                if ((attr & FileAttributes.ReparsePoint) != 0) return false; // �W�����N�V������
                if (ShouldSkipPath(fi.FullName)) return false;
                if (fi.Length > MaxBytesPerFile) return false;

                // �g���q�z���C�g���X�g
                return GoodExts.Contains(fi.Extension);
            }
            catch { return false; }
        }
    }
}

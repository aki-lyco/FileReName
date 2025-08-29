using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Explore.Build
{
    public static class PathUtils
    {
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
        private const int MaxRelLength = 240;

        public static string NormalizeRelPath(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return "";
            rel = rel.Replace('\\', '/').Trim();
            while (rel.Contains("//")) rel = rel.Replace("//", "/");
            rel = rel.Trim('/');
            var segs = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => s.Trim())
                          .ToArray();
            return string.Join("/", segs);
        }

        // ★ 互換: 相対パスをセグメント配列へ
        public static string[] SplitRel(string rel)
        {
            rel = NormalizeRelPath(rel);
            if (string.IsNullOrEmpty(rel)) return Array.Empty<string>();
            return rel.Split('/');
        }

        public static bool IsValidRelPath(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            rel = NormalizeRelPath(rel);
            if (rel.Length == 0 || rel.Length > MaxRelLength) return false;

            foreach (var seg in rel.Split('/'))
            {
                if (string.IsNullOrWhiteSpace(seg)) return false;
                if (seg == "." || seg == "..") return false;
                if (seg.IndexOfAny(InvalidFileNameChars) >= 0) return false;

                var upper = seg.ToUpperInvariant();
                if (upper is "CON" or "PRN" or "AUX" or "NUL") return false;
                if (Regex.IsMatch(upper, @"^(COM[1-9]|LPT[1-9])$")) return false;
            }
            return true;
        }

        public static string JoinRel(string baseRel, string name)
        {
            baseRel = NormalizeRelPath(baseRel ?? "");
            name = MakeSafeName(name ?? "");
            if (string.IsNullOrEmpty(baseRel)) return name;
            return $"{baseRel}/{name}";
        }

        public static string MakeSafeName(string name)
        {
            if (name == null) name = "";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name.Trim())
            {
                if (InvalidFileNameChars.Contains(ch)) sb.Append('_');
                else sb.Append(ch);
            }
            var safe = sb.ToString();
            if (string.IsNullOrWhiteSpace(safe)) safe = "NewFolder";
            return safe;
        }

        public static string GetParentRel(string rel)
        {
            rel = NormalizeRelPath(rel);
            var idx = rel.LastIndexOf('/');
            if (idx < 0) return "";
            return rel.Substring(0, idx);
        }

        public static string GetFileName(string rel)
        {
            rel = NormalizeRelPath(rel);
            var idx = rel.LastIndexOf('/');
            if (idx < 0) return rel;
            return rel[(idx + 1)..];
        }

        public static string CombineBase(string basePath, string relPath)
        {
            var rel = NormalizeRelPath(relPath ?? "");
            var combined = Path.Combine(basePath ?? "", rel.Replace('/', Path.DirectorySeparatorChar));
            return Path.GetFullPath(combined);
        }
    }
}

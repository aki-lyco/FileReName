using System;
using System.Collections.Generic;
using System.Text;

namespace Explore
{
    /// <summary>
    /// スレッドセーフなログバッファ。SearchNlp / SearchService からの DebugLog を集約。
    /// </summary>
    public static class DebugHub
    {
        private static readonly object _gate = new();
        private static readonly LinkedList<string> _lines = new();
        private const int MaxLines = 4000;

        public static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            lock (_gate)
            {
                _lines.AddLast(line);
                if (_lines.Count > MaxLines) _lines.RemoveFirst();
            }
            try { System.Diagnostics.Debug.WriteLine(line); } catch { /* ignore */ }
        }

        public static string Snapshot()
        {
            lock (_gate)
            {
                var sb = new StringBuilder(_lines.Count * 64);
                foreach (var l in _lines) sb.AppendLine(l);
                return sb.ToString();
            }
        }

        public static void Clear()
        {
            lock (_gate) _lines.Clear();
        }
    }
}

// System/ExplorerBackend.cs などに配置してOK
// ※ 重要: フォルダ名はどこでもよいですが、namespace は Explore.FileSystem に固定してください。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

// 追加: Win32 列挙用
using System.Runtime.InteropServices;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Explore.FileSystem
{
    // ====== 共通ベース ======
    public abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true;
        }
        protected void Raise([CallerMemberName] string? name = null)
        { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _run; private readonly Func<bool>? _can;
        public RelayCommand(Action run, Func<bool>? can = null) { _run = run; _can = can; }
        public bool CanExecute(object? p) => _can?.Invoke() ?? true;
        public void Execute(object? p) => _run();
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ====== ファイル/フォルダー 行 ======
    public sealed class FileItem : NotifyBase
    {
        public string Name { get; }
        public string FullPath { get; }
        public string Extension { get; }
        public long Size { get; }
        public DateTime LastWriteTime { get; }
        public bool IsDirectory { get; }

        public FileItem(FileInfo fi)
        {
            Name = fi.Name;
            FullPath = fi.FullName;
            Extension = fi.Extension;
            Size = fi.Exists ? fi.Length : 0;
            LastWriteTime = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
            IsDirectory = false;
        }

        // フォルダー用
        public FileItem(DirectoryInfo di)
        {
            Name = di.Name;
            FullPath = di.FullName;
            Extension = string.Empty;
            Size = 0;
            LastWriteTime = di.Exists ? di.LastWriteTime : DateTime.MinValue;
            IsDirectory = true;
        }

        // Win32 直列挙用
        public FileItem(string fullPath, string name, string? extension, long size, DateTime lastWriteTime, bool isDirectory)
        {
            FullPath = fullPath;
            Name = name;
            Extension = extension ?? System.IO.Path.GetExtension(name);
            Size = size;
            LastWriteTime = lastWriteTime;
            IsDirectory = isDirectory;
        }
    }

    // ====== フォルダノード（遅延ロード） ======
    public sealed class FolderNode : NotifyBase
    {
        private bool _isExpanded;
        internal bool _isLoaded;
        public string Name { get; }
        public string FullPath { get; }
        public ObservableCollection<FolderNode> Children { get; } = new();

        private FolderNode(string name, string path) { Name = name; FullPath = path; }

        public static FolderNode Create(string path)
        {
            var di = new DirectoryInfo(path);
            var name = string.IsNullOrEmpty(di.Name) ? path : di.Name;
            var node = new FolderNode(name, path);
            node.Children.Add(new FolderNode("(loading...)", "__PLACEHOLDER__")); // 展開矢印用ダミー
            return node;
        }

        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

        internal void ReplaceChildren(IEnumerable<FolderNode> newChildren)
        {
            Children.Clear(); foreach (var c in newChildren) Children.Add(c); Raise(nameof(Children));
        }
        internal void ClearPlaceholderIfAny()
        {
            if (Children.Count == 1 && Children[0].FullPath == "__PLACEHOLDER__") Children.Clear();
        }
    }

    // ====== ファイルシステム抽象 ======
    public interface IFileSystemService
    {
        IEnumerable<string> GetLogicalDrives();
        Task<IReadOnlyList<string>> GetSubfoldersAsync(string path, CancellationToken ct);
        Task<IReadOnlyList<FileItem>> GetFilesAsync(string path, CancellationToken ct);
    }

    public sealed class DefaultFileSystemService : IFileSystemService
    {
        public IEnumerable<string> GetLogicalDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady) yield return d.RootDirectory.FullName;
        }

        public async Task<IReadOnlyList<string>> GetSubfoldersAsync(string path, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var list = new List<string>();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var di = new DirectoryInfo(dir);
                            // 隠し・システムは非表示
                            if ((di.Attributes & FileAttributes.Hidden) != 0) continue;
                            if ((di.Attributes & FileAttributes.System) != 0) continue;
                            list.Add(dir);
                        }
                        catch { /* skip 個別フォルダ */ }
                    }
                }
                catch { /* skip ルート列挙失敗 */ }
                return (IReadOnlyList<string>)list;
            }, ct);
        }

        public async Task<IReadOnlyList<FileItem>> GetFilesAsync(string path, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                // ★ Win32 高速列挙 -> 失敗時はフォールバック
                try { return (IReadOnlyList<FileItem>)FastEnumerateFiles(path, ct); }
                catch
                {
                    var list = new List<FileItem>();
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(path))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var fi = new FileInfo(file);
                                if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                                if ((fi.Attributes & FileAttributes.System) != 0) continue;
                                list.Add(new FileItem(fi));
                            }
                            catch { /* skip 個別ファイル */ }
                        }
                    }
                    catch { /* skip */ }
                    return (IReadOnlyList<FileItem>)list;
                }
            }, ct);
        }

        #region Win32 fast enumeration (files only)
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;

        private enum FINDEX_INFO_LEVELS { Standard = 0, Basic = 1 }
        private enum FINDEX_SEARCH_OPS { NameMatch = 0 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public FileAttributes dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        private static List<FileItem> FastEnumerateFiles(string path, CancellationToken ct)
        {
            var list = new List<FileItem>();
            var pattern = System.IO.Path.Combine(path, "*");

            WIN32_FIND_DATA data;
            var h = FindFirstFileEx(pattern, FINDEX_INFO_LEVELS.Basic, out data,
                                    FINDEX_SEARCH_OPS.NameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (h == INVALID_HANDLE_VALUE)
                throw new IOException("FindFirstFileEx failed.");

            try
            {
                do
                {
                    ct.ThrowIfCancellationRequested();

                    var name = data.cFileName;
                    if (name == "." || name == "..") continue;

                    var attrs = data.dwFileAttributes;
                    if ((attrs & FileAttributes.Directory) != 0) continue; // フォルダは除外
                    if ((attrs & FileAttributes.Hidden) != 0) continue;
                    if ((attrs & FileAttributes.System) != 0) continue;

                    long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    long ft = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | (uint)data.ftLastWriteTime.dwLowDateTime;
                    var lastWrite = DateTime.FromFileTimeUtc(ft).ToLocalTime();

                    var full = System.IO.Path.Combine(path, name);
                    var ext = System.IO.Path.GetExtension(name);

                    list.Add(new FileItem(full, name, ext, size, lastWrite, isDirectory: false));
                }
                while (FindNextFile(h, out data));
            }
            finally { FindClose(h); }

            return list;
        }
        #endregion
    }

    // ====== ViewModel（心臓部） ======
    public sealed class ExplorerViewModel : NotifyBase
    {
        private readonly IFileSystemService _fs;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _cts;

        private string _currentPath = string.Empty;
        public string CurrentPath { get => _currentPath; private set => Set(ref _currentPath, value); }

        public ObservableCollection<FolderNode> Roots { get; } = new();
        public ObservableCollection<FileItem> Files { get; } = new();

        public ICommand RefreshCommand { get; }

        public ExplorerViewModel(IFileSystemService fs)
        {
            _fs = fs;
            RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !string.IsNullOrWhiteSpace(CurrentPath));
        }

        public Task LoadRootsAsync()
        {
            Roots.Clear();
            foreach (var d in _fs.GetLogicalDrives())
                Roots.Add(FolderNode.Create(d));

            if (Roots.Count == 0)
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Roots.Add(FolderNode.Create(profile));
            }
            return Task.CompletedTask;
        }

        public async Task EnsureChildrenLoadedAsync(FolderNode node)
        {
            if (node._isLoaded) return;
            node.ClearPlaceholderIfAny();

            using var localCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var subs = await _fs.GetSubfoldersAsync(node.FullPath, localCts.Token);
            node.ReplaceChildren(subs.Select(FolderNode.Create));
            node.IsExpanded = true;
            node._isLoaded = true;
        }

        public async Task NavigateToAsync(string path)
        {
            await _gate.WaitAsync();
            try
            {
                // 前回の列挙を止める
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                CurrentPath = path;
                Files.Clear();
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();

                // BGでストリーミング列挙開始（戻りは即）
                _ = Task.Run(async () =>
                {
                    const int FirstBurst = 100;
                    const int NextBatch = 100;

                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                    };

                    var buffer = new List<FileItem>(NextBatch);
                    int pushed = 0;

                    void FlushToUi()
                    {
                        if (buffer.Count == 0) return;
                        var copy = buffer.ToArray();
                        buffer.Clear();
                        // WPFのUIスレッドでObservableCollectionに加える
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var it in copy) Files.Add(it);
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }

                    try
                    {
                        // 2) ファイル
                        foreach (var filePath in Directory.EnumerateFiles(path, "*", opts))
                        {
                            ct.ThrowIfCancellationRequested();
                            FileItem it;
                            try
                            {
                                var fi = new FileInfo(filePath);
                                it = new FileItem(fi);
                            }
                            catch { continue; }

                            buffer.Add(it);
                            pushed++;

                            if (pushed <= FirstBurst)
                            {
                                FlushToUi();
                                continue;
                            }

                            if (buffer.Count >= NextBatch)
                            {
                                FlushToUi();
                                await Task.Yield(); // UIに描画時間を譲る
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* フォルダ切替など */ }
                    catch { /* 個別例外は握りつぶし */ }
                    finally
                    {
                        // 端数flush
                        FlushToUi();
                    }
                }, ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RefreshAsync()
        {
            if (!string.IsNullOrWhiteSpace(CurrentPath))
                await NavigateToAsync(CurrentPath);
        }
    }
}

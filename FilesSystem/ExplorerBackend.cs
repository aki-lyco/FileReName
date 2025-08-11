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

    // ====== ファイル行 ======
    public sealed class FileItem : NotifyBase
    {
        public string Name { get; }
        public string FullPath { get; }
        public string Extension { get; }
        public long Size { get; }
        public DateTime LastWriteTime { get; }
        public FileItem(FileInfo fi)
        {
            Name = fi.Name;
            FullPath = fi.FullName;
            Extension = fi.Extension;
            Size = fi.Exists ? fi.Length : 0;
            LastWriteTime = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
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
            }, ct);
        }
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
                _cts?.Cancel();
                _cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ct = _cts.Token;

                CurrentPath = path;
                var items = await _fs.GetFilesAsync(path, ct);

                Files.Clear();
                foreach (var it in items) Files.Add(it);

                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            finally { _gate.Release(); }
        }

        public async Task RefreshAsync()
        {
            if (!string.IsNullOrWhiteSpace(CurrentPath))
                await NavigateToAsync(CurrentPath);
        }
    }
}

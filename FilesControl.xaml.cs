// Explore/FilesControl.xaml.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;      // TreeViewItem, MenuItem
using Explore.FileSystem;           // ExplorerViewModel, FolderNode, DefaultFileSystemService
using Explore.Indexing;             // IndexDatabase, DbFileRecord, FileKeyUtil, FreshnessService, FreshState
using Microsoft.Data.Sqlite;
using WpfMessageBox = System.Windows.MessageBox;

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        // 左ペインVM
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());
        public ExplorerViewModel VM => _vm;

        // DB ＆ 鮮度
        private static readonly IndexDatabase _db = new();
        private static readonly FreshnessService _fresh = new(_db);

        // トグル（今はUIが無いので false 固定）
        private bool _includeSubs = false;

        // キャンセル
        private CancellationTokenSource? _indexCts;
        private CancellationTokenSource? _freshCts;

        // 進捗
        private bool _isIndexing;
        public bool IsIndexing { get => _isIndexing; private set { _isIndexing = value; Raise(); } }

        private double _indexPercent;
        public double IndexPercent { get => _indexPercent; private set { _indexPercent = value; Raise(); } }

        private string _indexStatusText = "待機中";
        public string IndexStatusText { get => _indexStatusText; private set { _indexStatusText = value; Raise(); } }

        private string _indexedCountText = "";
        public string IndexedCountText { get => _indexedCountText; private set { _indexedCountText = value; Raise(); } }

        // 鮮度％
        private double _freshnessPercent;
        public double FreshnessPercent { get => _freshnessPercent; private set { _freshnessPercent = value; Raise(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public FilesControl()
        {
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                DataContext = this;
                await _db.EnsureCreatedAsync();
                await _vm.LoadRootsAsync();
            };
        }

        // ===== 一覧VM =====
        public sealed class FileRow : INotifyPropertyChanged
        {
            public string FullPath { get; init; } = "";
            public string Name { get; init; } = "";
            public string Extension { get; init; } = "";
            public long Size { get; init; }
            public DateTime LastWriteTime { get; init; }

            private FreshState _fresh = FreshState.Unindexed;
            public FreshState FreshState
            {
                get => _fresh;
                set { if (_fresh != value) { _fresh = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreshState))); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public ObservableCollection<FileRow> Rows { get; } = new();

        // ===== ツリー操作 =====
        private async void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
                await _vm.EnsureChildrenLoadedAsync(node);
        }

        private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not FolderNode node) return;

            await _vm.NavigateToAsync(node.FullPath);

            // FS→行を構築
            Rows.Clear();
            foreach (var f in _vm.Files)
            {
                Rows.Add(new FileRow
                {
                    FullPath = f.FullPath,
                    Name = f.Name,
                    Extension = f.Extension,
                    Size = f.Size,
                    LastWriteTime = f.LastWriteTime
                });
            }

            // 行鮮度→全体％
            _fresh.InvalidateFreshnessCache(node.FullPath);
            await RefreshRowsFreshnessAsync(node.FullPath);
            _ = RecalcFreshnessPercentAsync(node.FullPath);
        }

        // ===== ボタン：このフォルダを同期 =====
        private async void OnIndexCurrentFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();

                var root = _vm?.CurrentPath;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    WpfMessageBox.Show("左のツリーでフォルダーを選んでください。");
                    return;
                }

                await _db.EnsureCreatedAsync();

                IsIndexing = true;
                IndexPercent = 0;
                IndexStatusText = "準備中…";

                var prog = new Progress<(long scanned, long inserted)>(p =>
                {
                    IndexStatusText = $"登録中 {p.scanned:N0} 件（新規 {p.inserted:N0}）";
                });

                var records = EnumerateRecordsAsync(root, _includeSubs, _indexCts.Token);

                var (scanned, inserted) = await _db.BulkUpsertAsync(
                    records,
                    batchSize: 500,
                    progress: prog,
                    ct: _indexCts.Token);

                IndexedCountText = $"DB件数: {await GetTableCountAsync("files"):N0}";
                IndexStatusText = "完了";

                // 鮮度更新
                _fresh.InvalidateFreshnessCache(root);
                await RefreshRowsFreshnessAsync(root);
                _ = RecalcFreshnessPercentAsync(root);
            }
            catch (OperationCanceledException)
            {
                IndexStatusText = "キャンセルしました";
            }
            catch (Exception ex)
            {
                IndexStatusText = "エラー: " + ex.Message;
                WpfMessageBox.Show(ex.Message, "Index Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsIndexing = false;
                _indexCts?.Dispose();
                _indexCts = null;
            }
        }

        // ===== ボタン：差分のみIndex（Unindexedだけ高速UPSERT） =====
        private async void OnIndexOnlyUnindexedClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = _vm?.CurrentPath;
                if (string.IsNullOrWhiteSpace(root)) return;

                int done = 0;
                foreach (var r in Rows)
                {
                    if (r.FreshState != FreshState.Unindexed) continue;
                    try
                    {
                        await _db.UpsertFileFromFsAsync(r.FullPath, CancellationToken.None);
                        done++;
                        if ((uint)Environment.TickCount % 64 == 0) await Task.Yield();
                    }
                    catch { /* 個別失敗は握りつぶす */ }
                }

                _fresh.InvalidateFreshnessCache(root);
                await RefreshRowsFreshnessAsync(root);
                _ = RecalcFreshnessPercentAsync(root);

                WpfMessageBox.Show($"差分Index 完了：{done} 件", "差分のみIndex", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "差分Index Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== 行メニュー =====
        private async void OnReindexFileClick(object sender, RoutedEventArgs e)
        {
            var path = (sender as MenuItem)?.CommandParameter as string ?? GetPathFromContext(sender);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                await _db.UpsertFileFromFsAsync(path!, CancellationToken.None);
                _fresh.InvalidateFreshnessCache(Path.GetDirectoryName(path!) ?? "");
                await RefreshRowsFreshnessAsync(_vm.CurrentPath);
                _ = RecalcFreshnessPercentAsync(_vm.CurrentPath);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "ReIndex Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnDeleteFromDbClick(object sender, RoutedEventArgs e)
        {
            var path = (sender as MenuItem)?.CommandParameter as string ?? GetPathFromContext(sender);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                await _db.DeleteByPathAsync(path!, CancellationToken.None);
                _fresh.InvalidateFreshnessCache(Path.GetDirectoryName(path!) ?? "");
                await RefreshRowsFreshnessAsync(_vm.CurrentPath);
                _ = RecalcFreshnessPercentAsync(_vm.CurrentPath);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "DB Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnOpenLocationClick(object sender, RoutedEventArgs e)
        {
            var path = (sender as MenuItem)?.CommandParameter as string ?? GetPathFromContext(sender);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var args = $"/select,\"{path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "Open Location Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string? GetPathFromContext(object? sender)
        {
            if (sender is MenuItem mi && mi.DataContext is FileRow r1) return r1.FullPath;
            return null;
        }

        // ===== ユーティリティ =====
        private async IAsyncEnumerable<DbFileRecord> EnumerateRecordsAsync(
            string root, bool recursive, [EnumeratorCancellation] CancellationToken ct)
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            foreach (var path in Directory.EnumerateFiles(root, "*", opts))
            {
                ct.ThrowIfCancellationRequested();

                DbFileRecord? rec = null;
                try
                {
                    var fi = new FileInfo(path);
                    rec = new DbFileRecord
                    {
                        FileKey = FileKeyUtil.GetStableKey(fi.FullName),
                        Path = fi.FullName,
                        Parent = fi.DirectoryName,
                        Name = fi.Name,
                        Ext = fi.Extension,
                        Size = fi.Exists ? fi.Length : 0,
                        MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                        CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                        Mime = null,
                        Summary = null,
                        Snippet = null,
                        Tags = null,
                        Classified = null,
                        IndexedAt = 0
                    };
                }
                catch { /* 個別失敗はスキップ */ }

                if (rec != null) yield return rec;
                if ((uint)Environment.TickCount % 2048 == 0)
                    await Task.Yield();
            }
        }

        private async Task<long> GetTableCountAsync(string table)
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = IndexDatabase.DatabasePath }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        // ===== 鮮度更新 =====
        private async Task RefreshRowsFreshnessAsync(string scopePath)
        {
            _freshCts?.Cancel();
            _freshCts = new CancellationTokenSource();

            try
            {
                foreach (var row in Rows)
                {
                    var st = await _fresh.GetFreshStateByPathAsync(row.FullPath, _freshCts.Token);
                    row.FreshState = st;
                    if ((uint)Environment.TickCount % 256 == 0) await Task.Yield();
                }
            }
            catch (OperationCanceledException) { /* ignore */ }
        }

        private async Task RecalcFreshnessPercentAsync(string scopePath)
        {
            try
            {
                var pct = await _fresh.CalcFreshnessPercentAsync(scopePath, CancellationToken.None);
                FreshnessPercent = Math.Clamp(pct * 100.0, 0.0, 100.0);
            }
            catch { /* ignore */ }
        }
    }
}

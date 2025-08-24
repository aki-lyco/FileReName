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
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Media;
using Explore.FileSystem;
using Explore.Indexing;
using Microsoft.Data.Sqlite;
using WpfMessageBox = System.Windows.MessageBox;
// ★ WPF 入力系をエイリアスで明示（KeyEventArgs の曖昧参照を解消）
using Input = System.Windows.Input;

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());
        public ExplorerViewModel VM => _vm;

        private static readonly IndexDatabase _db = new();
        private static readonly FreshnessService _fresh = new(_db);

        private bool _includeSubs = false;

        private CancellationTokenSource? _indexCts;
        private CancellationTokenSource? _freshCts;

        private bool _isIndexing;
        public bool IsIndexing { get => _isIndexing; private set { _isIndexing = value; Raise(); } }

        private double _indexPercent;
        public double IndexPercent { get => _indexPercent; private set { _indexPercent = value; Raise(); } }

        private string _indexStatusText = "待機中";
        public string IndexStatusText { get => _indexStatusText; private set { _indexStatusText = value; Raise(); } }

        private string _indexedCountText = "";
        public string IndexedCountText { get => _indexedCountText; private set { _indexedCountText = value; } }

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

            var root = _vm.CurrentPath;
            if (string.IsNullOrWhiteSpace(root)) return;

            _fresh.InvalidateFreshnessCache(root!);
            await RefreshRowsFreshnessAsync(root!);
            _ = RecalcFreshnessPercentAsync(root!);
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

                var records = EnumerateRecordsAsync(root!, _includeSubs, _indexCts.Token);

                var (scanned, inserted) = await _db.BulkUpsertAsync(
                    records, batchSize: 500, progress: prog, ct: _indexCts.Token);

                IndexedCountText = $"DB件数: {await GetTableCountAsync("files"):N0}";
                IndexStatusText = "完了";

                _fresh.InvalidateFreshnessCache(root!);
                await RefreshRowsFreshnessAsync(root!);
                _ = RecalcFreshnessPercentAsync(root!);
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

        // ===== ボタン：差分のみIndex =====
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
                    catch { /* ignore */ }
                }

                _fresh.InvalidateFreshnessCache(root!);
                await RefreshRowsFreshnessAsync(root!);
                _ = RecalcFreshnessPercentAsync(root!);

                WpfMessageBox.Show($"差分Index 完了：{done} 件", "差分のみIndex",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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

                var current = _vm?.CurrentPath ?? string.Empty;
                var dir = Path.GetDirectoryName(path!) ?? string.Empty;
                _fresh.InvalidateFreshnessCache(!string.IsNullOrWhiteSpace(dir) ? dir : current);

                if (!string.IsNullOrWhiteSpace(current))
                {
                    await RefreshRowsFreshnessAsync(current);
                    _ = RecalcFreshnessPercentAsync(current);
                }
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

                var current = _vm?.CurrentPath ?? string.Empty;
                var dir = Path.GetDirectoryName(path!) ?? string.Empty;
                _fresh.InvalidateFreshnessCache(!string.IsNullOrWhiteSpace(dir) ? dir : current);

                if (!string.IsNullOrWhiteSpace(current))
                {
                    await RefreshRowsFreshnessAsync(current);
                    _ = RecalcFreshnessPercentAsync(current);
                }
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

        // ===== 追加：開く（ダブルクリック／Enter／右クリック） =====
        private void OnFilesGridDoubleClick(object sender, Input.MouseButtonEventArgs e)
        {
            // ヘッダや空白のダブルクリックは無視
            var dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow && dep is not DataGridCell && dep is not DataGrid)
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is DataGridRow row && row.Item is FileRow fr)
            {
                OpenFile(fr.FullPath);
                e.Handled = true;
                return;
            }

            if (FilesGrid?.SelectedItem is FileRow sel)
            {
                OpenFile(sel.FullPath);
                e.Handled = true;
            }
        }

        private void OnFilesGridKeyDown(object sender, Input.KeyEventArgs e) // ★ ここを WPF に固定
        {
            if (e.Key == Input.Key.Enter && FilesGrid?.SelectedItem is FileRow fr)
            {
                OpenFile(fr.FullPath);
                e.Handled = true;
            }
        }

        private void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            var path = (sender as MenuItem)?.CommandParameter as string ?? GetPathFromContext(sender);
            if (string.IsNullOrWhiteSpace(path)) return;
            OpenFile(path!);
        }

        private void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    WpfMessageBox.Show($"ファイルが存在しません。\n{path}", "開く", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "open"
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "開くエラー", MessageBoxButton.OK, MessageBoxImage.Error);
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
                catch { }

                if (rec != null) yield return rec;
                if ((uint)Environment.TickCount % 2048 == 0)
                    await Task.Yield();
            }
        }

        private async Task<long> GetTableCountAsync(string table)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = IndexDatabase.DatabasePath }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }

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

        // ==== 一覧再読込 ====
        private async Task RefreshCurrentFolderViewAsync()
        {
            var root = _vm?.CurrentPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            await _vm.NavigateToAsync(root!);

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

            _fresh.InvalidateFreshnessCache(root!);
            await RefreshRowsFreshnessAsync(root!);
            _ = RecalcFreshnessPercentAsync(root!);
        }

        #region NewFileCreate
        private void OnNewButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }

        private async void OnImportFilesClick(object sender, RoutedEventArgs e)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "追加するファイルを選択",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
            {
                int ok = 0, fail = 0;
                foreach (var src in dlg.FileNames)
                {
                    try
                    {
                        var name = Path.GetFileName(src);
                        var dst = GetUniquePath(dir, name);
                        File.Copy(src, dst);
                        ok++;
                    }
                    catch { fail++; }
                }
                await RefreshCurrentFolderViewAsync();
                IndexedCountText = $"追加: {ok}, 失敗: {fail}";
            }
        }

        private async void OnNewFolderClick(object sender, RoutedEventArgs e)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var baseName = "新しいフォルダー";
            var name = baseName;
            int i = 2;
            while (Directory.Exists(Path.Combine(dir, name)))
                name = $"{baseName} ({i++})";
            Directory.CreateDirectory(Path.Combine(dir, name));
            await RefreshCurrentFolderViewAsync();
        }

        private async void OnNewTextClick(object sender, RoutedEventArgs e)
            => await CreateTextLikeAsync("新しいテキスト ドキュメント.txt", "");

        private async void OnNewMarkdownClick(object sender, RoutedEventArgs e)
            => await CreateTextLikeAsync("新しいMarkdown.md", "# タイトル\n");

        private async void OnNewJsonClick(object sender, RoutedEventArgs e)
            => await CreateTextLikeAsync("新しいJSON.json", "{\n}\n");

        private async void OnNewCsvClick(object sender, RoutedEventArgs e)
            => await CreateTextLikeAsync("新しいCSV.csv", "header1,header2\n");

        private async void OnNewZipClick(object sender, RoutedEventArgs e)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var path = GetUniquePath(dir, "新しい圧縮.zip");
            try
            {
                using var zip = System.IO.Compression.ZipFile.Open(
                    path, System.IO.Compression.ZipArchiveMode.Create);
            }
            catch { }
            await RefreshCurrentFolderViewAsync();
        }

        private async void OnNewPngClick(object sender, RoutedEventArgs e)
            => await CreateImageAsync("新しい画像.png", "png");
        private async void OnNewBmpClick(object sender, RoutedEventArgs e)
            => await CreateImageAsync("新しい画像.bmp", "bmp");

        private async Task CreateTextLikeAsync(string defaultName, string initialContent)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var path = GetUniquePath(dir, defaultName);
            try
            {
                await File.WriteAllTextAsync(path, initialContent, System.Text.Encoding.UTF8);
            }
            catch { }
            await RefreshCurrentFolderViewAsync();
        }

        private async Task CreateImageAsync(string defaultName, string kind)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var path = GetUniquePath(dir, defaultName);
            try
            {
                using var bmp = new System.Drawing.Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.White);
                if (kind == "png")
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                else
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Bmp);
            }
            catch { }
            await RefreshCurrentFolderViewAsync();
        }

        private string? GetTargetDirectory()
        {
            var dir = _vm?.CurrentPath;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                WpfMessageBox.Show("左のツリーでフォルダーを選んでください。", "新規作成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return dir;
        }

        private static string GetUniquePath(string dir, string fileName)
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;

            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            int i = 2;
            while (true)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
                i++;
            }
        }
        #endregion
    }
}

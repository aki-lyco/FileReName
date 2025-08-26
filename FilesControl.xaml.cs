// Explore/FilesControl.xaml.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Media;
using Explore.FileSystem;
using Explore.Indexing;
using Microsoft.Data.Sqlite;

// WPF型を明示するためのエイリアス
using WpfMessageBox = System.Windows.MessageBox;
using WpfInput = System.Windows.Input;
using WpfPoint = System.Windows.Point;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDataObject = System.Windows.DataObject;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfBinding = System.Windows.Data.Binding;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;   // ← 追加：WPFのCheckBoxを明示

namespace Explore
{
    // パンくず用モデル / コンバータ（C: → C:\ 正規化対応）
    public record BreadcrumbItem(string Name, string FullPath, bool IsLast);

    public sealed class PathToBreadcrumbConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var raw0 = value as string;
            if (string.IsNullOrWhiteSpace(raw0)) return Array.Empty<BreadcrumbItem>();

            var raw = raw0.Trim();
            if (raw.Length == 2 && raw[1] == ':' && char.IsLetter(raw[0])) raw += "\\";

            string path;
            try { path = Path.GetFullPath(raw); } catch { path = raw; }

            var root = Path.GetPathRoot(path) ?? string.Empty;
            if (root.Length == 2 && root[1] == ':') root += "\\";

            var rootDisplay = root.EndsWith("\\")
                ? (root.TrimEnd('\\').EndsWith(":") ? (root.TrimEnd('\\') + "\\") : root.TrimEnd('\\'))
                : root;

            var items = new List<BreadcrumbItem> { new(rootDisplay, root, false) };

            var remain = path.Length >= root.Length ? path.Substring(root.Length).Trim('\\') : string.Empty;
            if (remain.Length == 0) { items[0] = items[0] with { IsLast = true }; return items.ToArray(); }

            var acc = root;
            var parts = remain.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                acc = Path.Combine(acc, parts[i]);
                items.Add(new BreadcrumbItem(parts[i], acc, i == parts.Length - 1));
            }
            return items;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => WpfBinding.DoNothing;
    }

    public partial class FilesControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        private static class SessionExplorerState
        {
            public static readonly HashSet<string> ExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
            public static string? LastSelectedPath;
        }
        private bool _initialized = false;

        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());
        public ExplorerViewModel VM => _vm;

        private static readonly IndexDatabase _db = new();
        private static readonly FreshnessService _fresh = new(_db);

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
        private void Raise([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        // D&D
        private WpfPoint _dragStart;
        private DependencyObject? _dragOriginVisual;

        public FilesControl()
        {
            InitializeComponent();

            FolderTree.SelectedItemChanged += OnFolderSelected;
            FolderTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnNodeExpanded));
            FolderTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnNodeCollapsed));

            FolderTree.PreviewMouseLeftButtonDown += OnFolderTree_PreviewMouseLeftButtonDown_ForDrag;
            FolderTree.PreviewMouseMove += OnFolderTree_PreviewMouseMove_ForDrag;

            BtnNewCreate.Click += OnNewButtonClick;
            BtnIndex.Click += OnIndexCurrentFolderClick;
            BtnIndexDiff.Click += OnIndexOnlyUnindexedClick;
            BtnRecycle.Click += OnDeleteClick;

            FilesGrid.MouseDoubleClick += OnFilesGridDoubleClick;
            FilesGrid.KeyDown += OnFilesGridKeyDown;

            FilesGrid.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnFilesGridMenuClick));
            FolderTree.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnFolderTreeMenuClick));

            FilesGrid.PreviewMouseLeftButtonDown += OnFilesGrid_PreviewMouseLeftButtonDown_ForDrag;
            FilesGrid.PreviewMouseMove += OnFilesGrid_PreviewMouseMove_ForDrag;

            this.AllowDrop = true;
            this.DragOver += OnRootDragOver;
            this.Drop += OnRootDrop;

            Loaded += async (_, __) =>
            {
                if (_initialized) return;
                _initialized = true;

                DataContext = this;
                await _db.EnsureCreatedAsync();

                if (_vm.Roots.Count == 0)
                    await _vm.LoadRootsAsync();

                if (!await TryRestoreSelectionAndExpansionAsync())
                {
                    if (_vm.Roots.Count > 0)
                    {
                        var first = _vm.Roots[0];
                        await _vm.EnsureChildrenLoadedAsync(first);
                        first.IsExpanded = true;
                        await NavigateAndFillAsync(first.FullPath);
                    }
                }
            };
        }

        // 一覧VM
        public sealed class FileRow : INotifyPropertyChanged
        {
            public string FullPath { get; init; } = "";
            public string Name { get; init; } = "";
            public string Extension { get; init; } = "";
            public long Size { get; init; }
            public DateTime LastWriteTime { get; init; }

            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
            }

            private FreshState _fresh = FreshState.Unindexed;
            public FreshState FreshState
            {
                get => _fresh;
                set { if (_fresh != value) { _fresh = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreshState))); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public ObservableCollection<FileRow> Rows { get; } = new();

        // フォルダ選択/展開
        private async void OnFolderSelected(object? sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not FolderNode node) return;
            SessionExplorerState.LastSelectedPath = node.FullPath;
            await NavigateAndFillAsync(node.FullPath);
        }

        private async void OnNodeExpanded(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
            {
                await _vm.EnsureChildrenLoadedAsync(node);
                if (Directory.Exists(node.FullPath))
                    SessionExplorerState.ExpandedPaths.Add(node.FullPath);
            }
        }

        private void OnNodeCollapsed(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
            {
                SessionExplorerState.ExpandedPaths.Remove(node.FullPath);
            }
        }

        private async Task NavigateAndFillAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            await _vm.NavigateToAsync(path);
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

            _fresh.InvalidateFreshnessCache(path);
            await RefreshRowsFreshnessAsync(path);
            _ = RecalcFreshnessPercentAsync(path);
        }

        // Index 操作
        private async void OnIndexCurrentFolderClick(object? sender, RoutedEventArgs e)
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
                    IndexStatusText = $"登録中 {p.scanned:N0} 件（新規 {p.inserted:N0}）");

                var records = EnumerateRecordsAsync(root!, recursive: false, _indexCts.Token);
                var (_, inserted) = await _db.BulkUpsertAsync(records, batchSize: 500, progress: prog, ct: _indexCts.Token);

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

        private async void OnIndexOnlyUnindexedClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var root = _vm?.CurrentPath;
                if (string.IsNullOrWhiteSpace(root)) return;

                int done = 0;
                foreach (var r in Rows)
                {
                    if (r.FreshState != FreshState.Unindexed) continue;
                    try { await UpsertFileFromFsAsync(r.FullPath, CancellationToken.None); done++; }
                    catch { }
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

        // DataGrid：開く
        private void OnFilesGridDoubleClick(object? sender, WpfInput.MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow && dep is not DataGrid)
                dep = VisualTreeHelper.GetParent(dep);

            if (FilesGrid?.SelectedItem is FileRow fr)
            {
                OpenFile(fr.FullPath);
                e.Handled = true;
            }
        }

        private void OnFilesGridKeyDown(object? sender, WpfInput.KeyEventArgs e)
        {
            if (e.Key == WpfInput.Key.Space)
            {
                // Space で選択行のチェック反転
                foreach (var r in FilesGrid.SelectedItems.OfType<FileRow>())
                    r.IsChecked = !r.IsChecked;
                e.Handled = true;
                return;
            }

            if (e.Key == WpfInput.Key.Enter && FilesGrid?.SelectedItem is FileRow fr)
            {
                OpenFile(fr.FullPath);
                e.Handled = true;
            }
        }

        // 行メニュー（Tagで振り分け）
        private void OnFilesGridMenuClick(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not MenuItem mi) return;
            var tag = mi.Tag as string ?? "";
            var path = (mi.CommandParameter as string)
                       ?? (mi.DataContext as FileRow)?.FullPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            switch (tag)
            {
                case "OpenFile": OpenFile(path); break;
                case "DeleteFile": _ = DeleteFilesAsync(new[] { path }); break;
                case "ReindexFile": _ = ReindexOneAsync(path); break;
                case "DeleteDbRecord": _ = DeleteDbRecordAsync(path); break;
                case "OpenLocation": OpenLocation(path); break;
            }
        }

        // ツリーメニュー
        private void OnFolderTreeMenuClick(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not MenuItem mi) return;
            var tag = mi.Tag as string ?? "";
            if (tag != "DeleteFolder") return;

            var path = mi.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            _ = DeleteFolderAsync(path);
        }

        // ヘッダーの「全選択/全解除」
        private void OnHeaderCheckAllClick(object sender, RoutedEventArgs e)
        {
            if (sender is WpfCheckBox cb)   // ← ここでWPFのCheckBoxを使用
            {
                bool v = cb.IsChecked == true;
                foreach (var r in Rows) r.IsChecked = v;
            }
        }

        // 操作実体
        private void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    WpfMessageBox.Show($"ファイルが存在しません。\n{path}", "開く", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "開くエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLocation(string path)
        {
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

        private async Task ReindexOneAsync(string path)
        {
            try
            {
                await UpsertFileFromFsAsync(path, CancellationToken.None);
                var dir = Path.GetDirectoryName(path) ?? _vm.CurrentPath ?? "";
                _fresh.InvalidateFreshnessCache(dir);
                await RefreshRowsFreshnessAsync(dir);
                _ = RecalcFreshnessPercentAsync(dir);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "ReIndex Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeleteDbRecordAsync(string path)
        {
            try
            {
                await DeleteByPathAsync(path, CancellationToken.None);
                var dir = Path.GetDirectoryName(path) ?? _vm.CurrentPath ?? "";
                _fresh.InvalidateFreshnessCache(dir);
                await RefreshRowsFreshnessAsync(dir);
                _ = RecalcFreshnessPercentAsync(dir);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "DB Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ツールバー：チェック優先でゴミ箱へ
        private async void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            var checkedPaths = Rows.Where(r => r.IsChecked).Select(r => r.FullPath).ToList();
            if (checkedPaths.Count == 0)
                checkedPaths = FilesGrid?.SelectedItems?.OfType<FileRow>().Select(r => r.FullPath).ToList() ?? new();

            if (checkedPaths.Count > 0) { await DeleteFilesAsync(checkedPaths); return; }

            if (FolderTree?.SelectedItem is FolderNode node && !string.IsNullOrWhiteSpace(node.FullPath))
            {
                await DeleteFolderAsync(node.FullPath);
                return;
            }

            WpfMessageBox.Show("削除するファイルまたはフォルダーを選択してください。", "削除", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task DeleteFilesAsync(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            if (!ConfirmDelete("選択したファイルをゴミ箱へ移動", list)) return;

            await DeleteToRecycleBinAsync(list);
            await RefreshCurrentFolderViewAsync();
        }

        private async Task DeleteFolderAsync(string path)
        {
            if (!ConfirmDelete("フォルダーをゴミ箱へ移動", new[] { path })) return;

            var parent = Path.GetDirectoryName(path);

            await DeleteToRecycleBinAsync(new[] { path });

            try { await DeleteRecordsUnderAsync(path, CancellationToken.None); } catch { }

            if (!string.IsNullOrWhiteSpace(parent))
                await ForceRefreshFolderNodeAsync(parent!);

            if (!string.IsNullOrWhiteSpace(_vm?.CurrentPath) && IsSubPath(path, _vm.CurrentPath!))
                await NavigateAndFillAsync(parent ?? "");
            else
                await RefreshCurrentFolderViewAsync();
        }

        private static bool ConfirmDelete(string title, IEnumerable<string> paths)
        {
            var list = paths.ToList();
            var first10 = list.Take(10).Select(p => "・" + Path.GetFileName(p));
            var more = list.Count - 10;
            var msg = "以下をWindowsのごみ箱へ移動します。よろしいですか？\n\n"
                      + string.Join("\n", first10)
                      + (more > 0 ? $"\n…ほか {more} 件" : "")
                      + "\n\n※ ごみ箱から復元できます。";
            return WpfMessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static Task DeleteToRecycleBinAsync(IEnumerable<string> paths)
        {
            return Task.Run(() =>
            {
                foreach (var p in paths)
                {
                    try
                    {
                        if (File.Exists(p))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                p,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                        else if (Directory.Exists(p))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                p,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                    }
                    catch { }
                }
            });
        }

        private async Task DeleteRecordsUnderAsync(string dir, CancellationToken ct)
        {
            static string EscapeLike(string s) => s
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

            var prefix = EscapeLike(
                Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                + Path.DirectorySeparatorChar);

            await using var conn = OpenConn();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path LIKE $p || '%' ESCAPE '\\'";
            cmd.Parameters.AddWithValue("$p", prefix);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ユーティリティ
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
                        Classified = null
                    };
                }
                catch { }

                if (rec != null) yield return rec;
                if ((uint)Environment.TickCount % 2048 == 0) await Task.Yield();
            }
        }

        private async Task<long> GetTableCountAsync(string table)
        {
            await using var conn = OpenConn();
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
            catch (OperationCanceledException) { }
        }

        private async Task RecalcFreshnessPercentAsync(string scopePath)
        {
            try
            {
                var pct = await _fresh.CalcFreshnessPercentAsync(scopePath, CancellationToken.None);
                FreshnessPercent = Math.Clamp(pct * 100.0, 0.0, 100.0);
            }
            catch { }
        }

        private async Task RefreshCurrentFolderViewAsync()
        {
            var root = _vm?.CurrentPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            await NavigateAndFillAsync(root);
        }

        #region 新規作成メニュー
        private void OnNewButtonClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            var cm = new ContextMenu();
            static MenuItem MI(string header, RoutedEventHandler h) { var m = new MenuItem { Header = header }; m.Click += h; return m; }

            cm.Items.Add(MI("フォルダー", OnNewFolderClick));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("テキスト ドキュメント (.txt)", OnNewTextClick));
            cm.Items.Add(MI("Markdown (.md)", OnNewMarkdownClick));
            cm.Items.Add(MI("JSON (.json)", OnNewJsonClick));
            cm.Items.Add(MI("CSV (.csv)", OnNewCsvClick));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("空の画像 (.png)", OnNewPngClick));
            cm.Items.Add(MI("Bitmap 画像 (.bmp)", OnNewBmpClick));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("圧縮 (zip) フォルダー", OnNewZipClick));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("既存ファイルをコピーして追加...", OnImportFilesClick));

            cm.PlacementTarget = fe;
            cm.IsOpen = true;
        }

        private async void OnImportFilesClick(object? sender, RoutedEventArgs e)
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

        private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;

            var baseName = "新しいフォルダー";
            var name = baseName;
            int i = 2;
            while (Directory.Exists(Path.Combine(dir, name))) name = $"{baseName} ({i++})";

            var created = Path.Combine(dir, name);
            Directory.CreateDirectory(created);

            await ForceRefreshFolderNodeAsync(dir);
            await TryExpandPathAsync(created);
            await NavigateAndFillAsync(created);
        }

        private async void OnNewTextClick(object? sender, RoutedEventArgs e) => await CreateTextLikeAsync("新しいテキスト ドキュメント.txt", "");
        private async void OnNewMarkdownClick(object? sender, RoutedEventArgs e) => await CreateTextLikeAsync("新しいMarkdown.md", "# タイトル\n");
        private async void OnNewJsonClick(object? sender, RoutedEventArgs e) => await CreateTextLikeAsync("新しいJSON.json", "{\n}\n");
        private async void OnNewCsvClick(object? sender, RoutedEventArgs e) => await CreateTextLikeAsync("新しいCSV.csv", "header1,header2\n");

        private async void OnNewZipClick(object? sender, RoutedEventArgs e)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var path = GetUniquePath(dir, "新しい圧縮.zip");
            try { using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create); } catch { }
            await RefreshCurrentFolderViewAsync();
        }

        private async void OnNewPngClick(object? sender, RoutedEventArgs e) => await CreateImageAsync("新しい画像.png", "png");
        private async void OnNewBmpClick(object? sender, RoutedEventArgs e) => await CreateImageAsync("新しい画像.bmp", "bmp");

        private async Task CreateTextLikeAsync(string defaultName, string initialContent)
        {
            var dir = GetTargetDirectory();
            if (dir == null) return;
            var path = GetUniquePath(dir, defaultName);
            try { await File.WriteAllTextAsync(path, initialContent, System.Text.Encoding.UTF8); } catch { }
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
                if (kind == "png") bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                else bmp.Save(path, System.Drawing.Imaging.ImageFormat.Bmp);
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
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
                i++;
            }
        }
        #endregion

        // 展開・選択状態の復元
        private async Task<bool> TryRestoreSelectionAndExpansionAsync()
        {
            bool didSomething = false;

            var expanded = SessionExplorerState.ExpandedPaths
                .Where(Directory.Exists)
                .OrderBy(p => p.Length)
                .ToArray();

            foreach (var p in expanded)
                if (await TryExpandPathAsync(p)) didSomething = true;

            var last = SessionExplorerState.LastSelectedPath;
            if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
            {
                await TryExpandPathAsync(last);
                await NavigateAndFillAsync(last);
                didSomething = true;
            }

            return didSomething;
        }

        private async Task<bool> TryExpandPathAsync(string path)
        {
            try
            {
                var rootPath = Path.GetPathRoot(path) ?? "";
                var root = _vm.Roots.FirstOrDefault(r =>
                    string.Equals(r.FullPath, rootPath, StringComparison.OrdinalIgnoreCase));
                if (root == null) return false;

                await _vm.EnsureChildrenLoadedAsync(root);
                root.IsExpanded = true;

                var remain = path.Substring(rootPath.Length).Trim('\\');
                if (remain.Length == 0) return true;

                var parts = remain.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .Where(s => !string.IsNullOrWhiteSpace(s));

                var current = root;
                foreach (var part in parts)
                {
                    var nextFull = Path.Combine(current.FullPath, part);
                    var next = current.Children.FirstOrDefault(c =>
                        string.Equals(c.FullPath, nextFull, StringComparison.OrdinalIgnoreCase));
                    if (next == null)
                    {
                        await _vm.EnsureChildrenLoadedAsync(current);
                        next = current.Children.FirstOrDefault(c =>
                            string.Equals(c.FullPath, nextFull, StringComparison.OrdinalIgnoreCase));
                        if (next == null) return false;
                    }

                    await _vm.EnsureChildrenLoadedAsync(next);
                    next.IsExpanded = true;
                    current = next;
                }

                return true;
            }
            catch { return false; }
        }

        // ========== D&D での移動 ==========
        private void OnFilesGrid_PreviewMouseLeftButtonDown_ForDrag(object? sender, WpfInput.MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(this);
            _dragOriginVisual = e.OriginalSource as DependencyObject;
        }

        private void OnFilesGrid_PreviewMouseMove_ForDrag(object? sender, WpfInput.MouseEventArgs e)
        {
            if (e.LeftButton != WpfInput.MouseButtonState.Pressed || _dragOriginVisual == null) return;

            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _dragStart.X) < 4 && Math.Abs(now.Y - _dragStart.Y) < 4) return;

            var selected = FilesGrid?.SelectedItems?.OfType<FileRow>().Select(r => r.FullPath).ToArray();
            if (selected == null || selected.Length == 0)
            {
                var row = FindDataContext<FileRow>(_dragOriginVisual);
                if (row != null) selected = new[] { row.FullPath };
            }
            if (selected == null || selected.Length == 0) return;

            var data = new WpfDataObject();
            data.SetData(WpfDataFormats.FileDrop, selected);
            try { WpfDragDrop.DoDragDrop(this, data, WpfDragDropEffects.Move); }
            catch { }
            finally { _dragOriginVisual = null; }
        }

        private void OnFolderTree_PreviewMouseLeftButtonDown_ForDrag(object? sender, WpfInput.MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(this);
            _dragOriginVisual = e.OriginalSource as DependencyObject;
        }

        private void OnFolderTree_PreviewMouseMove_ForDrag(object? sender, WpfInput.MouseEventArgs e)
        {
            if (e.LeftButton != WpfInput.MouseButtonState.Pressed || _dragOriginVisual == null) return;

            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _dragStart.X) < 4 && Math.Abs(now.Y - _dragStart.Y) < 4) return;

            var node = FindDataContext<FolderNode>(_dragOriginVisual);
            if (node == null || string.IsNullOrWhiteSpace(node.FullPath) || !Directory.Exists(node.FullPath)) return;

            if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(node.FullPath))) return;

            var data = new WpfDataObject();
            data.SetData(WpfDataFormats.FileDrop, new[] { node.FullPath });
            try { WpfDragDrop.DoDragDrop(this, data, WpfDragDropEffects.Move); }
            catch { }
            finally { _dragOriginVisual = null; }
        }

        private void OnRootDragOver(object? sender, WpfDragEventArgs e)
        {
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                e.Effects = WpfDragDropEffects.None; e.Handled = true; return;
            }

            string? destDir = HitFolderNode(e.GetPosition(this))?.FullPath ?? _vm.CurrentPath;
            if (string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(destDir))
            {
                e.Effects = WpfDragDropEffects.None; e.Handled = true; return;
            }

            var paths = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p) && IsSubPath(p, destDir))
                    {
                        e.Effects = WpfDragDropEffects.None; e.Handled = true; return;
                    }
                }
            }

            e.Effects = WpfDragDropEffects.Move; e.Handled = true;
        }

        private async void OnRootDrop(object? sender, WpfDragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
                var paths = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
                if (paths == null || paths.Length == 0) return;

                string? destDir = HitFolderNode(e.GetPosition(this))?.FullPath;
                if (string.IsNullOrWhiteSpace(destDir)) destDir = _vm.CurrentPath;
                if (string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(destDir))
                {
                    WpfMessageBox.Show("移動先フォルダーが見つかりませんでした。"); return;
                }

                await MoveAnyAsync(paths, destDir!);
                await _vm.RefreshAsync();
                await RefreshTreeAfterMoveAsync(paths, destDir!);

                var srcParents = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
                                      .Where(d => !string.IsNullOrWhiteSpace(d))
                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var d in srcParents) _fresh.InvalidateFreshnessCache(d!);
                _fresh.InvalidateFreshnessCache(destDir!);
                await RefreshCurrentFolderViewAsync();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RefreshTreeAfterMoveAsync(IEnumerable<string> sources, string destDir)
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var src in sources)
            {
                var p = Path.GetDirectoryName(src?.TrimEnd(Path.DirectorySeparatorChar) ?? "");
                if (!string.IsNullOrWhiteSpace(p)) parents.Add(p);
            }
            parents.Add(destDir);

            foreach (var parent in parents)
                await ForceRefreshFolderNodeAsync(parent);
        }

        private async Task ForceRefreshFolderNodeAsync(string folderPath)
        {
            var node = await GetNodeByPathAsync(folderPath);
            if (node == null) return;

            var list = new List<string>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(folderPath))
                {
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        if ((di.Attributes & FileAttributes.Hidden) != 0) continue;
                        if ((di.Attributes & FileAttributes.System) != 0) continue;
                        list.Add(dir);
                    }
                    catch { }
                }
            }
            catch { }

            node.ReplaceChildren(list.Select(FolderNode.Create));
            node.IsExpanded = true;
            node._isLoaded = true;
        }

        private async Task<FolderNode?> GetNodeByPathAsync(string path)
        {
            await TryExpandPathAsync(path);
            return FindNodeInTree(path);
        }
        private FolderNode? FindNodeInTree(string path)
        {
            foreach (var r in _vm.Roots)
            {
                var found = FindNodeRecursive(r, path);
                if (found != null) return found;
            }
            return null;
        }
        private static FolderNode? FindNodeRecursive(FolderNode node, string targetPath)
        {
            if (string.Equals(node.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (var c in node.Children)
            {
                var f = FindNodeRecursive(c, targetPath);
                if (f != null) return f;
            }
            return null;
        }

        private async Task MoveAnyAsync(IEnumerable<string> sources, string destDir)
        {
            var errors = new List<string>();
            Directory.CreateDirectory(destDir);

            foreach (var src in sources)
            {
                try
                {
                    if (File.Exists(src)) await MoveOneFileAsync(src, destDir);
                    else if (Directory.Exists(src)) await MoveOneDirectoryAsync(src, destDir);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar))} : {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                WpfMessageBox.Show("一部移動に失敗しました。\n\n" + string.Join("\n", errors.Take(10)) +
                    (errors.Count > 10 ? $"\n…ほか {errors.Count - 10} 件" : ""),
                    "Move", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task MoveOneFileAsync(string src, string destDir)
        {
            var name = Path.GetFileName(src);
            var destPath = Path.Combine(destDir, name);
            if (string.Equals(src, destPath, StringComparison.OrdinalIgnoreCase)) return;

            destPath = EnsureUniqueFilePath(destPath);

            var oldKey = FileKeyUtil.GetStableKey(src);
            SafeFileMove(src, destPath);

            var rec = BuildRecordFromFs(destPath);
            await _db.UpsertFileAsync(rec);

            var newKey = rec.FileKey;
            if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
                await DeleteByFileKeyAsync(oldKey, CancellationToken.None);

            await LogMoveAsync(oldKey, src, destPath, CancellationToken.None);
        }

        private async Task MoveOneDirectoryAsync(string srcDir, string destParent)
        {
            if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(srcDir)))
                throw new InvalidOperationException("このフォルダーは移動できません。");

            var finalDest = Path.Combine(destParent, Path.GetFileName(srcDir.TrimEnd(Path.DirectorySeparatorChar)));
            finalDest = EnsureUniqueDirectoryPath(finalDest);

            var oldFiles = EnumerateAllFiles(srcDir).ToList();
            var mapping = new List<(string oldPath, string newPath, string oldKey)>(oldFiles.Count);
            foreach (var oldPath in oldFiles)
            {
                var rel = Path.GetRelativePath(srcDir, oldPath);
                var newPath = Path.Combine(finalDest, rel);
                mapping.Add((oldPath, newPath, FileKeyUtil.GetStableKey(oldPath)));
            }

            var sameVolume = string.Equals(Path.GetPathRoot(srcDir), Path.GetPathRoot(destParent), StringComparison.OrdinalIgnoreCase);
            if (sameVolume) Directory.Move(srcDir, finalDest);
            else
            {
                await CopyDirectoryAsync(srcDir, finalDest);
                try { Directory.Delete(srcDir, recursive: true); }
                catch (Exception ex) { WpfMessageBox.Show($"元フォルダの削除に失敗: {ex.Message}", "フォルダ移動", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }

            foreach (var (oldPath, newPath, oldKey) in mapping)
            {
                if (!File.Exists(newPath)) continue;

                var rec = BuildRecordFromFs(newPath);
                await _db.UpsertFileAsync(rec);

                if (!string.Equals(oldKey, rec.FileKey, StringComparison.Ordinal))
                    await DeleteByFileKeyAsync(oldKey, CancellationToken.None);

                await LogMoveAsync(oldKey, oldPath, newPath, CancellationToken.None);
            }
        }

        private static string EnsureUniqueFilePath(string destPath)
        {
            if (!File.Exists(destPath)) return destPath;
            var dir = Path.GetDirectoryName(destPath)!;
            var baseName = Path.GetFileNameWithoutExtension(destPath);
            var ext = Path.GetExtension(destPath);
            for (int i = 1; ; i++)
            {
                var cand = Path.Combine(dir, $"{baseName} ({i}){ext}");
                if (!File.Exists(cand)) return cand;
            }
        }

        private static string EnsureUniqueDirectoryPath(string destPath)
        {
            if (!Directory.Exists(destPath)) return destPath;
            var dir = Path.GetDirectoryName(destPath)!;
            var name = Path.GetFileName(destPath.TrimEnd(Path.DirectorySeparatorChar));
            for (int i = 1; ; i++)
            {
                var cand = Path.Combine(dir, $"{name} ({i})");
                if (!Directory.Exists(cand)) return cand;
            }
        }

        private static void SafeFileMove(string src, string dest)
        {
            var sameVolume = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase);
            if (sameVolume) File.Move(src, dest);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: false);
                File.Delete(src);
            }
        }

        private static IEnumerable<string> EnumerateAllFiles(string root)
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var f in Directory.EnumerateFiles(root, "*", opts)) yield return f;
        }

        private static Task CopyDirectoryAsync(string srcDir, string destDir)
        {
            return Task.Run(() =>
            {
                var stack = new Stack<(string src, string dst)>();
                stack.Push((srcDir, destDir));

                while (stack.Count > 0)
                {
                    var (s, d) = stack.Pop();
                    Directory.CreateDirectory(d);

                    foreach (var file in Directory.GetFiles(s))
                    {
                        var dstFile = Path.Combine(d, Path.GetFileName(file));
                        File.Copy(file, dstFile, overwrite: false);
                    }
                    foreach (var sub in Directory.GetDirectories(s))
                        stack.Push((sub, Path.Combine(d, Path.GetFileName(sub))));
                }
            });
        }

        private static bool IsSubPath(string parent, string candidate)
        {
            static string Norm(string p) => Path.GetFullPath(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + Path.DirectorySeparatorChar;
            var p0 = Norm(parent);
            var c0 = Norm(candidate);
            return c0.StartsWith(p0, StringComparison.OrdinalIgnoreCase);
        }

        // DBユーティリティ
        private static SqliteConnection OpenConn()
            => new(new SqliteConnectionStringBuilder { DataSource = IndexDatabase.DatabasePath }.ToString());

        private static DbFileRecord BuildRecordFromFs(string path)
        {
            var fi = new FileInfo(path);
            return new DbFileRecord
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
                Classified = null
            };
        }

        private async Task UpsertFileFromFsAsync(string path, CancellationToken ct)
        {
            if (!File.Exists(path)) return;
            var rec = BuildRecordFromFs(path);
            await _db.UpsertFileAsync(rec);
        }

        private async Task DeleteByFileKeyAsync(string fileKey, CancellationToken ct)
        {
            await using var conn = OpenConn();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE file_key=$k";
            cmd.Parameters.AddWithValue("$k", fileKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task DeleteByPathAsync(string path, CancellationToken ct)
        {
            await using var conn = OpenConn();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task LogMoveAsync(string fileKey, string oldPath, string newPath, CancellationToken ct)
        {
            await using var conn = OpenConn();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO moves(file_key, old_path, new_path, op, reason, at)
VALUES($k,$o,$n,'move',NULL,$t)";
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$o", oldPath);
            cmd.Parameters.AddWithValue("$n", newPath);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // パンくずクリック
        private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton b && b.Tag is string p && Directory.Exists(p))
                await NavigateAndFillAsync(p);
        }

        // 汎用
        private FolderNode? HitFolderNode(WpfPoint p)
        {
            var hit = this.InputHitTest(p) as DependencyObject;
            return FindDataContext<FolderNode>(hit);
        }

        private static T? FindDataContext<T>(DependencyObject? start) where T : class
        {
            while (start != null)
            {
                if (start is FrameworkElement fe && fe.DataContext is T t) return t;
                start = VisualTreeHelper.GetParent(start);
            }
            return null;
        }
    }
}

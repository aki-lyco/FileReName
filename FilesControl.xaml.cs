// Explore/FilesControl.xaml.cs
// 左：フォルダツリー / 右：ファイル一覧。DBインデックスは手動時は進捗バーを更新（scanned/total）。
// 自動インデックス（フォルダ選択時ON）は静かに実行してUIを塞がない。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Explore.FileSystem;
using Explore.Indexing;
using Microsoft.Data.Sqlite;
using Explore.UI;
using WpfMessageBox = System.Windows.MessageBox;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Explore
{
    public partial class FilesControl : WpfUserControl, INotifyPropertyChanged
    {
        // ---- VM（ExplorerBackend） ----
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());
        public ExplorerViewModel VM => _vm;

        // ---- DB ----
        private static readonly IndexDatabase _db = new();

        // ---- 設定（歯車） ----
        private bool _includeSubs;
        private bool _autoIndex;

        // ---- インデックス処理のキャンセル用 ----
        private CancellationTokenSource? _indexCts;

        public FilesControl()
        {
            InitializeComponent();

            Loaded += async (_, __) =>
            {
                DataContext = this;

                // 設定を反映
                _autoIndex = UiSettings.Instance.AutoIndexOnSelect;
                _includeSubs = UiSettings.Instance.IncludeSubfolders;
                UiSettings.Instance.Changed += (_, __) =>
                {
                    _autoIndex = UiSettings.Instance.AutoIndexOnSelect;
                    _includeSubs = UiSettings.Instance.IncludeSubfolders;
                };

                try
                {
                    await _db.EnsureCreatedAsync();
                    await _vm.LoadRootsAsync(); // ルート（ドライブ等）をロード
                    await RefreshDbCountAsync();
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show($"初期化に失敗しました: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            Unloaded += (_, __) => _indexCts?.Cancel();
        }

        // ===== バインディング用プロパティ =====
        private bool _isIndexing;
        public bool IsIndexing { get => _isIndexing; private set { _isIndexing = value; OnPropertyChanged(); } }

        private double _indexPercent;
        public double IndexPercent { get => _indexPercent; private set { _indexPercent = value; OnPropertyChanged(); } }

        private string _indexStatusText = "待機中";
        public string IndexStatusText { get => _indexStatusText; private set { _indexStatusText = value; OnPropertyChanged(); } }

        private string _indexedCountText = "";
        public string IndexedCountText { get => _indexedCountText; private set { _indexedCountText = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ===== ツリー操作 =====

        // ノード展開時：子フォルダを遅延ロード
        private async void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
            {
                try { await _vm.EnsureChildrenLoadedAsync(node); }
                catch { /* アクセス拒否などは握りつぶす */ }
            }
        }

        // 選択変更：右ペインにファイル一覧を表示（VMが埋める）
        private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not FolderNode node) return;

            try { await _vm.NavigateToAsync(node.FullPath); }
            catch { /* 握りつぶし */ }

            // 自動インデックスがONなら、静かにDB投入（ゲージは出さない）
            // 自動インデックスがONなら、600msディレイ後に軽量スキャン（非再帰・フィルタ・上限付き）
            if (_autoIndex)
            {
                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();
                var ct = _indexCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(600, ct); // 連打対策
                        await _db.EnsureCreatedAsync();

                        var root = node.FullPath;
                        if (AutoIndexPolicy.ShouldSkipPath(root)) return;

                        // 非再帰・拡張子/サイズ/システム除外・上限N件
                        async IAsyncEnumerable<DbFileRecord> EnumerateAuto([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
                        {
                            int count = 0;
                            IEnumerable<string> files;
                            try
                            {
                                files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly);
                            }
                            catch { yield break; }

                            foreach (var path in files)
                            {
                                token.ThrowIfCancellationRequested();
                                if (AutoIndexPolicy.ShouldSkipPath(path)) continue;

                                FileInfo fi;
                                try { fi = new FileInfo(path); }
                                catch { continue; }

                                if (!AutoIndexPolicy.ShouldIndexFile(fi)) continue;

                                yield return new DbFileRecord
                                {
                                    Path = fi.FullName,
                                    FileKey = FileKeyUtil.GetStableKey(fi.FullName),
                                    Parent = fi.DirectoryName,
                                    Name = fi.Name,
                                    Ext = fi.Extension,
                                    Size = fi.Length,
                                    MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                                    CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                                    Mime = null,
                                    Summary = null,
                                    Snippet = null,
                                    Classified = null
                                };

                                if (++count >= AutoIndexPolicy.MaxFilesPerAutoIndex) yield break;
                                if ((count & 0x3FF) == 0) await Task.Yield(); // UIに譲る
                            }
                        }

                        await _db.BulkUpsertAsync(EnumerateAuto(ct), batchSize: 500, progress: null, ct: ct);

                        // カウント更新はUIスレッドへ
                        await Dispatcher.InvokeAsync(async () => await RefreshDbCountAsync());
                    }
                    catch (OperationCanceledException) { /* 直近選択へ切替 */ }
                    catch { /* 自動は静かに失敗でOK */ }
                    finally
                    {
                        _indexCts?.Dispose();
                        _indexCts = null;
                    }
                }, ct);
            }

        }

        // ===== 手動インデックス（メニュー等から呼ぶ想定。XAMLボタンは削除済み） =====
        private async void OnIndexCurrentFolderClick(object sender, RoutedEventArgs e)
        {
            if (_indexCts != null) return; // 多重起動防止
            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;

            try
            {
                var targets = GetSelectedFolderPaths();
                if (targets.Count == 0)
                    targets = GetDefaultHotRoots();

                await _db.EnsureCreatedAsync();

                // ① 総数を事前カウント（別スレッド）
                IndexStatusText = "ファイル数を集計中…";
                IndexPercent = 0;
                var includeSubs = _includeSubs; // ローカルに固定
                long total = await Task.Run(() => CountFiles(targets, includeSubs, ct), ct);

                // ② 本処理：レコード列挙
                IsIndexing = true;
                IndexStatusText = $"登録開始（対象 {total:N0} 件）";
                IndexPercent = 0;

                var records = EnumerateRecordsAsync(targets, includeSubs, ct);

                // ③ 進捗（scanned / total）
                var prog = new Progress<(long scanned, long inserted)>(p =>
                {
                    double percent = (total <= 0) ? 0 : Math.Min(100.0, p.scanned * 100.0 / total);
                    IndexPercent = percent;
                    IndexStatusText = $"登録中 {p.scanned:N0} / {total:N0}";
                });

                var (scanned, inserted) = await _db.BulkUpsertAsync(records, batchSize: 200, progress: prog, ct: ct);

                await RefreshDbCountAsync();

                IndexStatusText = "完了";
                IndexPercent = 100;

                // 完了ダイアログがうるさければコメントアウトのままでOK
                // WpfMessageBox.Show(
                //     $"スキャン: {scanned:N0} 件\n新規: {inserted:N0} 件\nDB: {IndexDatabase.DatabasePath}",
                //     "Index 完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                IndexStatusText = "キャンセルしました";
            }
            catch (Exception ex)
            {
                IndexStatusText = "エラー";
                WpfMessageBox.Show(ex.Message, "Index Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsIndexing = false;
                _indexCts?.Dispose();
                _indexCts = null;
            }
        }

        // 任意（もし残っていればメニュー等からキャンセル可能）
        private void OnCancelIndexClick(object sender, RoutedEventArgs e)
        {
            _indexCts?.Cancel();
        }

        // ===== 列挙（Hidden/System を除外） =====
        private async IAsyncEnumerable<DbFileRecord> EnumerateRecordsAsync(
            List<string> targets, bool includeSubs, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); // UI ブロック回避

            var so = includeSubs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (var root in targets)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", so); }
                catch { continue; }

                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();

                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(path);
                        if (!fi.Exists) continue;

                        var attr = fi.Attributes;
                        if ((attr & FileAttributes.Hidden) != 0) continue;
                        if ((attr & FileAttributes.System) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    yield return new DbFileRecord
                    {
                        Path = fi.FullName,
                        FileKey = FileKeyUtil.GetStableKey(fi.FullName),
                        Parent = fi.DirectoryName,
                        Name = fi.Name,
                        Ext = fi.Extension,
                        Size = fi.Length,
                        MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                        CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                        Mime = null,
                        Summary = null,
                        Snippet = null,
                        Classified = null
                    };
                }
            }
        }

        // ===== 進捗表示用：総数カウント =====
        private static long CountFiles(List<string> roots, bool includeSubs, CancellationToken ct)
        {
            long total = 0;
            var so = includeSubs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", so); }
                catch { continue; }

                foreach (var path in files)
                {
                    if (ct.IsCancellationRequested) return total;
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.Hidden) != 0) continue;
                        if ((attr & FileAttributes.System) != 0) continue;
                        total++;
                    }
                    catch { /* ignore */ }
                }
            }
            return total;
        }

        // ===== ヘルパー =====
        private List<string> GetSelectedFolderPaths()
        {
            var list = new List<string>();
            if (TreeViewRoot?.SelectedItem is FolderNode node && Directory.Exists(node.FullPath))
                list.Add(node.FullPath);
            return list;
        }

        private static List<string> GetDefaultHotRoots()
        {
            var result = new List<string>();
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            void add(string p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) result.Add(p); }

            add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            add(Path.Combine(user, "Downloads"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            var oneDrive = Path.Combine(user, "OneDrive");
            add(oneDrive);
            return result;
        }

        private async Task RefreshDbCountAsync()
        {
            try
            {
                IndexedCountText = $"DB件数: {(await GetTableCountAsync("files")).ToString("N0")}";
            }
            catch
            {
                IndexedCountText = "DB件数: -";
            }
        }

        private static async Task<long> GetTableCountAsync(string table)
        {
            await using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = IndexDatabase.DatabasePath }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        }

        // ===== INotifyPropertyChanged 実装は上に記載 =====
    }
}

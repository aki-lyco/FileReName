// Explore/FilesControl.xaml.cs
// ExplorerBackend（Explore.FileSystem）に沿って、遅延ロードのツリー＋右側のファイル一覧を表示。
// DB登録は選択フォルダ（未選択時は既知フォルダ群）を対象に実行。UI応答性重視で IAsyncEnumerable で供給。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Explore.FileSystem;
using Explore.Indexing;
using Microsoft.Data.Sqlite; // DB件数表示用
using WpfMessageBox = System.Windows.MessageBox; // ← WPFのMessageBoxを明示

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        // ---- VM（ExplorerBackend） ----
        public ExplorerViewModel Vm { get; } = new ExplorerViewModel(new DefaultFileSystemService());

        // ---- DB ----
        private readonly IndexDatabase _db = new();

        // ---- トグル状態 ----
        private bool _includeSubs;
        private bool _autoIndex;

        // ---- インデックス処理のキャンセル用 ----
        private CancellationTokenSource? _indexCts;

        public FilesControl()
        {
            InitializeComponent();
            DataContext = this; // この UserControl を DataContext に。XAML は Vm.* にバインド。

            Loaded += async (_, __) =>
            {
                try
                {
                    await _db.EnsureCreatedAsync();
                    await Vm.LoadRootsAsync(); // ルート（ドライブ等）をロード
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
        private double _indexPercent;
        public double IndexPercent { get => _indexPercent; set { _indexPercent = value; OnPropertyChanged(); } }

        private string _indexStatusText = "";
        public string IndexStatusText { get => _indexStatusText; set { _indexStatusText = value; OnPropertyChanged(); } }

        private string _indexedCountText = "0";
        public string IndexedCountText { get => _indexedCountText; set { _indexedCountText = value; OnPropertyChanged(); } }

        // ===== ツリー操作 =====

        // ノード展開時：子フォルダを遅延ロード
        private async void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
            {
                try { await Vm.EnsureChildrenLoadedAsync(node); }
                catch { /* アクセス拒否などは握りつぶす */ }
            }
        }

        // 選択変更：右ペインにファイル一覧を表示（VMが埋める）
        private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderNode node)
            {
                try { await Vm.NavigateToAsync(node.FullPath); }
                catch { /* 握りつぶし */ }

                // 自動インデックスがONなら開始
                if (_autoIndex)
                {
                    OnIndexCurrentFolderClick(this, new RoutedEventArgs());
                }
            }
        }

        // ===== トグル（XAMLのハンドラに対応） =====
        private void IncludeSubs_Checked(object sender, RoutedEventArgs e) => _includeSubs = true;
        private void IncludeSubs_Unchecked(object sender, RoutedEventArgs e) => _includeSubs = false;

        private void AutoIndex_Checked(object sender, RoutedEventArgs e) => _autoIndex = true;
        private void AutoIndex_Unchecked(object sender, RoutedEventArgs e) => _autoIndex = false;

        // ===== 手動インデックス（選択フォルダ優先、未選択時は既知フォルダ） =====
        private async void OnIndexCurrentFolderClick(object sender, RoutedEventArgs e)
        {
            if (_indexCts != null) return; // 多重起動防止

            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;

            try
            {
                IndexStatusText = "収集中...";
                IndexPercent = 0;

                var targets = GetSelectedFolderPaths();
                if (targets.Count == 0)
                    targets = GetDefaultHotRoots(); // 未選択時のフォールバック

                // ファイル列挙（Hidden/System 除外）
                async IAsyncEnumerable<DbFileRecord> EnumerateAsync()
                {
                    await Task.Yield(); // UI ブロック回避

                    var so = _includeSubs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    foreach (var root in targets)
                    {
                        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                            continue;

                        IEnumerable<string> files;
                        try
                        {
                            files = Directory.EnumerateFiles(root, "*", so);
                        }
                        catch
                        {
                            continue; // アクセス拒否などはスキップ
                        }

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
                                FileKey = FileKeyUtil.GetStableKey(fi.FullName), // ← 修正
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

                var progress = new Progress<(long scanned, long inserted)>(p =>
                {
                    IndexStatusText = $"スキャン {p.scanned:N0} / 追加 {p.inserted:N0}";
                    IndexPercent = p.scanned == 0 ? 0 : Math.Min(100, p.inserted * 100.0 / Math.Max(1, p.scanned));
                });

                var result = await _db.BulkUpsertAsync(EnumerateAsync(), batchSize: 200, progress: progress, ct: ct);

                IndexStatusText = $"完了: スキャン {result.scanned:N0} / 追加 {result.inserted:N0}";
                IndexPercent = 100;
                await RefreshDbCountAsync();

                WpfMessageBox.Show(
                    $"スキャン: {result.scanned:N0} 件\n新規: {result.inserted:N0} 件\nDB: {IndexDatabase.DatabasePath}",
                    "インデックス完了",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                IndexStatusText = "キャンセルしました";
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Index 処理でエラー: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _indexCts?.Dispose();
                _indexCts = null;
            }
        }

        // キャンセル
        private void OnCancelIndexClick(object sender, RoutedEventArgs e)
        {
            _indexCts?.Cancel();
        }

        // DB 件数表示
        private async void OnShowDbCountsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                long files = await CountAsync("files");
                long renms = await CountAsync("rename_suggestions");
                long moves = await CountAsync("moves");

                WpfMessageBox.Show(
                    $"files: {files:N0}\nrename_suggestions: {renms:N0}\nmoves: {moves:N0}\n\nDB: {IndexDatabase.DatabasePath}",
                    "DB 状態",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"DB情報の取得に失敗: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 選択中フォルダのパス取得
        private List<string> GetSelectedFolderPaths()
        {
            var list = new List<string>();
            if (TreeViewRoot?.SelectedItem is FolderNode node && Directory.Exists(node.FullPath))
                list.Add(node.FullPath);
            return list;
        }

        // 既定の対象ルート（未選択時用）
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
                IndexedCountText = (await CountAsync("files")).ToString("N0");
            }
            catch
            {
                IndexedCountText = "0";
            }
        }

        private static async Task<long> CountAsync(string table)
        {
            await using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = IndexDatabase.DatabasePath }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        }

        // ===== INotifyPropertyChanged =====
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

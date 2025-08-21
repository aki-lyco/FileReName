// Explore/FilesControl.xaml.cs
// - 左：フォルダツリー（展開で遅延ロード）
// - 中：ファイル一覧（選択フォルダ直下のみ表示）
// - ツールバー：自動DB更新 / サブフォルダ含む / 手動インデックス / 停止 / 件数表示
// - 大量件数対策：BulkUpsert（バッチコミット + WAL）＋ キャンセル対応
// - 進捗UI対応：IsIndexing / IndexPercent / IndexStatusText / IndexedCountText プロパティ追加

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;               // RoutedEventArgs, MessageBoxButton, MessageBoxImage
using System.Windows.Controls;      // TreeViewItem
using Explore.FileSystem;           // ExplorerViewModel, FolderNode, DefaultFileSystemService
using Explore.Indexing;             // IndexDatabase, DbFileRecord, FileKeyUtil
using Microsoft.Data.Sqlite;        // 件数確認ユーティリティ用
using WpfMessageBox = System.Windows.MessageBox; // ★ WPFのMessageBoxを明示

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        // 画面VM（フォルダツリー + ファイル一覧）
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());
        public ExplorerViewModel VM => _vm; // ★ XAML から {Binding VM.*} で参照

        // SQLite DB（index.db）
        private static readonly IndexDatabase _db = new();

        // トグル状態
        private bool _includeSubs;  // サブフォルダを含める（Index時のみ影響）
        private bool _autoIndex;    // フォルダ選択時に自動Index

        // インデックス処理のキャンセル用
        private CancellationTokenSource? _indexCts;

        // ==== 進捗UI用プロパティ ====
        private bool _isIndexing;
        public bool IsIndexing { get => _isIndexing; private set { _isIndexing = value; Raise(); } }

        private double _indexPercent;
        public double IndexPercent { get => _indexPercent; private set { _indexPercent = value; Raise(); } }

        private string _indexStatusText = "待機中";
        public string IndexStatusText { get => _indexStatusText; private set { _indexStatusText = value; Raise(); } }

        private string _indexedCountText = "";
        public string IndexedCountText { get => _indexedCountText; private set { _indexedCountText = value; Raise(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public FilesControl()
        {
            InitializeComponent();

            // 画面がロードされたら初期化
            Loaded += async (_, __) =>
            {
                // 進捗用プロパティとVMの両方にバインドするため DataContext は this
                DataContext = this;

                await _db.EnsureCreatedAsync();
                await _vm.LoadRootsAsync();
            };
        }

        // ★ 追加：既定DBパスを算出するヘルパー（IndexDatabase と同じロジック）
        private static string GetDefaultDbPath()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileReName");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "index.db");
        }

        // -------- ツリー操作 --------

        // ノード展開時：子フォルダを遅延ロード
        private async void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FolderNode node)
            {
                await _vm.EnsureChildrenLoadedAsync(node);
            }
        }

        // ノード選択時：ファイル一覧を更新 ＋ 自動IndexがONなら投入
        private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderNode node)
            {
                await _vm.NavigateToAsync(node.FullPath);

                if (_autoIndex)
                {
                    try
                    {
                        _indexCts?.Cancel();
                        _indexCts = new CancellationTokenSource();

                        await _db.EnsureCreatedAsync();

                        var records = EnumerateRecordsAsync(
                            node.FullPath,
                            recursive: _includeSubs,
                            _indexCts.Token);

                        await _db.BulkUpsertAsync(
                            records,
                            batchSize: 500,
                            progress: null,            // 自動時は静かに
                            ct: _indexCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // ユーザーが停止
                    }
                    catch (Exception ex)
                    {
                        WpfMessageBox.Show(ex.Message, "Auto Index Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        // -------- トグル（ツールバー） --------

        private void IncludeSubs_Checked(object sender, RoutedEventArgs e) => _includeSubs = true;
        private void IncludeSubs_Unchecked(object sender, RoutedEventArgs e) => _includeSubs = false;

        private void AutoIndex_Checked(object sender, RoutedEventArgs e) => _autoIndex = true;
        private void AutoIndex_Unchecked(object sender, RoutedEventArgs e) => _autoIndex = false;

        // -------- コマンド（ツールバーのボタン） --------

        // 選択中フォルダを手動でインデックス投入（進捗表示あり）
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
                    // パーセントは総件数が不明なので省略（必要なら推定可）
                });

                var records = EnumerateRecordsAsync(root, recursive: _includeSubs, _indexCts.Token);

                var (scanned, inserted) = await _db.BulkUpsertAsync(
                    records,
                    batchSize: 500,
                    progress: prog,
                    ct: _indexCts.Token);

                var files = await GetTableCountAsync("files");
                IndexedCountText = $"DB件数: {files:N0}";

                WpfMessageBox.Show(
                    $"スキャン: {scanned} 件\n新規: {inserted} 件\nDB: {GetDefaultDbPath()}",
                    "Index 完了",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                IndexStatusText = "完了";
            }
            catch (OperationCanceledException)
            {
                IndexStatusText = "キャンセルしました";
                WpfMessageBox.Show("インデックスを中断しました。",
                    "Index", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                IndexStatusText = "エラー: " + ex.Message;
                WpfMessageBox.Show(ex.Message, "Index Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsIndexing = false;
                _indexCts?.Dispose();
                _indexCts = null;
            }
        }

        // インデックス処理を停止
        private void OnCancelIndexClick(object sender, RoutedEventArgs e)
        {
            _indexCts?.Cancel();
        }

        // DBの件数を確認
        private async void OnShowDbCountsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await _db.EnsureCreatedAsync();

                var files = await GetTableCountAsync("files");
                var renms = await GetTableCountAsync("rename_suggestions");
                var moves = await GetTableCountAsync("moves");

                WpfMessageBox.Show(
                    $"files: {files}\nrename_suggestions: {renms}\nmoves: {moves}\n\nDB: {GetDefaultDbPath()}",
                    "DB 状態",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "DB Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------- ユーティリティ --------

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
                catch
                {
                    // 個別失敗はスキップ
                }

                if (rec != null) yield return rec;
                if ((uint)Environment.TickCount % 2048 == 0)
                    await Task.Yield();
            }
        }

        private async Task<long> GetTableCountAsync(string table)
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = GetDefaultDbPath() }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
    }
}

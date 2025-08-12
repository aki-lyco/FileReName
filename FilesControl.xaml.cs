// Explore/FilesControl.xaml.cs
// - 左：フォルダツリー（展開で遅延ロード）
// - 中：ファイル一覧（選択フォルダ直下のみ表示）
// - ツールバー：自動DB更新 / サブフォルダ含む / 手動インデックス / 停止 / 件数表示
// - 大量件数対策：BulkUpsert（バッチコミット + WAL）＋ キャンセル対応

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;               // RoutedEventArgs, MessageBoxButton, MessageBoxImage
using System.Windows.Controls;      // TreeViewItem
using Explore.FileSystem;           // ExplorerViewModel, FolderNode, DefaultFileSystemService
using Explore.Indexing;             // IndexDatabase, DbFileRecord, FileKeyUtil
using Microsoft.Data.Sqlite;        // 件数確認ユーティリティ用

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl
    {
        // 画面VM（フォルダツリー + ファイル一覧）
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());

        // SQLite DB（index.db）
        private static readonly IndexDatabase _db = new();

        // トグル状態
        private bool _includeSubs;  // サブフォルダを含める（Index時のみ影響）
        private bool _autoIndex;    // フォルダ選択時に自動Index

        // インデックス処理のキャンセル用
        private CancellationTokenSource? _indexCts;

        public FilesControl()
        {
            InitializeComponent();

            // 画面がロードされたら初期化
            Loaded += async (_, __) =>
            {
                // XAML バインディングのため先に DataContext
                DataContext = _vm;

                // DB を確実に初期化（存在すれば何もしない）
                await _db.EnsureCreatedAsync();

                // ルート（ドライブ等）をロード
                await _vm.LoadRootsAsync();
            };
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

                        // 大量件数に耐えるバルクUpsert（IndexDatabase 側に実装）
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
                        System.Windows.MessageBox.Show(ex.Message, "Auto Index Error",
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

        // 選択中フォルダを手動でインデックス投入（ダイアログで結果を表示）
        private async void OnIndexCurrentFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();

                var root = _vm?.CurrentPath;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    System.Windows.MessageBox.Show("左のツリーでフォルダーを選んでください。");
                    return;
                }

                await _db.EnsureCreatedAsync();

                // 進捗は必要ならステータスバー等に出せる
                var prog = new Progress<(long scanned, long inserted)>(_ =>
                {
                    // 例: Debug.WriteLine($"scanned={_.scanned}, inserted={_.inserted}");
                });

                var records = EnumerateRecordsAsync(root, recursive: _includeSubs, _indexCts.Token);

                var (scanned, inserted) = await _db.BulkUpsertAsync(
                    records,
                    batchSize: 500,
                    progress: prog,
                    ct: _indexCts.Token);

                System.Windows.MessageBox.Show(
                    $"スキャン: {scanned} 件\n新規: {inserted} 件\nDB: {_db.DatabasePath}",
                    "Index 完了",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                System.Windows.MessageBox.Show("インデックスを中断しました。",
                    "Index", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Index Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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

                System.Windows.MessageBox.Show(
                    $"files: {files}\nrename_suggestions: {renms}\nmoves: {moves}\n\nDB: {_db.DatabasePath}",
                    "DB 状態",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "DB Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------- ユーティリティ --------

        /// <summary>
        /// ファイルシステムからレコードを“その場で”生成して流す（巨大配列にしない）
        /// </summary>
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

                // UIの固まりを緩和（適度にスレッド譲渡）
                if ((uint)Environment.TickCount % 2048 == 0)
                    await Task.Yield();
            }
        }

        /// <summary>指定テーブルの件数取得（軽量）</summary>
        private async Task<long> GetTableCountAsync(string table)
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = _db.DatabasePath }.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
    }
}

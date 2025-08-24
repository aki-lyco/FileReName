﻿using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Explore.UI;             // UiSettings
using Explore.Indexing;       // IndexDatabase
using Microsoft.Data.Sqlite;  // SQLite
using WpfMessageBox = System.Windows.MessageBox; // WPFのMessageBoxに明示エイリアス

namespace Explore
{
    public partial class SettingsWindow : Window
    {
        // %APPDATA%\Explore\gemini_api_key.txt
        private readonly string _appDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Explore");
        private string ApiKeyFilePath => Path.Combine(_appDataDir, "gemini_api_key.txt");

        private string? _dbPath;

        public SettingsWindow()
        {
            InitializeComponent();

            // 既存の UI 設定（インデックス系）を反映
            try
            {
                AutoIndexBox.IsChecked = UiSettings.Instance.AutoIndexOnSelect;
                IncludeSubsBox.IsChecked = UiSettings.Instance.IncludeSubfolders;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"設定の読み込みに失敗しました。\n{ex.Message}",
                    "設定",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // 既存の API キーを読み込んで表示（保存元を自動判定）
            LoadExistingGeminiKey();

            // ★ DBパスは static プロパティを型名で参照
            try
            {
                _dbPath = IndexDatabase.DatabasePath;
                DbPathText.Text = _dbPath;
            }
            catch (Exception ex)
            {
                DbPathText.Text = $"（DBパスの取得に失敗）{ex.Message}";
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await RefreshDbCountsAsync();
        }

        // ===== OK / Cancel =====

        private async void OnOK(object? sender, RoutedEventArgs e)
        {
            try
            {
                // 1) インデックス系
                UiSettings.Instance.AutoIndexOnSelect = AutoIndexBox.IsChecked == true;
                UiSettings.Instance.IncludeSubfolders = IncludeSubsBox.IsChecked == true;
                UiSettings.Instance.Save();
                UiSettings.Instance.RaiseChanged();

                // 2) Gemini API キー保存
                var key = GetApiKeyInput().Trim();

                if (StoreEnvRadio.IsChecked == true)
                {
                    SaveToEnvironment(key);
                }
                else // StoreFileRadio
                {
                    SaveToFile(key);
                }

                // 念のため件数表示も最新化
                await RefreshDbCountsAsync();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"設定の保存に失敗しました。\n{ex.Message}",
                    "設定",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ====== API キー表示切替 ======
        private void OnShowApiChecked(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (ShowApiSwitch.IsChecked == true)
                {
                    // Password -> Plain へ同期して表示
                    ApiKeyBoxPlain.Text = ApiKeyBox.Password;
                    ApiKeyBoxPlain.Visibility = Visibility.Visible;
                    ApiKeyBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Plain -> Password へ同期して隠す
                    ApiKeyBox.Password = ApiKeyBoxPlain.Text;
                    ApiKeyBox.Visibility = Visibility.Visible;
                    ApiKeyBoxPlain.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // 見た目の切替だけなので握りつぶし
            }
        }

        // ====== 読み込み・保存処理（APIキー） ======

        private void LoadExistingGeminiKey()
        {
            string? fileKey = null;
            if (File.Exists(ApiKeyFilePath))
            {
                try
                {
                    fileKey = File.ReadAllText(ApiKeyFilePath, Encoding.UTF8).Trim();
                }
                catch { /* 読めない場合は無視 */ }
            }

            string? envUser = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            string? envProc = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Process);
            string? envKey = !string.IsNullOrWhiteSpace(envUser) ? envUser : envProc;

            // 優先順位：ファイル > 環境変数（ユーザー/プロセス）
            if (!string.IsNullOrEmpty(fileKey))
            {
                SetApiKeyInput(fileKey);
                StoreFileRadio.IsChecked = true;
                StoreEnvRadio.IsChecked = false;
                ApiSourceNote.Text = $"現在: ファイル（{ApiKeyFilePath}）から読み込みました。";
            }
            else if (!string.IsNullOrEmpty(envKey))
            {
                SetApiKeyInput(envKey);
                StoreEnvRadio.IsChecked = true;
                StoreFileRadio.IsChecked = false;
                ApiSourceNote.Text = "現在: ユーザー環境変数 GEMINI_API_KEY から読み込みました。";
            }
            else
            {
                SetApiKeyInput(string.Empty);
                StoreEnvRadio.IsChecked = true; // 既定は環境変数
                ApiSourceNote.Text = "現在: 保存されたキーは見つかりません。";
            }
        }

        private void SaveToEnvironment(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                // 削除（クリア）
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.Process);
                ApiSourceNote.Text = "環境変数 GEMINI_API_KEY を削除しました。";
                return;
            }

            // ユーザー環境変数 + カレントプロセスに反映
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
            ApiSourceNote.Text = "環境変数 GEMINI_API_KEY に保存しました（このアプリでは直ちに有効）。";
        }

        private void SaveToFile(string key)
        {
            // %APPDATA%\Explore\gemini_api_key.txt に保存（空なら削除）
            Directory.CreateDirectory(_appDataDir);

            if (string.IsNullOrEmpty(key))
            {
                if (File.Exists(ApiKeyFilePath))
                    File.Delete(ApiKeyFilePath);
                ApiSourceNote.Text = $"ファイル（{ApiKeyFilePath}）を削除しました。";
                return;
            }

            File.WriteAllText(ApiKeyFilePath, key, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                // 隠し属性を付与（失敗しても致命的ではない）
                var attr = File.GetAttributes(ApiKeyFilePath);
                File.SetAttributes(ApiKeyFilePath, attr | FileAttributes.Hidden);
            }
            catch { /* ignore */ }

            // 現プロセスにも反映してすぐ使えるようにする
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
            ApiSourceNote.Text = $"ファイル（{ApiKeyFilePath}）に保存しました（このアプリでは直ちに有効）。";
        }

        private string GetApiKeyInput()
        {
            return (ShowApiSwitch.IsChecked == true) ? ApiKeyBoxPlain.Text : ApiKeyBox.Password;
        }

        private void SetApiKeyInput(string value)
        {
            ApiKeyBox.Password = value ?? string.Empty;
            ApiKeyBoxPlain.Text = value ?? string.Empty;
        }

        // ====== DB件数の再読込 ======

        private async void OnReloadCounts(object sender, RoutedEventArgs e)
        {
            await RefreshDbCountsAsync();
        }

        private async Task RefreshDbCountsAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_dbPath))
                {
                    FileCountText.Text = "—";
                    FtsCountText.Text = "—";
                    return;
                }

                if (!File.Exists(_dbPath))
                {
                    FileCountText.Text = "0（DBが見つかりません）";
                    FtsCountText.Text = "—";
                    return;
                }

                long files = 0;
                long? fts = null;

                await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
                {
                    await conn.OpenAsync();

                    // files
                    files = await ExecScalarLongAsync(conn, "SELECT COUNT(*) FROM files");

                    // files_fts（存在しない場合は「—」表記）
                    try
                    {
                        fts = await ExecScalarLongAsync(conn, "SELECT COUNT(*) FROM files_fts");
                    }
                    catch
                    {
                        fts = null;
                    }
                }

                var n = CultureInfo.CurrentCulture;
                FileCountText.Text = files.ToString("N0", n);
                FtsCountText.Text = (fts.HasValue ? fts.Value.ToString("N0", n) : "—");
            }
            catch (Exception ex)
            {
                FileCountText.Text = "—";
                FtsCountText.Text = "—";
                WpfMessageBox.Show(this,
                    $"DB件数の取得に失敗しました。\n{ex.Message}",
                    "データベース",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static async Task<long> ExecScalarLongAsync(SqliteConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var obj = await cmd.ExecuteScalarAsync();
            if (obj == null || obj is DBNull) return 0L;
            return Convert.ToInt64(obj, CultureInfo.InvariantCulture);
        }
    }
}

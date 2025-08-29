using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Explore.UI;               // UiSettings
using Explore.Indexing;         // IndexDatabase
using Microsoft.Data.Sqlite;    // SQLite

// 明示的に WPF 側の型を使うためのエイリアス
using WpfMessageBox = System.Windows.MessageBox;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;

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
                // AutoIndex チェックボックス（これはXAML上そのまま）
                var auto = this.FindName("AutoIndexBox") as WpfCheckBox;
                if (auto != null) auto.IsChecked = UiSettings.Instance.AutoIndexOnSelect;

                // ▼ サブフォルダ深さ：DepthBox（数値）があればそれを使う。無ければ旧IncludeSubsBoxに値を反映。
                var depthBox = this.FindName("DepthBox") as WpfTextBox;
                if (depthBox != null)
                {
                    depthBox.Text = UiSettings.Instance.IncludeDepth.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var includeSubs = this.FindName("IncludeSubsBox") as WpfCheckBox;
                    if (includeSubs != null)
                        includeSubs.IsChecked = (UiSettings.Instance.IncludeDepth != 0);
                }
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
                var auto = this.FindName("AutoIndexBox") as WpfCheckBox;
                UiSettings.Instance.AutoIndexOnSelect = (auto?.IsChecked == true);

                // DepthBox（数値）があればそれを優先。無ければ IncludeSubsBox のON/OFFを -1 / 0 にマップ。
                var depthBox = this.FindName("DepthBox") as WpfTextBox;
                if (depthBox != null)
                {
                    if (!int.TryParse(depthBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth))
                        depth = -1; // 入力不正は無制限扱い
                    UiSettings.Instance.IncludeDepth = depth;
                }
                else
                {
                    var includeSubs = this.FindName("IncludeSubsBox") as WpfCheckBox;
                    UiSettings.Instance.IncludeDepth = (includeSubs?.IsChecked == true) ? -1 : 0;
                }

                UiSettings.Instance.Save();
                UiSettings.Instance.RaiseChanged();

                // 2) Gemini API キー保存
                var key = GetApiKeyInput().Trim();

                var env = this.FindName("StoreEnvRadio") as WpfRadioButton;
                if (env?.IsChecked == true)
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

        // ====== 数値入力のバリデーション（DepthBox用） ======
        // XAML側で TextBox x:Name="DepthBox" に PreviewTextInput="OnDepthPreviewTextInput" を付けてください。
        private void OnDepthPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 0-9 と '-' のみ許可（簡易）。詳細な整合性はOK時に int.TryParse で判定。
            e.Handled = !Regex.IsMatch(e.Text ?? "", @"^[0-9\-]$");
        }

        // ====== 表示切替（APIキー） ======
        private void OnShowApiChecked(object? sender, RoutedEventArgs e)
        {
            try
            {
                var show = this.FindName("ShowApiSwitch") as WpfCheckBox;
                var boxPwd = this.FindName("ApiKeyBox") as WpfPasswordBox;
                var boxPlain = this.FindName("ApiKeyBoxPlain") as WpfTextBox;

                if (show?.IsChecked == true)
                {
                    if (boxPlain != null && boxPwd != null)
                    {
                        boxPlain.Text = boxPwd.Password;
                        boxPlain.Visibility = Visibility.Visible;
                        boxPwd.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    if (boxPlain != null && boxPwd != null)
                    {
                        boxPwd.Password = boxPlain.Text;
                        boxPwd.Visibility = Visibility.Visible;
                        boxPlain.Visibility = Visibility.Collapsed;
                    }
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

            var boxPwd = this.FindName("ApiKeyBox") as WpfPasswordBox;
            var boxPlain = this.FindName("ApiKeyBoxPlain") as WpfTextBox;
            var envRadio = this.FindName("StoreEnvRadio") as WpfRadioButton;
            var fileRadio = this.FindName("StoreFileRadio") as WpfRadioButton;
            var note = this.FindName("ApiSourceNote") as WpfTextBlock;

            // 優先順位：ファイル > 環境変数（ユーザー/プロセス）
            if (!string.IsNullOrEmpty(fileKey))
            {
                SetApiKeyInput(fileKey, boxPwd, boxPlain);
                if (fileRadio != null) fileRadio.IsChecked = true;
                if (envRadio != null) envRadio.IsChecked = false;
                if (note != null) note.Text = $"現在: ファイル（{ApiKeyFilePath}）から読み込みました。";
            }
            else if (!string.IsNullOrEmpty(envKey))
            {
                SetApiKeyInput(envKey, boxPwd, boxPlain);
                if (envRadio != null) envRadio.IsChecked = true;
                if (fileRadio != null) fileRadio.IsChecked = false;
                if (note != null) note.Text = "現在: ユーザー環境変数 GEMINI_API_KEY から読み込みました。";
            }
            else
            {
                SetApiKeyInput(string.Empty, boxPwd, boxPlain);
                if (envRadio != null) envRadio.IsChecked = true; // 既定は環境変数
                if (note != null) note.Text = "現在: 保存されたキーは見つかりません。";
            }
        }

        private void SaveToEnvironment(string key)
        {
            var note = this.FindName("ApiSourceNote") as WpfTextBlock;

            if (string.IsNullOrEmpty(key))
            {
                // 削除（クリア）
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.Process);
                if (note != null) note.Text = "環境変数 GEMINI_API_KEY を削除しました。";
                return;
            }

            // ユーザー環境変数 + カレントプロセスに反映
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
            if (note != null) note.Text = "環境変数 GEMINI_API_KEY に保存しました（このアプリでは直ちに有効）。";
        }

        private void SaveToFile(string key)
        {
            var note = this.FindName("ApiSourceNote") as WpfTextBlock;

            // %APPDATA%\Explore\gemini_api_key.txt に保存（空なら削除）
            Directory.CreateDirectory(_appDataDir);

            if (string.IsNullOrEmpty(key))
            {
                if (File.Exists(ApiKeyFilePath))
                    File.Delete(ApiKeyFilePath);
                if (note != null) note.Text = $"ファイル（{ApiKeyFilePath}）を削除しました。";
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
            if (note != null) note.Text = $"ファイル（{ApiKeyFilePath}）に保存しました（このアプリでは直ちに有効）。";
        }

        private string GetApiKeyInput()
        {
            var show = this.FindName("ShowApiSwitch") as WpfCheckBox;
            var boxPwd = this.FindName("ApiKeyBox") as WpfPasswordBox;
            var boxPlain = this.FindName("ApiKeyBoxPlain") as WpfTextBox;
            if (show?.IsChecked == true) return boxPlain?.Text ?? "";
            return boxPwd?.Password ?? "";
        }

        private void SetApiKeyInput(string value, WpfPasswordBox? pwd, WpfTextBox? plain)
        {
            if (pwd != null) pwd.Password = value ?? string.Empty;
            if (plain != null) plain.Text = value ?? string.Empty;
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

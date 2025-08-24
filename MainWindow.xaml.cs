using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Explore
{
    public partial class MainWindow : Window
    {
        // Ctrl+I 用コマンド
        public static readonly RoutedUICommand CheckAICommand =
            new RoutedUICommand("AI状態確認", "CheckAICommand", typeof(MainWindow));

        private bool _ready;
        private FilesControl _filesControl = null!;
        private ChatControl _chatControl = null!;
        private BuildControl _buildControl = null!;
        private AiRenameView _renameControl = null!; // AIリネーム画面

        public MainWindow()
        {
            InitializeComponent();

            // ショートカットのバインド
            this.CommandBindings.Add(new CommandBinding(CheckAICommand, async (_, __) => await RunAICheckAsync()));

            try
            {
                _filesControl = new FilesControl();
                _chatControl = new ChatControl();
                _buildControl = new BuildControl();
                _renameControl = new AiRenameView();

                // 既定ビュー
                MainViewBox.Content = _filesControl;
                if (FileToggle != null) { FileToggle.IsChecked = true; FileToggle.IsEnabled = false; }

                _ready = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "MainWindow initialization error");
            }
        }

        // ===== 画面切替 =====
        private void SwitchToFile(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            FileToggle.IsEnabled = false;
            MainViewBox.Content = _filesControl;

            if (ChatToggle != null) { ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true; }
            if (BuildToggle != null) { BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true; }
            if (RenameToggle != null) { RenameToggle.IsChecked = false; RenameToggle.IsEnabled = true; }
        }

        private void SwitchToChat(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            ChatToggle.IsEnabled = false;
            MainViewBox.Content = _chatControl;

            if (FileToggle != null) { FileToggle.IsChecked = false; FileToggle.IsEnabled = true; }
            if (BuildToggle != null) { BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true; }
            if (RenameToggle != null) { RenameToggle.IsChecked = false; RenameToggle.IsEnabled = true; }
        }

        private void SwitchToBuild(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            BuildToggle.IsEnabled = false;
            MainViewBox.Content = _buildControl;

            if (FileToggle != null) { FileToggle.IsChecked = false; FileToggle.IsEnabled = true; }
            if (ChatToggle != null) { ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true; }
            if (RenameToggle != null) { RenameToggle.IsChecked = false; RenameToggle.IsEnabled = true; }
        }

        private void SwitchToRename(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RenameToggle.IsEnabled = false;
            MainViewBox.Content = _renameControl;

            if (FileToggle != null) { FileToggle.IsChecked = false; FileToggle.IsEnabled = true; }
            if (ChatToggle != null) { ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true; }
            if (BuildToggle != null) { BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true; }
        }

        // ===== AI状態確認（メニュー／ショートカット） =====
        private async void Menu_CheckAI_Click(object sender, RoutedEventArgs e)
            => await RunAICheckAsync();

        private async Task RunAICheckAsync()
        {
            try
            {
                var provider = GeminiRenameProvider.TryCreateFromEnv();
                if (provider == null)
                {
                    ShowStatus("AIは無効です（APIキー未検出）。gemini_api_key.txt か 環境変数 GEMINI_API_KEY を設定してください。");
                    System.Windows.MessageBox.Show(this,
                        "AIは無効です（APIキー未検出）\n" +
                        "・実行フォルダまたは %APPDATA%\\Explore に gemini_api_key.txt を置く\n" +
                        "・または環境変数 GEMINI_API_KEY を設定",
                        "AI状態確認", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ShowStatus("AI接続確認中…");
                bool ok = await provider.PingAsync();
                if (ok)
                {
                    ShowStatus($"AIは有効です（モデル: {provider.Model}）");
                    System.Windows.MessageBox.Show(this, $"AIは有効です。\nモデル: {provider.Model}",
                        "AI状態確認", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    ShowStatus("AIキーは検出されましたが応答がありません（ネットワーク/モデル名/プロキシをご確認ください）");
                    System.Windows.MessageBox.Show(this,
                        "AIキーは検出されましたが応答がありません。\n" +
                        "・ネットワーク/ファイアウォール/プロキシ\n" +
                        "・モデル名（GEMINI_MODEL）をご確認ください。",
                        "AI状態確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("AI状態確認でエラーが発生しました。");
                System.Windows.MessageBox.Show(this, ex.Message, "AI状態確認エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 現在表示中の AI リネーム画面があれば、その StatusText にも反映
        private void ShowStatus(string text)
        {
            if (MainViewBox.Content is System.Windows.Controls.UserControl uc &&
                uc.DataContext is RenameViewModel vm)
            {
                vm.StatusText = text;
            }
        }

        // ===== 追加：歯車ボタンのハンドラ =====
        // いったん「構築」ビューに切り替える挙動にしています。
        // 専用の設定ウィンドウが用意できたらここで開くように差し替えてください。
        private void OpenSettings(object sender, RoutedEventArgs e)
        {
            if (!_ready)
            {
                System.Windows.MessageBox.Show(this, "画面初期化中です。", "設定", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (BuildToggle != null)
                BuildToggle.IsChecked = true;

            SwitchToBuild(sender, e);
        }
    }
}

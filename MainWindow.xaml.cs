using System;
using System.Windows;

namespace Explore
{
    public partial class MainWindow : Window
    {
        private bool ready = false;
        private readonly FilesControl _filesControl;
        private readonly ChatControl _chatControl;
        private readonly BuildControl _buildControl;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                _filesControl = new FilesControl();
                _chatControl = new ChatControl();
                _buildControl = new BuildControl();

                // 既定はファイルビュー
                MainViewBox.Content = _filesControl;
                if (FileToggle != null)
                {
                    FileToggle.IsChecked = true;
                    FileToggle.IsEnabled = false;
                }
                ready = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "MainWindow Error");
                throw; // デバッガにも伝える
            }
        }

        private void SwitchToFile(object sender, RoutedEventArgs e)
        {
            FileToggle.IsEnabled = false;
            if (ready) MainViewBox.Content = _filesControl;
            if (ChatToggle != null) { ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true; }
            if (BuildToggle != null) { BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true; }
        }

        private void SwitchToChat(object sender, RoutedEventArgs e)
        {
            ChatToggle.IsEnabled = false;
            if (ready) MainViewBox.Content = _chatControl;
            if (FileToggle != null) { FileToggle.IsChecked = false; FileToggle.IsEnabled = true; }
            if (BuildToggle != null) { BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true; }
        }

        private void SwitchToBuild(object sender, RoutedEventArgs e)
        {
            BuildToggle.IsEnabled = false;
            if (ready) MainViewBox.Content = _buildControl;
            if (FileToggle != null) { FileToggle.IsChecked = false; FileToggle.IsEnabled = true; }
            if (ChatToggle != null) { ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true; }
        }

        // 右上の歯車（設定）ボタン
        private void OpenSettings(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SettingsWindow { Owner = this };
                dlg.ShowDialog(); // OK/キャンセルに応じて UiSettings 側からイベントが飛びます
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "設定", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

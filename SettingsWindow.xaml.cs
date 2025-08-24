using System;
using System.Windows;
using Explore.UI; // UiSettings
using WpfMessageBox = System.Windows.MessageBox; // ← WPFのMessageBoxに明示エイリアス

namespace Explore
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();

            // 起動時に現在値をUIへ反映
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
        }

        private void OnOK(object sender, RoutedEventArgs e)
        {
            try
            {
                UiSettings.Instance.AutoIndexOnSelect = AutoIndexBox.IsChecked == true;
                UiSettings.Instance.IncludeSubfolders = IncludeSubsBox.IsChecked == true;

                // 保存と通知
                UiSettings.Instance.Save();
                UiSettings.Instance.RaiseChanged();

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

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

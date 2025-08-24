using System;
using System.Windows;
using Explore.UI; // UiSettings
using WpfMessageBox = System.Windows.MessageBox; // �� WPF��MessageBox�ɖ����G�C���A�X

namespace Explore
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();

            // �N�����Ɍ��ݒl��UI�֔��f
            try
            {
                AutoIndexBox.IsChecked = UiSettings.Instance.AutoIndexOnSelect;
                IncludeSubsBox.IsChecked = UiSettings.Instance.IncludeSubfolders;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"�ݒ�̓ǂݍ��݂Ɏ��s���܂����B\n{ex.Message}",
                    "�ݒ�",
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

                // �ۑ��ƒʒm
                UiSettings.Instance.Save();
                UiSettings.Instance.RaiseChanged();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"�ݒ�̕ۑ��Ɏ��s���܂����B\n{ex.Message}",
                    "�ݒ�",
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

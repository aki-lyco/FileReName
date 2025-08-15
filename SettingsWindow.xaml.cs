using System.Windows;
using Explore.UI;

namespace Explore
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            // èâä˙íl
            AutoIndexBox.IsChecked = UiSettings.Instance.AutoIndexOnSelect;
            IncludeSubsBox.IsChecked = UiSettings.Instance.IncludeSubfolders;
        }

        private void OnOK(object sender, RoutedEventArgs e)
        {
            UiSettings.Instance.AutoIndexOnSelect = AutoIndexBox.IsChecked == true;
            UiSettings.Instance.IncludeSubfolders = IncludeSubsBox.IsChecked == true;
            UiSettings.Instance.Save();
            UiSettings.Instance.RaiseChanged();
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

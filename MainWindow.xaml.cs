using System;
using System.Windows;

namespace Explore
{
    public partial class MainWindow : Window
    {
        private bool ready = false;
        private FilesControl _filesControl;
        private ChatControl _chatControl;
        private BuildControl _buildControl;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                _filesControl = new FilesControl();
                _chatControl = new ChatControl();
                _buildControl = new BuildControl();
                MainViewBox.Content = _filesControl;
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
            ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true;
            BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true;
        }

        private void SwitchToChat(object sender, RoutedEventArgs e)
        {
            ChatToggle.IsEnabled = false;
            if (ready) MainViewBox.Content = _chatControl;
            FileToggle.IsChecked = false; FileToggle.IsEnabled = true;
            BuildToggle.IsChecked = false; BuildToggle.IsEnabled = true;
        }

        private void SwitchToBuild(object sender, RoutedEventArgs e)
        {
            BuildToggle.IsEnabled = false;
            MainViewBox.Content = _buildControl;
            FileToggle.IsChecked = false; FileToggle.IsEnabled = true;
            ChatToggle.IsChecked = false; ChatToggle.IsEnabled = true;
        }
    }
}

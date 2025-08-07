using System.Drawing.Imaging.Effects;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Explore
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool ready = false;
        private FilesControl _filesControl;
        private ChatControl _chatControl;
        private BuildControl _buildControl;
        public MainWindow()
        {
            InitializeComponent();
            _filesControl = new FilesControl();
            _chatControl = new ChatControl();
            _buildControl = new BuildControl();
            MainViewBox.Content = _filesControl;
            ready = true;
        }

        private void SwitchToFile(object sender, RoutedEventArgs e)
        {
            FileToggle.IsEnabled = false;
            if (ready)
            {
                MainViewBox.Content = _filesControl;
            }
            ChatToggle.IsChecked = false;
            ChatToggle.IsEnabled = true;
            BuildToggle.IsChecked = false;
            BuildToggle.IsEnabled = true;
        }

        private void SwitchToChat(object sender, RoutedEventArgs e)
        {
            ChatToggle.IsEnabled = false;
            MainViewBox.Content = _chatControl;
            FileToggle.IsChecked = false;
            FileToggle.IsEnabled = true;
            BuildToggle.IsChecked = false;
            BuildToggle.IsEnabled = true;
        }

        private void SwitchToBuild(object sender, RoutedEventArgs e)
        {
            BuildToggle.IsEnabled = false;
            MainViewBox.Content = _buildControl;
            FileToggle.IsChecked = false;
            FileToggle.IsEnabled = true;
            ChatToggle.IsChecked = false;
            ChatToggle.IsEnabled = true;
        }
    }
}
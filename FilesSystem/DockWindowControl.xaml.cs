using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Explore.FilesSystem
{
    /// <summary>
    /// DockWindowControl.xaml の相互作用ロジック
    /// </summary>
    public partial class DockWindowControl : System.Windows.Controls.UserControl
    {
        public DockWindowControl()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(DockWindowControl), new PropertyMetadata(""));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(DockWindowControl), new PropertyMetadata(""));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty ToolbarProperty =
            DependencyProperty.Register(nameof(Toolbar), typeof(object), typeof(DockWindowControl), new PropertyMetadata(null));

        public object Toolbar
        {
            get => GetValue(ToolbarProperty);
            set => SetValue(ToolbarProperty, value);
        }


        public new static readonly DependencyProperty ContentProperty =
            DependencyProperty.Register(nameof(Content), typeof(object), typeof(DockWindowControl), new PropertyMetadata(null));

        public new object Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
// WPF aliases to avoid WinForms ambiguity
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace Explore
{
    public partial class AiRenameView : WpfControls.UserControl
    {
        private RenameViewModel Vm => (RenameViewModel)DataContext;

        public AiRenameView()
        {
            InitializeComponent();
            DataContext = new RenameViewModel();
        }

        // �t�@�C���ǉ�
        private void OnAddFiles(object sender, Wpf.RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "���l�[���Ώۂ̃t�@�C����I��"
            };
            if (dlg.ShowDialog() == true) Vm.AddFiles(dlg.FileNames);
        }

        // ���쐬
        private async void OnSuggest(object sender, Wpf.RoutedEventArgs e) => await Vm.SuggestAsync();

        // ���ׂđI��
        private void OnSelectAll(object sender, Wpf.RoutedEventArgs e) => Vm.SelectAll();

        // �I����K�p�i���������s�� ViewModel ���Ŏ����폜�j
        private async void OnApply(object sender, Wpf.RoutedEventArgs e) => await Vm.ApplyAsync();

        // �N���A�i���K�p�ł��S�������j
        private void OnClear(object sender, Wpf.RoutedEventArgs e) => Vm.ClearAll();

        // AI��Ԋm�F
        private async void OnCheckAI(object sender, Wpf.RoutedEventArgs e) => await Vm.CheckAIAsync();

        // Drag & Drop�iWPF �̌^�Ŗ����j
        private void OnDragOver(object sender, Wpf.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(Wpf.DataFormats.FileDrop)
                ? Wpf.DragDropEffects.Copy
                : Wpf.DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, Wpf.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(Wpf.DataFormats.FileDrop)) return;
            var paths = (IEnumerable<string>)e.Data.GetData(Wpf.DataFormats.FileDrop);
            Vm.AddFiles(paths.Where(System.IO.File.Exists));
        }
    }
}

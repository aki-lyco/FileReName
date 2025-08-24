using System.Collections.Generic;
using System.Linq;
// WPF alias (avoid WinForms ambiguity)
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace Explore
{
    public partial class AiRenameView : WpfControls.UserControl
    {
        private RenameViewModel Vm => (RenameViewModel)this.DataContext;

        public AiRenameView()
        {
            InitializeComponent();
            this.DataContext = new RenameViewModel();

            // ���{�ꃉ�x���iUnicode�G�X�P�[�v���g�킸���ڑ����OK�j
            TxtHeader.Text = "AI ���l�[��";
            BtnAdd.Content = "�t�@�C���ǉ��c";
            BtnSuggest.Content = "���쐬";
            BtnSelectAll.Content = "���ׂđI��";
            BtnApply.Content = "�I����K�p";
            BtnCheckAI.Content = "AI��Ԋm�F";

            ColApply.Header = "�K�p";
            ColOriginal.Header = "���̖��O";
            ColSuggested.Header = "��Ė��iAI�j";
            ColPreview.Header = "�K�p��v���r���[";

            TxtHint.Text = "�����Ƀt�@�C�����h���b�v���邩�u�t�@�C���ǉ��c�v���N���b�N���Ă��������B";
        }

        // Add files
        private void OnAddFiles(object sender, Wpf.RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "���l�[���Ώۂ̃t�@�C����I��"
            };
            if (dlg.ShowDialog() == true) Vm.AddFiles(dlg.FileNames);
        }

        // Suggest names
        private async void OnSuggest(object sender, Wpf.RoutedEventArgs e) => await Vm.SuggestAsync();

        // Select all
        private void OnSelectAll(object sender, Wpf.RoutedEventArgs e) => Vm.SelectAll();

        // Apply selected
        private async void OnApply(object sender, Wpf.RoutedEventArgs e) => await Vm.ApplyAsync();

        // �� AI��Ԋm�F
        private async void OnCheckAI(object sender, Wpf.RoutedEventArgs e)
        {
            if (sender is WpfControls.Button b) b.IsEnabled = false;
            try { await Vm.CheckAIAsync(); }
            finally { if (sender is WpfControls.Button b2) b2.IsEnabled = true; }
        }

        // Drag & Drop
        private void OnDragOver(object sender, Wpf.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(Wpf.DataFormats.FileDrop) ? Wpf.DragDropEffects.Copy : Wpf.DragDropEffects.None;
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

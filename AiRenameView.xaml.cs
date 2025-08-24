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

            // 日本語ラベル（Unicodeエスケープを使わず直接代入でOK）
            TxtHeader.Text = "AI リネーム";
            BtnAdd.Content = "ファイル追加…";
            BtnSuggest.Content = "候補作成";
            BtnSelectAll.Content = "すべて選択";
            BtnApply.Content = "選択を適用";
            BtnCheckAI.Content = "AI状態確認";

            ColApply.Header = "適用";
            ColOriginal.Header = "元の名前";
            ColSuggested.Header = "提案名（AI）";
            ColPreview.Header = "適用先プレビュー";

            TxtHint.Text = "ここにファイルをドロップするか「ファイル追加…」をクリックしてください。";
        }

        // Add files
        private void OnAddFiles(object sender, Wpf.RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "リネーム対象のファイルを選択"
            };
            if (dlg.ShowDialog() == true) Vm.AddFiles(dlg.FileNames);
        }

        // Suggest names
        private async void OnSuggest(object sender, Wpf.RoutedEventArgs e) => await Vm.SuggestAsync();

        // Select all
        private void OnSelectAll(object sender, Wpf.RoutedEventArgs e) => Vm.SelectAll();

        // Apply selected
        private async void OnApply(object sender, Wpf.RoutedEventArgs e) => await Vm.ApplyAsync();

        // ▼ AI状態確認
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

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

        // ファイル追加
        private void OnAddFiles(object sender, Wpf.RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "リネーム対象のファイルを選択"
            };
            if (dlg.ShowDialog() == true) Vm.AddFiles(dlg.FileNames);
        }

        // 候補作成
        private async void OnSuggest(object sender, Wpf.RoutedEventArgs e) => await Vm.SuggestAsync();

        // すべて選択
        private void OnSelectAll(object sender, Wpf.RoutedEventArgs e) => Vm.SelectAll();

        // 選択を適用（成功した行は ViewModel 側で自動削除）
        private async void OnApply(object sender, Wpf.RoutedEventArgs e) => await Vm.ApplyAsync();

        // クリア（未適用でも全部消す）
        private void OnClear(object sender, Wpf.RoutedEventArgs e) => Vm.ClearAll();

        // AI状態確認
        private async void OnCheckAI(object sender, Wpf.RoutedEventArgs e) => await Vm.CheckAIAsync();

        // Drag & Drop（WPF の型で明示）
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

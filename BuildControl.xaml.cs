using System;
using System.Linq;
using System.Windows;
using Explore.Build;
using WinForms = System.Windows.Forms;  // FolderBrowserDialog
using Win32 = Microsoft.Win32;         // OpenFileDialog
using System.Collections;              // IList

namespace Explore
{
    public partial class BuildControl : System.Windows.Controls.UserControl
    {
        private readonly BuildViewModel _vm = new BuildViewModel();

        public BuildControl()
        {
            InitializeComponent();
            DataContext = _vm;

            // DI: UIサービス
            _vm.PickFolder = PickFolder;
            _vm.PickFilesOrFolder = PickFilesOrFolder;
            _vm.ShowMessage = (title, body) => System.Windows.MessageBox.Show(body, title);

            // 既定選択
            _vm.SelectedCategory = _vm.Root.Children.FirstOrDefault();
        }

        private string? PickFolder()
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = "保存先ベースフォルダを選択",
                UseDescriptionForTitle = true
            };
            var res = dlg.ShowDialog();
            if (res == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                return dlg.SelectedPath;
            return null;
        }

        private (string[] files, string? folder)? PickFilesOrFolder()
        {
            // 必要になったら実装
            return null;
        }

        // SelectTargets: 追加ボタン（ファイル）
        private void OnAddFilesClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "分類対象ファイルを選択"
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                    if (!_vm.SelectedTargets.Contains(f)) _vm.SelectedTargets.Add(f);
            }
        }

        // SelectTargets: 追加ボタン（フォルダ）
        private void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            var path = PickFolder();
            if (!string.IsNullOrWhiteSpace(path) && !_vm.SelectedTargets.Contains(path))
                _vm.SelectedTargets.Add(path);
        }

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _vm.SelectedCategory = e.NewValue as CategoryNode;
        }

        private void OnTestDrop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (paths != null && paths.Length > 0)
                {
                    _vm.TestFilePath = paths[0];
                    // 右パネルはダミー（保存先プレビューなどはVM側で実装予定）
                }
            }
        }

        // ===== ListBox（SelectedList）操作 =====

        // 選択を削除
        private void OnRemoveSelectedTargets(object sender, RoutedEventArgs e)
        {
            if (SelectedList?.ItemsSource is not IList src) return;

            var toRemove = new System.Collections.Generic.List<object>();
            foreach (var it in SelectedList.SelectedItems)
                toRemove.Add(it);
            foreach (var it in toRemove)
                src.Remove(it);
        }

        // 全クリア
        private void OnClearTargets(object sender, RoutedEventArgs e)
        {
            if (SelectedList?.ItemsSource is IList src) src.Clear();
        }

        // Delete キーで「選択を削除」
        private void SelectedList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                OnRemoveSelectedTargets(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }
}

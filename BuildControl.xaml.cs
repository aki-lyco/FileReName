using System;
using System.Collections;
using System.Linq;
using System.Windows;
using Explore.Build;
using WinForms = System.Windows.Forms;

namespace Explore
{
    public partial class BuildControl : System.Windows.Controls.UserControl
    {
        private readonly BuildViewModel _vm = new BuildViewModel();

        public BuildControl()
        {
            InitializeComponent();
            DataContext = _vm;

            // UI services
            _vm.PickFolder = PickFolder;
            _vm.ShowMessage = (title, body) => System.Windows.MessageBox.Show(body, title);
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

        // == SelectTargets: 追加（ファイル） ==
        private void OnAddFilesClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
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

        // == SelectTargets: 追加（フォルダ） ==
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

        // == ListBox 操作 ==
        private System.Windows.Controls.ListBox? ResolveListBox(object sender)
        {
            if (sender is System.Windows.Controls.ListBox lb) return lb;

            if (sender is System.Windows.DependencyObject d)
            {
                if (d is System.Windows.Controls.MenuItem mi &&
                    mi.Parent is System.Windows.Controls.ContextMenu cm &&
                    cm.PlacementTarget is System.Windows.Controls.ListBox plb)
                    return plb;

                var cur = d;
                while (cur != null && cur is not System.Windows.Controls.ListBox)
                    cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
                if (cur is System.Windows.Controls.ListBox lb1) return lb1;
            }

            if (SelectedList != null && SelectedList.IsKeyboardFocusWithin) return SelectedList;
            if (SelectedListDesign != null && SelectedListDesign.IsKeyboardFocusWithin) return SelectedListDesign;
            return SelectedList ?? SelectedListDesign;
        }

        private void OnRemoveSelectedTargets(object sender, RoutedEventArgs e)
        {
            var list = ResolveListBox(sender);
            if (list?.ItemsSource is IList src)
            {
                var toRemove = list.SelectedItems.Cast<object>().ToList();
                foreach (var it in toRemove) src.Remove(it);
            }
        }

        private void OnClearTargets(object sender, RoutedEventArgs e)
        {
            var list = ResolveListBox(sender);
            if (list?.ItemsSource is IList src) src.Clear();
        }

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

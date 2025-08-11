using System.Windows;
using Explore.FileSystem; // ← これ絶対入れて！

namespace Explore
{
    public partial class FilesControl : System.Windows.Controls.UserControl
    {
        private readonly ExplorerViewModel _vm = new(new DefaultFileSystemService());

        public FilesControl()
        {
            InitializeComponent();

            // 画面に載ったら VM をバインドしてルートを読み込む
            Loaded += async (_, __) =>
            {
                DataContext = _vm;          // ← これが無いと TreeView/ListView に何も出ません
                await _vm.LoadRootsAsync(); // ドライブ or プロファイルを Roots に投入
            };
        }

        // ツリー展開：初回だけ子フォルダを遅延ロード
        private async void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TreeViewItem tvi &&
                tvi.DataContext is FolderNode node)
            {
                await _vm.EnsureChildrenLoadedAsync(node);
            }
        }

        // ツリー選択：中央のファイル一覧を更新（シングルクリック）
        private async void OnFolderSelected(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderNode node)
            {
                await _vm.NavigateToAsync(node.FullPath);
            }
        }
    }
}

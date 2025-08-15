using System;

namespace Explore
{
    // WinForms と衝突しないように WPF をフル修飾
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);

            var settings = AppSettings.Load();

            // ダイアログを閉じてもアプリが落ちないよう、明示シャットダウンに一時変更
            var oldMode = this.ShutdownMode;
            this.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

            try
            {
                // 開発中は毎回表示
                var dlg = new FirstRunDialog(settings);
                dlg.ShowDialog(); // 結果は見ずに続行

                var win = new MainWindow();
                this.MainWindow = win;
                win.Show();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "Startup Error");
                this.Shutdown(-1);
            }
            finally
            {
                // 既定動作に戻す（お好みで OnExplicitShutdown のままでもOK）
                this.ShutdownMode = oldMode;
            }
        }
    }
}

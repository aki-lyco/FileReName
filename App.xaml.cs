using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // WPF
using System.Windows.Threading; // DispatcherPriority
using WpfMessageBox = System.Windows.MessageBox; // ← これで曖昧さ解消

namespace Explore
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstance;
        private static int _shown; // 例外ダイアログの連発防止

        public App()
        {
            // UI スレッドの未処理例外
            this.DispatcherUnhandledException += (s, e) =>
            {
                ShowFatal(e.Exception, "UI スレッド例外");
                e.Handled = true; // アプリを落とさない
            };

            // 非UI(Task) の未処理例外
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                ShowFatal(e.Exception, "Task 例外");
                e.SetObserved();
            };

            // ドメイン全体の未処理例外
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception");
                ShowFatal(ex, "ドメイン例外");
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- 単一起動（多重起動でロックされないように）----
            bool createdNew = false;
            try
            {
                _singleInstance = new Mutex(true, @"Global\FileReName.Explore.SingleInstance", out createdNew);
            }
            catch (Exception ex)
            {
                ShowFatal(ex, "単一起動の初期化エラー");
                Shutdown(-1);
                return;
            }

            if (!createdNew)
            {
                WpfMessageBox.Show("既に Explore が起動しています。", "情報",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // ---- テーマ辞書を安全に読み込み（失敗しても続行）----
            TryLoadThemeDictionary();

            // ---- MainWindow を必ず表示 ----
            try
            {
                if (this.MainWindow == null)
                {
                    var w = new MainWindow();
                    this.MainWindow = w;
                    w.Show();
                }
            }
            catch (Exception ex)
            {
                ShowFatal(ex, "起動初期化エラー");
                Shutdown(-1);
                return;
            }

            // ---- ここが追加点：開発中は毎回「初回セットアップ(DB更新)」を表示 ----
            // MainWindow を表示した後、UIが落ち着いてからダイアログを出す
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // AppSettings の作り方が別なら、この1行を既存の生成/読込APIに差し替え
                    var settings = new AppSettings();

                    var dlg = new FirstRunDialog(settings)
                    {
                        Owner = this.MainWindow,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };
                    dlg.ShowDialog();
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show(
                        "初回セットアップ画面の表示に失敗しました。\n\n" + ex.Message,
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }), DispatcherPriority.Background);
        }

        private void TryLoadThemeDictionary()
        {
            var tried = false;

            try
            {
                var pack = new Uri("pack://application:,,,/Themes/LightTheme.xaml", UriKind.Absolute);
                var dict = new ResourceDictionary { Source = pack };
                Current.Resources.MergedDictionaries.Add(dict);
                tried = true;
            }
            catch { /* 次の方法を試す */ }

            if (!tried)
            {
                try
                {
                    var relative = new Uri("Themes/LightTheme.xaml", UriKind.Relative);
                    var dict = new ResourceDictionary { Source = relative };
                    Current.Resources.MergedDictionaries.Add(dict);
                }
                catch (Exception ex)
                {
                    LogOnly(ex, "テーマ読み込み失敗");
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstance?.ReleaseMutex();
                _singleInstance?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }

        private static void ShowFatal(Exception ex, string title)
        {
            // 連発防止：最初の1回だけダイアログ表示。以降はログのみ。
            if (_shown == 0)
            {
                _shown = 1;
                try
                {
                    var path = WriteCrashLog(ex);
                    WpfMessageBox.Show($"{title}\n\n{ex.Message}\n\n詳細ログ: {path}",
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                catch { /* 下のフォールバックへ */ }
            }

            // 2回目以降はログのみ
            try { WriteCrashLog(ex); } catch { }
        }

        private static string WriteCrashLog(Exception ex)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "FileReName", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, ex.ToString());
            return path;
        }

        private static void LogOnly(Exception ex, string title)
        {
            try
            {
                var path = WriteCrashLog(new Exception($"{title}: {ex.Message}", ex));
                System.Diagnostics.Debug.WriteLine($"{title}: {ex}\n -> {path}");
            }
            catch { }
        }
    }
}

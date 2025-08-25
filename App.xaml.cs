using System;
using System.IO;
using System.Reflection;
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- 単一起動 ----
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

            // ---- 外部ツール整備 ----
            try
            {
                await SetupExternalAssetsAsync().ConfigureAwait(true); // TextExtractor が動く前に実施
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("External assets setup failed: " + ex);
            }

            // ---- テーマ ----
            TryLoadThemeDictionary();

            // ---- MainWindow ----
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

            // ---- 設定に従って「初回セットアップ」を出すか判断 ----
            try
            {
                var settings = AppSettings.Load(); // ★ 既存の settings.json を読む
                if (settings.Dev.AlwaysShowFirstRun) // ★ true のときだけ表示
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var dlg = new FirstRunDialog(settings)
                        {
                            Owner = this.MainWindow,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner
                        };
                        dlg.ShowDialog(); // チェックされたらダイアログ側で保存して二度と出ない
                    }, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    "初回セットアップ画面の表示に失敗しました。\n\n" + ex.Message,
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        // 外部アセットの取得＆環境変数セット（既存のまま）
        private static async Task SetupExternalAssetsAsync()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("Explore.AssetsManifest.json"))
            {
                if (s != null)
                {
                    await ExternalAssets.EnsureAllAsync(s).ConfigureAwait(false);
                }
            }

            static string? FindUnder(string? root, string file)
            {
                if (root == null || !Directory.Exists(root)) return null;
                try
                {
                    foreach (var p in Directory.EnumerateFiles(root, file, SearchOption.AllDirectories))
                        return p;
                }
                catch { }
                return null;
            }

            var popRoot = ExternalAssets.TryResolve("poppler");
            var popPdftotext = FindUnder(popRoot, "pdftotext.exe");
            var popPdftoppm = FindUnder(popRoot, "pdftoppm.exe");
            if (popPdftotext != null) Environment.SetEnvironmentVariable("PDFTOTEXT_EXE", popPdftotext);
            if (popPdftoppm != null) Environment.SetEnvironmentVariable("PDFTOPPM_EXE", popPdftoppm);

            string[] probables =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "Tesseract-OCR", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe")
            };
            string? tesseractExe = Array.Find(probables, File.Exists);

            if (tesseractExe == null)
            {
                var tesRoot = ExternalAssets.TryResolve("tesseract");
                tesseractExe = FindUnder(tesRoot, "tesseract.exe");
            }

            if (tesseractExe != null)
            {
                Environment.SetEnvironmentVariable("TESSERACT_EXE", tesseractExe);

                string? tessdataDir = null;
                var tesRoot = ExternalAssets.TryResolve("tesseract");
                if (tesRoot != null)
                {
                    var candidate = Path.Combine(tesRoot, "tesseract", "tessdata");
                    if (Directory.Exists(candidate)) tessdataDir = candidate;
                }
                if (tessdataDir == null)
                {
                    var pf = Path.GetDirectoryName(tesseractExe);
                    var candidate = pf != null ? Path.Combine(pf, "tessdata") : null;
                    if (candidate != null && Directory.Exists(candidate)) tessdataDir = candidate;
                }

                if (tessdataDir != null)
                {
                    var prefix = Path.GetDirectoryName(tessdataDir);
                    if (!string.IsNullOrEmpty(prefix))
                        Environment.SetEnvironmentVariable("TESSDATA_PREFIX", prefix);
                }

                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESSERACT_LANG")))
                {
                    Environment.SetEnvironmentVariable("TESSERACT_LANG", "jpn+eng");
                }
            }
        }

        private static void ShowFatal(Exception ex, string title)
        {
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
                catch { }
            }
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

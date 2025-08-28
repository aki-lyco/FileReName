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

        // A用：二重起動防止
        private static int _assetsPrepareRunning = 0;

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

            // ---- テーマ（軽いので先に適用）----
            TryLoadThemeDictionary();

            // ---- MainWindow を先に表示（ここがAの肝）----
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

            // ---- 外部ツール準備は UI をブロックしない（BG実行）----
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await PrepareExternalAssetsAsync(); // 完了後に AppStatus.IsOcrReady を立てる
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("External assets prepare failed: " + ex);
                }
            }, DispatcherPriority.Background);

            // ---- 設定に従って「初回セットアップ」を出すか判断 ----
            try
            {
                var settings = AppSettings.Load(); // settings.json を読む
                if (settings.Dev.AlwaysShowFirstRun) // true のときだけ表示
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

        /// <summary>
        /// 外部ツールの準備（A対応版）。
        /// 1) 設定キャッシュ（O(1)）→ OKなら即適用
        /// 2) 簡易探索（env/ProgramFiles/assets）→ OKなら解決して保存
        /// 3) 不足時はマニフェストから取得・展開 → 解決して保存
        /// ※ このメソッドは BG から呼ばれる（起動をブロックしない）
        /// </summary>
        private async Task PrepareExternalAssetsAsync()
        {
            // 二重起動防止
            if (Interlocked.Exchange(ref _assetsPrepareRunning, 1) == 1)
                return;

            try
            {
                // ① 設定キャッシュで即判定
                if (ExternalAssets.QuickCheckFromSettings())
                {
                    ApplyEnvFromSettings();   // 現プロセスに反映
                    AppStatus.SetOcrReady();  // フラグON（UIで使える）
                    // 背景でパス再解決（構成変化の軽チェック）
                    _ = Task.Run(async () =>
                    {
                        try { await ExternalAssets.ResolveAndCacheToolPathsAsync(); } catch { }
                    });
                    return;
                }

                // ② 従来の軽量探索（env→既知→assets再帰）
                var ready = AreExternalToolsAvailableQuickCheck();

                if (!ready)
                {
                    // ★ 初回など：待って確実に整える（DL/展開＋env設定）
                    await SetupExternalAssetsAsync().ConfigureAwait(true);

                    // 展開結果から実体パスを確定して設定に保存（次回はO(1)）
                    await ExternalAssets.ResolveAndCacheToolPathsAsync().ConfigureAwait(false);

                    // 念のため設定から env を反映
                    ApplyEnvFromSettings();
                    AppStatus.SetOcrReady();
                }
                else
                {
                    // 見つかったが設定未保存の場合：解決→保存→env反映
                    await ExternalAssets.ResolveAndCacheToolPathsAsync().ConfigureAwait(false);
                    ApplyEnvFromSettings();
                    AppStatus.SetOcrReady();

                    // ★ 2回目以降の最新化はBGで（起動をブロックしない）
                    _ = Task.Run(() => SetupExternalAssetsAsync());
                }
            }
            finally
            {
                Interlocked.Exchange(ref _assetsPrepareRunning, 0);
            }
        }

        /// <summary>
        /// 設定に保存されたパスを環境変数へ反映（現プロセス）
        /// </summary>
        private static void ApplyEnvFromSettings()
        {
            var ext = AppSettings.Load().ExternalTools;

            if (!string.IsNullOrEmpty(ext.PdfToText))
                Environment.SetEnvironmentVariable("PDFTOTEXT_EXE", ext.PdfToText);
            if (!string.IsNullOrEmpty(ext.PdfToPpm))
                Environment.SetEnvironmentVariable("PDFTOPPM_EXE", ext.PdfToPpm);
            if (!string.IsNullOrEmpty(ext.Tesseract))
                Environment.SetEnvironmentVariable("TESSERACT_EXE", ext.Tesseract);

            if (!string.IsNullOrEmpty(ext.TessdataDir))
            {
                var prefix = Path.GetDirectoryName(ext.TessdataDir);
                if (!string.IsNullOrEmpty(prefix))
                    Environment.SetEnvironmentVariable("TESSDATA_PREFIX", prefix);
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESSERACT_LANG")))
                Environment.SetEnvironmentVariable("TESSERACT_LANG", "jpn+eng");
        }

        /// <summary>
        /// すでに使用可能な Poppler/Tesseract があるか簡易チェック。
        /// env 変数 → 既知パス → assets 配下の順で軽く探す。
        /// （※ 設定キャッシュは PrepareExternalAssetsAsync 側で先に判定）
        /// </summary>
        private static bool AreExternalToolsAvailableQuickCheck()
        {
            // 1) まずは env を見る
            string? pdftext = Environment.GetEnvironmentVariable("PDFTOTEXT_EXE");
            string? pdfppm = Environment.GetEnvironmentVariable("PDFTOPPM_EXE");
            string? tesser = Environment.GetEnvironmentVariable("TESSERACT_EXE");

            bool popplerOk = !string.IsNullOrWhiteSpace(pdftext) && File.Exists(pdftext)
                          && !string.IsNullOrWhiteSpace(pdfppm) && File.Exists(pdfppm);
            bool tessOk = !string.IsNullOrWhiteSpace(tesser) && File.Exists(tesser);

            if (popplerOk && tessOk) return true;

            // 2) 既定インストール先（Tesseract）
            string[] probables =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "Tesseract-OCR", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe")
            };
            if (!tessOk)
            {
                foreach (var p in probables)
                    if (File.Exists(p)) { tessOk = true; break; }
            }

            // 3) assets 配下を軽く再帰検索（Poppler / Tesseract）
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

            if (!popplerOk)
            {
                var popRoot = ExternalAssets.TryResolve("poppler");
                var a = FindUnder(popRoot, "pdftotext.exe");
                var b = FindUnder(popRoot, "pdftoppm.exe");
                popplerOk = a != null && b != null;
            }
            if (!tessOk)
            {
                var tesRoot = ExternalAssets.TryResolve("tesseract");
                var t = FindUnder(tesRoot, "tesseract.exe");
                tessOk = t != null;
            }

            return popplerOk && tessOk;
        }

        /// <summary>
        /// 外部アセットの取得＆環境変数セット（重い処理。状況に応じて await/非同期で呼ぶ）
        /// </summary>
        private static async Task SetupExternalAssetsAsync()
        {
            // 1) マニフェスト(Embedded Resource)を読む → 不足分をDL/展開/インストール
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("Explore.AssetsManifest.json"))
            {
                if (s != null)
                {
                    await ExternalAssets.EnsureAllAsync(s).ConfigureAwait(false);
                }
            }

            // ユーティリティ：配下を再帰的に1個見つけたら返す
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

            // 2) Poppler の exe を探す（assets 配下を再帰検索）
            var popRoot = ExternalAssets.TryResolve("poppler");
            var popPdftotext = FindUnder(popRoot, "pdftotext.exe");
            var popPdftoppm = FindUnder(popRoot, "pdftoppm.exe");
            if (popPdftotext != null) Environment.SetEnvironmentVariable("PDFTOTEXT_EXE", popPdftotext);
            if (popPdftoppm != null) Environment.SetEnvironmentVariable("PDFTOPPM_EXE", popPdftoppm);

            // 3) Tesseract の exe を探す
            // まず一般的な既定インストール先（インストーラで入れた場合）
            string[] probables =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "Tesseract-OCR", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe")
            };
            string? tesseractExe = Array.Find(probables, File.Exists);

            // 見つからなければ assets 配下を再帰検索（ポータブル or 自己展開ZIP想定）
            if (tesseractExe == null)
            {
                var tesRoot = ExternalAssets.TryResolve("tesseract");
                tesseractExe = FindUnder(tesRoot, "tesseract.exe");
            }

            if (tesseractExe != null)
            {
                Environment.SetEnvironmentVariable("TESSERACT_EXE", tesseractExe);

                // tessdata の場所を推定：assets 配下の tesseract\tessdata を最優先
                string? tessdataDir = null;
                var tesRoot = ExternalAssets.TryResolve("tesseract");
                if (tesRoot != null)
                {
                    var candidate = Path.Combine(tesRoot, "tesseract", "tessdata");
                    if (Directory.Exists(candidate)) tessdataDir = candidate;
                }
                // 無ければ Program Files 内の tessdata
                if (tessdataDir == null)
                {
                    var pf = Path.GetDirectoryName(tesseractExe);
                    var candidate = pf != null ? Path.Combine(pf, "tessdata") : null;
                    if (candidate != null && Directory.Exists(candidate)) tessdataDir = candidate;
                }

                if (tessdataDir != null)
                {
                    // TESSDATA_PREFIX は tessdata の親ディレクトリを指す必要がある
                    var prefix = Path.GetDirectoryName(tessdataDir);
                    if (!string.IsNullOrEmpty(prefix))
                        Environment.SetEnvironmentVariable("TESSDATA_PREFIX", prefix);
                }

                // 既定のOCR言語（未設定なら）
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

    /// <summary>
    /// Aのための簡易ステータス。UI側で IsEnabled バインドやガードに使える。
    /// </summary>
    public static class AppStatus
    {
        public static bool IsOcrReady { get; private set; } = false;
        public static event EventHandler? OcrReadyChanged;

        public static void SetOcrReady()
        {
            if (IsOcrReady) return;
            IsOcrReady = true;
            try { OcrReadyChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }
    }
}

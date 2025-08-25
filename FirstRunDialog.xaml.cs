using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Explore.Indexing; // IndexDatabase, DbFileRecord, FileKeyUtil

namespace Explore
{
    public sealed partial class FirstRunDialog : Window, INotifyPropertyChanged
    {
        private readonly IndexDatabase _db = new();
        private CancellationTokenSource? _cts;
        private bool _finished; // ���s����/���~��ɁuClose�v�{�^����

        public AppSettings Settings { get; }

        // ---- INPC �v���p�e�B ----
        private string _phase = "Waiting";
        public string Phase { get => _phase; set { if (_phase != value) { _phase = value; OnPropertyChanged(); } } }

        private double _percent;
        public double Percent { get => _percent; set { if (Math.Abs(_percent - value) > 0.0001) { _percent = value; OnPropertyChanged(); } } }

        private string _status = "";
        public string StatusLine { get => _status; set { if (_status != value) { _status = value; OnPropertyChanged(); } } }

        private string _detail = "";
        public string DetailLine { get => _detail; set { if (_detail != value) { _detail = value; OnPropertyChanged(); } } }

        // �����\��
        private bool _showCompletion;
        public bool ShowCompletion { get => _showCompletion; set { if (_showCompletion != value) { _showCompletion = value; OnPropertyChanged(); } } }

        private string _completionMessage = "";
        public string CompletionMessage { get => _completionMessage; set { if (_completionMessage != value) { _completionMessage = value; OnPropertyChanged(); } } }

        // �� �ǉ��F����\�����Ȃ��i�`�F�b�N���ꂽ�瑦�ۑ�������j
        private bool _dontShowAgain;
        public bool DontShowAgain
        {
            get => _dontShowAgain;
            set
            {
                if (_dontShowAgain == value) return;
                _dontShowAgain = value;
                OnPropertyChanged();
                if (value)
                {
                    try
                    {
                        if (Settings.Dev.AlwaysShowFirstRun)
                        {
                            Settings.Dev.AlwaysShowFirstRun = false;
                            Settings.Save();
                        }
                    }
                    catch { /* �ۑ����s�͋N���p�� */ }
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public FirstRunDialog(AppSettings settings)
        {
            InitializeComponent();
            Settings = settings;
            DataContext = this;

            // ����͖��`�F�b�N�i��������\���j�B���ɔ�\���ݒ�Ȃ�`�F�b�N�ς݂Ɍ����邱�Ƃ��ł��邪�A
            // �����ł͖��� false �Ŏn�߂�B
            DontShowAgain = false;
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = false;
            CancelBtn.Content = "Cancel and continue";
            CancelBtn.IsEnabled = true;
            _finished = false;
            ShowCompletion = false;
            CompletionMessage = "";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                await _db.EnsureCreatedAsync();

                Phase = "Preparing�c";
                Percent = 0;
                StatusLine = "";
                DetailLine = "";

                var sw = Stopwatch.StartNew();
                int discovered = 0, skipped = 0;

                // �i���� 100ms �ɊԈ���
                using var throttled = new ThrottledProgress<(long scanned, long inserted)>(
                    Dispatcher, TimeSpan.FromMilliseconds(100),
                    p =>
                    {
                        var speed = p.scanned <= 0 ? 0 : p.scanned / Math.Max(1.0, sw.Elapsed.TotalSeconds);
                        var covered = discovered > 0 ? (double)p.scanned / discovered * 100.0 : 0.0;

                        Percent = Math.Clamp(covered, 0.0, 100.0);
                        Phase = "Indexing (HOT)";
                        StatusLine = $"Discovered {discovered:N0} / Indexed {p.scanned:N0} (New {p.inserted:N0}) | {speed:N0}/s";
                        DetailLine = $"Skipped {skipped:N0} | Target {Settings.FirstRun.HotMaxFiles:N0}";
                    });

                // �Ώۗ�
                var records = EnumerateHotAsync(
                    Settings, _cts.Token,
                    onSeen: _ => { discovered++; },
                    onSkip: _ => { skipped++; });

                // DB �� BG �X���b�h��
                var (scanned, inserted) = await Task.Run(() =>
                    _db.BulkUpsertAsync(records, batchSize: 1000,
                                        progress: throttled,
                                        ct: _cts.Token));

                sw.Stop();

                Phase = "Done";
                Percent = 100;
                StatusLine = $"Discovered {discovered:N0} / Indexed {scanned:N0} (New {inserted:N0})";
                DetailLine = $"Time {sw.Elapsed.TotalSeconds:N1}s | Skipped {skipped:N0}";

                // �����\��
                CompletionMessage = "�������܂���";
                ShowCompletion = true;

                _finished = true;
                CancelBtn.Content = "Close";
                CancelBtn.IsEnabled = true;
                StartBtn.IsEnabled = false;
            }
            catch (OperationCanceledException)
            {
                Phase = "Canceled";
                _finished = true;
                ShowCompletion = false;
                CompletionMessage = "";
                CancelBtn.Content = "Close";
                CancelBtn.IsEnabled = true;
                StartBtn.IsEnabled = true; // �Ď��s��
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "Hot Start Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _finished = true;
                ShowCompletion = false;
                CompletionMessage = "";
                CancelBtn.Content = "Close";
                CancelBtn.IsEnabled = true;
                StartBtn.IsEnabled = true;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            // ���s���Ȃ�L�����Z���A����/�L�����Z����Ȃ����
            if (!_finished && _cts != null)
            {
                _cts.Cancel();
                return;
            }

            DialogResult = true;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // �O�̂��߁A�`�F�b�N�ς݂Ȃ�N���[�Y���ɂ��ۑ��i�����ۑ��Ɏ��s�����P�[�X�̕ی��j
            try
            {
                if (DontShowAgain && Settings.Dev.AlwaysShowFirstRun)
                {
                    Settings.Dev.AlwaysShowFirstRun = false;
                    Settings.Save();
                }
            }
            catch { /* ���� */ }
            base.OnClosed(e);
        }

        // --------- HOT �񋓁itry �̊O�� yield�j ---------
        private async IAsyncEnumerable<DbFileRecord> EnumerateHotAsync(
            AppSettings s,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
            Action<string>? onSeen = null,
            Action<string>? onSkip = null)
        {
            var winFrom = DateTime.UtcNow.AddDays(-Math.Abs(s.FirstRun.HotWindowDays));
            var hotExts = new HashSet<string>(s.FirstRun.HotExts.Select(e => "." + e.ToLowerInvariant()));
            var skipExts = new HashSet<string>(s.ExcludeExtensions.Select(e => e.ToLowerInvariant()));
            var skipDirs = s.ExcludeDirectories;
            var skipAttrs = GetSkippedAttributes(s.SkipAttributes);

            var roots = GetKnownRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None
            };

            int yielded = 0;

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                foreach (var path in Directory.EnumerateFiles(root, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    onSeen?.Invoke(path);

                    DbFileRecord? rec = null;

                    try
                    {
                        if (skipDirs.Any(d => IsUnder(path, d))) { onSkip?.Invoke(path); goto AfterTry; }

                        var fi = new FileInfo(path);

                        if ((fi.Attributes & skipAttrs) != 0) { onSkip?.Invoke(path); goto AfterTry; }

                        var extLower = fi.Extension.ToLowerInvariant().TrimStart('.');
                        if (skipExts.Contains(extLower)) { onSkip?.Invoke(path); goto AfterTry; }

                        if (!hotExts.Contains(fi.Extension.ToLowerInvariant())) { onSkip?.Invoke(path); goto AfterTry; }

                        if (fi.LastWriteTimeUtc < winFrom) { onSkip?.Invoke(path); goto AfterTry; }

                        rec = new DbFileRecord
                        {
                            FileKey = FileKeyUtil.GetStableKey(fi.FullName),
                            Path = fi.FullName,
                            Parent = fi.DirectoryName,
                            Name = fi.Name,
                            Ext = fi.Extension,
                            Size = fi.Exists ? fi.Length : 0,
                            MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                            CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                            Mime = null,
                            Summary = null,
                            Snippet = null,
                            Classified = null
                        };
                    }
                    catch
                    {
                        onSkip?.Invoke(path);
                    }

                AfterTry:
                    if (rec != null)
                    {
                        yield return rec;
                        yielded++;
                        if (yielded >= s.FirstRun.HotMaxFiles)
                            yield break;

                        if ((uint)Environment.TickCount % 1024 == 0)
                            await Task.Yield();
                    }
                }
            }
        }

        private static FileAttributes GetSkippedAttributes(string[] names)
        {
            FileAttributes flags = 0;
            foreach (var n in names)
                if (Enum.TryParse<FileAttributes>(n, true, out var v)) flags |= v;
            return flags;
        }

        private static bool IsUnder(string path, string prefix)
        {
            if (prefix.Contains('*'))
            {
                var p = prefix.Replace("*", "", StringComparison.Ordinal);
                return path.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetKnownRoots()
        {
            static string OneDrive()
            {
                var p = Environment.GetEnvironmentVariable("OneDrive");
                if (!string.IsNullOrWhiteSpace(p)) return p;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
            }

            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            yield return OneDrive();
        }
    }
}

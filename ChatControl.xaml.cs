using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;                           // WPF
using System.Windows.Input;                     // WPF Input
using System.Windows.Documents;                 // Hyperlink
using Explore.Indexing;
using Explore.Search;
using Explore.Searching;
using WpfMessageBox = System.Windows.MessageBox;

namespace Explore
{
    // WPF を完全修飾
    public partial class ChatControl : System.Windows.Controls.UserControl
    {
        private readonly ObservableCollection<ChatMessage> _messages = new();
        private GeminiSearchNlp? _nlp;
        private SearchService? _search;
        private readonly CancellationTokenSource _cts = new();

        // XAML が {Binding Messages} で拾えるよう公開
        public ObservableCollection<ChatMessage> Messages => _messages;

        // XAML: <ItemsControl x:Name="ChatList">
        private System.Windows.Controls.ItemsControl? _messagesList;
        private System.Windows.Controls.ScrollViewer? _chatScrollViewer;

        // 直近の5件（openハンドラからも使う）
        private List<SearchService.SearchRow> _lastTop5 = new();

        public ChatControl()
        {
            InitializeComponent();

            if (DataContext == null) DataContext = this;

            Loaded += (_, __) =>
            {
                TryBindMessagesList();
                TryBindScrollViewer();
            };

            string dbPath = IndexDatabase.DatabasePath;
            string apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            _nlp = new GeminiSearchNlp(apiKey);
            _search = new SearchService(dbPath, _nlp.NormalizeAsync);

            _nlp.DebugLog = DebugHub.Log;
            _search.DebugLog = DebugHub.Log;

            TryBindMessagesList();
            TryBindScrollViewer();
        }

        private void TryBindMessagesList()
        {
            _messagesList =
                (FindName("ChatList") as System.Windows.Controls.ItemsControl) ??
                (FindName("MessagesList") as System.Windows.Controls.ItemsControl) ??
                (FindName("ChatListBox") as System.Windows.Controls.ItemsControl);

            if (_messagesList != null)
                _messagesList.ItemsSource = _messages;
        }

        private void TryBindScrollViewer()
        {
            _chatScrollViewer =
                (FindName("ChatScrollViewer") as System.Windows.Controls.ScrollViewer) ??
                (FindName("MessagesScrollViewer") as System.Windows.Controls.ScrollViewer);
        }

        // ===== 既存ハンドラ（XAML参照を満たすダミー含む） =====
        private void ChatToggle_Click(object sender, RoutedEventArgs e) => DebugHub.Log("[UI] ChatToggle_Click");
        private void NlpToggle_Click(object sender, RoutedEventArgs e) => DebugHub.Log("[UI] NlpToggle_Click");
        private void DebugToggle_Click(object sender, RoutedEventArgs e) => ShowDebugLogWindow();

        private async void DbCountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_search == null) return;
                var (files, fts) = await _search.GetCountsAsync(_cts.Token);
                var msg = $"DB件数: files={files:N0}, files_fts={fts:N0}";
                DebugHub.Log("[DB] " + msg);
                WpfMessageBox.Show(msg, "DB Count", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "DB Count Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RebuildButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_search == null) return;
                await _search.RebuildFtsAsync(_cts.Token);
                DebugHub.Log("[DB] files_fts rebuilt");
                WpfMessageBox.Show("FTSを再構築しました。", "Rebuild FTS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(ex.Message, "Rebuild FTS Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChatScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) { }
        private void ScrollDownButton_Click(object sender, RoutedEventArgs e)
        {
            TryBindScrollViewer();
            _chatScrollViewer?.ScrollToEnd();
        }

        private void InputFocus(object sender, RoutedEventArgs e)
        {
            var box =
                (FindName("InputBox") as System.Windows.Controls.TextBox) ??
                (FindName("ChatInput") as System.Windows.Controls.TextBox) ??
                (FindName("PromptBox") as System.Windows.Controls.TextBox);
            box?.Focus();
        }

        private void EnterSend(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SendButton_Click(sender, new RoutedEventArgs());
            }
        }
        private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => EnterSend(sender, e);

        // ===== メイン送信 =====
        private async void SendButton_Click(object? sender, RoutedEventArgs? e)
        {
            var input =
                (FindName("InputBox") as System.Windows.Controls.TextBox) ??
                (FindName("ChatInput") as System.Windows.Controls.TextBox) ??
                (FindName("PromptBox") as System.Windows.Controls.TextBox);

            var text = input?.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            AppendMessage("user", text);   // 右側バブル
            if (input != null) input.Text = "";

            try
            {
                if (_search == null)
                {
                    AppendMessage("assistant", "SearchService が初期化されていません。");
                    return;
                }

                var res = await _search.SearchAsync(text, offset: 0, _cts.Token);
                _lastTop5 = res.Top5 ?? new List<SearchService.SearchRow>();

                if (_lastTop5.Count == 0)
                {
                    AppendMessage("assistant", "該当が見つかりませんでした。");
                    return;
                }

                // ★ 文字列ではなく「カード型」メッセージを追加
                AppendSearchResults(_lastTop5);
            }
            catch (Exception ex)
            {
                AppendMessage("assistant", "エラー: " + ex.Message);
            }
        }

        // ====== 検索結果 → カード型メッセージ ======
        private void AppendSearchResults(List<SearchService.SearchRow> top5)
        {
            var list = new List<SearchResultItem>(top5.Count);
            for (int i = 0; i < top5.Count; i++)
            {
                var r = top5[i];
                var parent = System.IO.Path.GetDirectoryName(r.Path) ?? "";
                list.Add(new SearchResultItem
                {
                    Name = r.Name,
                    Path = r.Path,
                    Parent = parent,
                    SizeText = FormatSize(r.Size),
                    MtimeText = UnixToText(r.MtimeUnix),
                    Index = i + 1
                });
            }

            _messages.Add(new ChatMessage(
                role: "assistant",
                content: "",                 // 表示はテンプレート側
                isSearchResult: true,
                results: list
            ));

            TryBindScrollViewer();
            _chatScrollViewer?.ScrollToEnd();
        }

        // ====== Hyperlink: 場所を開く ======
        private async void ResultOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Hyperlink link && link.Tag is string path && !string.IsNullOrWhiteSpace(path))
                {
                    await OpenInExplorerSelectAsync(path);
                }
            }
            catch (Exception ex)
            {
                AppendMessage("assistant", "場所を開けませんでした: " + ex.Message);
            }
        }

        // ===== デバッグウィンドウ =====
        private void ShowDebugLogWindow()
        {
            var tb = new System.Windows.Controls.TextBox
            {
                Text = DebugHub.Snapshot(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = System.Windows.TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(6)
            };

            var btnCopy = new System.Windows.Controls.Button { Content = "コピー", Margin = new Thickness(0, 0, 8, 0) };
            btnCopy.Click += (_, __) => { System.Windows.Clipboard.SetText(tb.Text ?? string.Empty); };
            var btnClear = new System.Windows.Controls.Button { Content = "クリア" };
            btnClear.Click += (_, __) => { DebugHub.Clear(); tb.Text = ""; };

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(6, 6, 6, 0)
            };
            buttons.Children.Add(btnCopy);
            buttons.Children.Add(btnClear);

            var panel = new System.Windows.Controls.DockPanel { LastChildFill = true };
            System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Top);
            panel.Children.Add(buttons);
            panel.Children.Add(tb);

            var win = new Window
            {
                Title = "Debug Log",
                Width = 900,
                Height = 600,
                Content = panel,
                Owner = Window.GetWindow(this)
            };
            win.Show();
        }

        // ===== ユーティリティ =====
        private static string UnixToText(long sec)
        {
            if (sec <= 0) return "—";
            try { return DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime.ToString("yyyy/MM/dd HH:mm"); }
            catch { return "—"; }
        }

        private static string FormatSize(long size)
        {
            if (size <= 0) return "—";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = size; int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.#} {units[u]}";
        }

        private void AppendMessage(string role, string content)
        {
            _messages.Add(new ChatMessage(role, content));
            TryBindScrollViewer();
            _chatScrollViewer?.ScrollToEnd();
        }

        // Explorer で選択表示
        private static Task OpenInExplorerSelectAsync(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return Task.CompletedTask;

            string args;
            if (System.IO.File.Exists(fullPath)) args = "/select,\"" + fullPath + "\"";
            else if (System.IO.Directory.Exists(fullPath)) args = "\"" + fullPath + "\"";
            else throw new System.IO.FileNotFoundException("ファイル/フォルダが見つかりません。", fullPath);

            var psi = new ProcessStartInfo { FileName = "explorer.exe", Arguments = args, UseShellExecute = true };
            Process.Start(psi);
            return Task.CompletedTask;
        }
    }

    // ===== モデル =====
    public sealed class ChatMessage
    {
        public string Role { get; }
        public string Content { get; }                  // 通常のテキストに使用
        public bool IsSearchResult { get; }             // 検索結果メッセージか
        public IReadOnlyList<SearchResultItem>? Results { get; }

        public ChatMessage(string role, string content, bool isSearchResult = false, IReadOnlyList<SearchResultItem>? results = null)
        {
            Role = role;
            Content = content;
            IsSearchResult = isSearchResult;
            Results = results;
        }
    }

    public sealed class SearchResultItem
    {
        public int Index { get; set; } = 0;             // 1..5
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Parent { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string MtimeText { get; set; } = "";
    }

    // ===== DataTemplateSelector =====
    public sealed class ChatTemplateSelector : System.Windows.Controls.DataTemplateSelector
    {
        public System.Windows.DataTemplate? UserTemplate { get; set; }
        public System.Windows.DataTemplate? AssistantTemplate { get; set; }
        public System.Windows.DataTemplate? SearchTemplate { get; set; }

        public override System.Windows.DataTemplate? SelectTemplate(object item, System.Windows.DependencyObject container)
        {
            if (item is ChatMessage m)
            {
                if (m.IsSearchResult && SearchTemplate != null) return SearchTemplate;
                if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) return UserTemplate;
                return AssistantTemplate ?? UserTemplate;
            }
            return base.SelectTemplate(item, container);
        }
    }
}

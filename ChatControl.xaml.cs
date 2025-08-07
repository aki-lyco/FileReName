using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Explore
{
    /// <summary>
    /// ChatControl.xaml の相互作用ロジック
    /// </summary>
    public partial class ChatControl : System.Windows.Controls.UserControl
    {
        private ObservableCollection<ChatMessage> Messages = new();
        private byte scrolling = 0;
        public ChatControl()
        {
            InitializeComponent();
            ChatList.ItemsSource = Messages;
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // ユーザーメッセージ追加
            Messages.Add(new ChatMessage { Role = "user", Content = text });
            InputBox.Clear();

            // 仮：AIの応答（後で本物に置き換え可能）
            Messages.Add(new ChatMessage { Role = "assistant", Content = "これは仮の応答です。" });

            ScrollToBottom();
        }

        private async void ScrollToBottom()
        {
            scrolling++;
            ScrollDownButton.Visibility = Visibility.Collapsed;

            await Dispatcher.Yield(DispatcherPriority.Background);
            await AnimateScrollToBottomAsync();

            scrolling--;
        }

        private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            bool isAtBottom = ChatScrollViewer.VerticalOffset >= ChatScrollViewer.ScrollableHeight - 1;

            ScrollDownButton.Visibility = (isAtBottom || scrolling != 0) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task AnimateScrollToBottomAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            double from = ChatScrollViewer.VerticalOffset;
            double to = ChatScrollViewer.ScrollableHeight;
            const double duration = 300; // ミリ秒
            DateTime start = DateTime.Now;

            EventHandler handler = null!;
            handler = (s, e) =>
            {
                double elapsed = (DateTime.Now - start).TotalMilliseconds;
                double progress = Math.Min(elapsed / duration, 1.0);
                double eased = 1 - Math.Pow(1 - progress, 3); // Ease-out cubic

                double offset = from + (to - from) * eased;
                ChatScrollViewer.ScrollToVerticalOffset(offset);

                if (progress >= 1.0)
                {
                    CompositionTarget.Rendering -= handler;
                    tcs.TrySetResult(true);
                }
            };

            CompositionTarget.Rendering += handler;

            await tcs.Task;
        }

        private void ScrollDownButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToBottom();
        }

        private void InputFocus(object sender, MouseButtonEventArgs e)
        {
            InputBox.Focus();
        }

        private void EnterSend(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                SendButton_Click(null!, null!);
            }
        }
    }

#pragma warning disable CS8618

    public class ChatMessage
    {
        public string Role { get; set; }  // "user" または "assistant"
        public string Content { get; set; }
    }

    public class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate AITemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage msg)
            {
                return msg.Role == "user" ? UserTemplate : AITemplate;
            }
            return base.SelectTemplate(item, container);
        }
    }

#pragma warning restore CS8618

}

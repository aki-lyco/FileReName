using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Explore.Build;

namespace Explore
{
    public class RenameViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<FileRenameItem> Items { get; } = new();

        private string _statusText = "準備完了";
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }

        private readonly IRenameAIProvider _ai;

        public RenameViewModel()
        {
            // ← 左項を IRenameAIProvider? にキャストして ?? を成立させる
            _ai = (IRenameAIProvider?)GeminiRenameProvider.TryCreateFromEnv() ?? new HeuristicAIProvider();

            StatusText = _ai is GeminiRenameProvider
                ? "準備完了（AI: Gemini Vision 有効）"
                : "準備完了（AI: オフ/ヒューリスティック）";
        }

        // ---- View から呼ばれる公開メソッド ----

        public void AddFiles(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                if (Items.Any(i => string.Equals(i.OriginalFullPath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fi = new FileInfo(path);
                var item = new FileRenameItem
                {
                    OriginalFullPath = path,
                    OriginalName = fi.Name,
                    DirectoryPath = fi.DirectoryName ?? "",
                    Extension = fi.Extension,
                    Apply = true
                };
                HookItem(item);
                Items.Add(item);
                added++;
            }
            UpdatePreviews();
            StatusText = $"ファイルを読み込みました：{added} 件";
        }

        public async Task SuggestAsync()
        {
            if (Items.Count == 0) { StatusText = "項目がありません"; return; }

            bool usingGemini = _ai is GeminiRenameProvider;
            StatusText = usingGemini ? "AI に問い合わせ中..." : "候補を作成中...";

            try
            {
                foreach (var item in Items)
                {
                    var path = item.OriginalFullPath;

                    if (ImageInfoExtractor.LooksLikeImage(path))
                    {
                        // 画像：EXIF等の要約（ヒント）
                        item.ContentSample = await SafeExtractImageHintAsync(path);
                    }
                    else
                    {
                        // テキスト類：本文抽出
                        item.ContentSample = await SafeExtractAsync(path, 8 * 1024);
                    }
                }

                await _ai.SuggestAsync(Items);

                UpdatePreviews();
                StatusText = usingGemini
                    ? "候補を用意しました。（AI: Gemini Vision 利用）"
                    : "候補を用意しました。（AI: オフ/ヒューリスティック）";
            }
            catch (Exception ex)
            {
                StatusText = "候補作成に失敗しました：" + ex.Message +
                             (usingGemini ? "（AI呼び出し失敗につきフォールバックの可能性）" : "");
            }
        }

        public void SelectAll()
        {
            foreach (var i in Items) i.Apply = true;
            OnCollectionChanged();
            StatusText = "すべてにチェックを入れました。";
        }

        public async Task ApplyAsync()
        {
            if (Items.Count == 0) { StatusText = "項目がありません"; return; }

            int ok = 0, skip = 0, fail = 0;
            foreach (var item in Items.Where(x => x.Apply))
            {
                try
                {
                    var newName = string.IsNullOrWhiteSpace(item.SuggestedName)
                        ? item.OriginalName
                        : EnsureExtension(item.SuggestedName, item.Extension);

                    newName = SanitizeFileName(newName);
                    var target = GetUniquePath(item.DirectoryPath, newName);

                    if (string.Equals(item.OriginalFullPath, target, StringComparison.OrdinalIgnoreCase))
                    {
                        skip++;
                        continue;
                    }

                    File.Move(item.OriginalFullPath, target);
                    item.OriginalFullPath = target;
                    item.OriginalName = Path.GetFileName(target);
                    ok++;
                }
                catch
                {
                    fail++;
                }
            }
            UpdatePreviews();
            StatusText = $"適用完了：成功={ok}, 変更なし={skip}, 失敗={fail}";
            await Task.CompletedTask;
        }

        // ▼ 追加：AI状態確認
        public async Task CheckAIAsync()
        {
            if (_ai is GeminiRenameProvider g)
            {
                StatusText = "AI接続確認中…";
                var ok = await g.PingAsync();
                StatusText = ok
                    ? $"AIは有効です（モデル: {g.Model}）"
                    : "AIキーは検出されましたが応答がありません（ネットワーク/モデル名/プロキシをご確認ください）";
            }
            else
            {
                StatusText = "AIは無効です（ヒューリスティックのみ）。APIキーを設定してください。";
            }
        }

        // Helpers
        private void HookItem(FileRenameItem item)
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FileRenameItem.SuggestedName))
                    UpdatePreviews();
            };
        }

        private static async Task<string> SafeExtractAsync(string path, int maxBytes)
        {
            try { return await TextExtractor.ExtractAsync(path, maxBytes); }
            catch { return ""; }
        }

        private static async Task<string> SafeExtractImageHintAsync(string path)
        {
            try { return await ImageInfoExtractor.ExtractSummaryAsync(path); }
            catch { return ""; }
        }

        private void UpdatePreviews()
        {
            foreach (var item in Items)
            {
                var suggested = string.IsNullOrWhiteSpace(item.SuggestedName)
                    ? item.OriginalName
                    : EnsureExtension(item.SuggestedName, item.Extension);

                suggested = SanitizeFileName(suggested);
                item.PreviewFullPath = GetUniquePath(item.DirectoryPath, suggested);
            }
            OnCollectionChanged();
        }

        private static string EnsureExtension(string name, string ext)
        {
            if (string.IsNullOrEmpty(ext)) return name;
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name;
            return Path.GetFileNameWithoutExtension(name) + ext;
        }

        private static string GetUniquePath(string dir, string fileName)
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) return path;

            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            int i = 2;
            while (true)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
                i++;
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);

            var cleaned = sb.ToString();
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            cleaned = Regex.Replace(cleaned, @"_{2,}", "_");
            cleaned = cleaned.Trim(' ', '.');
            return string.IsNullOrWhiteSpace(cleaned) ? "untitled" : cleaned;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private void OnCollectionChanged() => OnPropertyChanged(nameof(Items));
    }

    // ---- Model ----
    public class FileRenameItem : INotifyPropertyChanged
    {
        private bool _apply;
        private string _suggestedName = "";
        private string _previewFullPath = "";

        public string OriginalFullPath { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string Extension { get; set; } = "";
        public string ContentSample { get; set; } = "";

        public bool Apply { get => _apply; set { _apply = value; OnPropertyChanged(nameof(Apply)); } }
        public string SuggestedName { get => _suggestedName; set { _suggestedName = value; OnPropertyChanged(nameof(SuggestedName)); } }
        public string PreviewFullPath { get => _previewFullPath; set { _previewFullPath = value; OnPropertyChanged(nameof(PreviewFullPath)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ---- ヒューリスティック（AIオフ時のフォールバック） ----
    public interface IRenameAIProvider
    {
        Task SuggestAsync(ObservableCollection<FileRenameItem> items);
    }

    public class HeuristicAIProvider : IRenameAIProvider
    {
        public Task SuggestAsync(ObservableCollection<FileRenameItem> items)
        {
            foreach (var item in items)
            {
                var name = Path.GetFileNameWithoutExtension(item.OriginalName);
                name = Regex.Replace(name, @"[_\-]{2,}", "_");
                name = Regex.Replace(name, @"\s{2,}", " ");
                name = name.Trim('_', '-', ' ', '.');
                item.SuggestedName = string.IsNullOrWhiteSpace(name) ? "untitled" : name;
            }
            return Task.CompletedTask;
        }
    }

    // ---- コマンド（他VM互換） ----
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        { _execute = _ => execute(); _canExecute = canExecute != null ? new Func<object?, bool>(_ => canExecute()) : null; }
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isExecuting;
        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        { _executeAsync = _ => executeAsync(); _canExecute = canExecute != null ? new Func<object?, bool>(_ => canExecute()) : null; }
        public AsyncRelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
        { _executeAsync = executeAsync ?? new Func<object?, Task>(_ => Task.CompletedTask); _canExecute = canExecute; }
        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
        public async void Execute(object? parameter)
        { if (_isExecuting) return; _isExecuting = true; RaiseCanExecuteChanged(); try { await _executeAsync(parameter); } finally { _isExecuting = false; RaiseCanExecuteChanged(); } }
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

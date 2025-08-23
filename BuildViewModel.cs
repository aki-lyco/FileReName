using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Explore.FileSystem; // NotifyBase, RelayCommand, PathUtils など

namespace Explore.Build
{
    public enum BuildState { SelectTargets, Design, DryRunPreview, Applying, Done }

    public sealed class BuildViewModel : NotifyBase
    {
        // ===== UI services =====
        public Func<string?>? PickFolder { get; set; }
        public Func<(string[] files, string? folder)?>? PickFilesOrFolder { get; set; }
        public Action<string, string>? ShowMessage { get; set; }

        // ===== State =====
        private BuildState _state = BuildState.SelectTargets;
        public BuildState State { get => _state; set => Set(ref _state, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RefreshCommands(); } }

        // 1) 対象
        public ObservableCollection<string> SelectedTargets { get; } = new();

        // 2) 保存先 & ツリー
        private string? _classificationBasePath;
        public string? ClassificationBasePath
        {
            get => _classificationBasePath;
            set { if (Set(ref _classificationBasePath, value)) { Raise(nameof(IsBasePicked)); Raise(nameof(BaseDisplay)); } }
        }
        public bool IsBasePicked => !string.IsNullOrWhiteSpace(ClassificationBasePath);
        public string BaseDisplay => string.IsNullOrWhiteSpace(ClassificationBasePath) ? "(未選択)" : ClassificationBasePath!;

        private CategoryNode _root = CategoryNode.UncategorizedRoot();
        public CategoryNode Root { get => _root; set => Set(ref _root, value); }

        private CategoryNode? _selectedCategory;
        public CategoryNode? SelectedCategory { get => _selectedCategory; set => Set(ref _selectedCategory, value); }

        // 3) IO
        private string? _lastCategoriesJsonPath;
        public string? LastCategoriesJsonPath { get => _lastCategoriesJsonPath; set => Set(ref _lastCategoriesJsonPath, value); }

        // 右ペイン
        private double _aiThreshold = 0.55;
        public double AiThreshold { get => _aiThreshold; set => Set(ref _aiThreshold, value); }

        // ★ 通知つきプロパティに変更（UI 反映のため）
        private string? _testFilePath;
        public string? TestFilePath
        {
            get => _testFilePath;
            set => Set(ref _testFilePath, value);
        }

        public string Status { get; set; } = "";

        // BLOCK F: 計画
        private BuildPlan? _plan;
        public BuildPlan? Plan { get => _plan; set => Set(ref _plan, value); }

        // BLOCK G: 適用
        private CancellationTokenSource? _applyCts;
        private PlanApplier? _applier;

        // Commands
        public ICommand DecideTargets { get; }
        public ICommand PickBasePath { get; }
        public ICommand AutoImportFromBase { get; }
        public ICommand AddRootCategory { get; }
        public ICommand AddCategory { get; }
        public ICommand RenameCategory { get; }
        public ICommand RemoveCategory { get; }
        public ICommand MoveCategory { get; }
        public ICommand SaveCategories { get; }
        public ICommand LoadCategories { get; }
        public ICommand ExportCategories { get; }
        public ICommand ValidateCategories { get; }
        public ICommand RunDryRun { get; }
        public ICommand BackToDesign { get; }
        public ICommand BackToSelectTargets { get; }
        public ICommand OpenCategoryInExplorer { get; }

        // BLOCK G: 適用＆Undo
        public ICommand ApplyPlan { get; }
        public ICommand UndoAll { get; }

        public BuildViewModel()
        {
            DecideTargets = new RelayCommand(async () => await OnDecideTargetsAsync(), () => !IsBusy);
            PickBasePath = new RelayCommand(OnPickBasePath, () => !IsBusy);
            AutoImportFromBase = new RelayCommand(async () => await OnAutoImportAsync(), () => !IsBusy);
            AddRootCategory = new RelayCommand(OnAddRootCategory, () => !IsBusy);
            AddCategory = new RelayCommand(OnAddCategory, () => SelectedCategory != null && !IsBusy);
            RenameCategory = new RelayCommand(OnRenameCategory, () => SelectedCategory != null && !(SelectedCategory?.IsRequired ?? false) && !IsBusy);
            RemoveCategory = new RelayCommand(OnRemoveCategory, () => SelectedCategory != null && !(SelectedCategory?.IsRequired ?? false) && !IsBusy);
            MoveCategory = new RelayCommand(OnMoveCategory, () => !IsBusy);
            SaveCategories = new RelayCommand(async () => await OnSaveAsync(), () => !IsBusy);
            LoadCategories = new RelayCommand(async () => await OnLoadAsync(), () => !IsBusy);
            ExportCategories = new RelayCommand(async () => await OnExportAsync(), () => !IsBusy);
            ValidateCategories = new RelayCommand(OnValidate, () => !IsBusy);
            RunDryRun = new RelayCommand(OnRunDryRun, () => !IsBusy);
            BackToDesign = new RelayCommand(() => State = BuildState.Design, () => !IsBusy);
            BackToSelectTargets = new RelayCommand(OnBackToSelectTargets, () => !IsBusy);
            OpenCategoryInExplorer = new RelayCommand(OnOpenInExplorer, () => !IsBusy);

            ApplyPlan = new RelayCommand(async () => await OnApplyAsync(), () => !IsBusy && Plan != null);
            UndoAll = new RelayCommand(async () => await OnUndoAsync(), () => !IsBusy && _applier != null);
        }

        private void RefreshCommands()
        {
            (DecideTargets as RelayCommand)?.RaiseCanExecuteChanged();
            (PickBasePath as RelayCommand)?.RaiseCanExecuteChanged();
            (AutoImportFromBase as RelayCommand)?.RaiseCanExecuteChanged();
            (AddRootCategory as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCategory as RelayCommand)?.RaiseCanExecuteChanged();
            (RenameCategory as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveCategory as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveCategory as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveCategories as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadCategories as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportCategories as RelayCommand)?.RaiseCanExecuteChanged();
            (ValidateCategories as RelayCommand)?.RaiseCanExecuteChanged();
            (RunDryRun as RelayCommand)?.RaiseCanExecuteChanged();
            (BackToDesign as RelayCommand)?.RaiseCanExecuteChanged();
            (BackToSelectTargets as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenCategoryInExplorer as RelayCommand)?.RaiseCanExecuteChanged();

            (ApplyPlan as RelayCommand)?.RaiseCanExecuteChanged();
            (UndoAll as RelayCommand)?.RaiseCanExecuteChanged();
        }

        // 次へ（SelectTargets -> Design）
        private async Task OnDecideTargetsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                if (SelectedTargets.Count == 0)
                {
                    var res = System.Windows.MessageBox.Show(
                        "分類対象が未選択です。このまま設計モードへ進みますか？",
                        "確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                    if (res != System.Windows.MessageBoxResult.Yes) return;
                }

                State = BuildState.Design;
                SelectedCategory ??= Root.Children.FirstOrDefault();

                // 初回だけ自動取り込み
                bool onlyUncategorized = Root.Children.Count == 1 && Root.Children[0].IsRequired;
                if (IsBasePicked && onlyUncategorized)
                {
                    Status = "保存先のフォルダ構造を読み込み中…";
                    var nodes = await CategoriesRepository.ImportFromBaseAsync(ClassificationBasePath!, CancellationToken.None);
                    MergeIntoTree(nodes);
                    SelectedCategory = Root.FlattenTree().FirstOrDefault(n => !n.IsRequired) ?? Root.Children.FirstOrDefault();
                    Status = "保存先のフォルダ構造を取り込みました。";
                }
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("エラー", $"設計モードの初期化でエラー: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        // 設計 -> 対象選択
        private void OnBackToSelectTargets()
        {
            if (HasUnsavedChanges())
            {
                var ok = System.Windows.MessageBox.Show(
                    "設計内容に未保存の変更があります。対象選択に戻りますか？（変更は保持されますが、保存はされていません）",
                    "確認",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (ok != System.Windows.MessageBoxResult.Yes) return;
            }
            State = BuildState.SelectTargets;
        }

        private void OnPickBasePath()
        {
            if (PickFolder == null) { ShowMessage?.Invoke("エラー", "フォルダ選択サービスが未設定です。"); return; }
            var picked = PickFolder.Invoke();
            if (string.IsNullOrWhiteSpace(picked)) return;

            if (HasUnsavedChanges())
            {
                if (System.Windows.MessageBox.Show("未保存の変更があります。ベースを切り替えますか？", "確認",
                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
                    return;
            }

            ClassificationBasePath = picked;
            Root = CategoryNode.UncategorizedRoot();
            SelectedCategory = Root.Children.FirstOrDefault();
            Status = $"保存先を設定: {picked}";
        }

        private async Task OnAutoImportAsync()
        {
            if (!IsBasePicked) { ShowMessage?.Invoke("警告", "まず保存先ベースを選択してください。"); return; }
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                Status = "保存先のフォルダ構造を読み込み中…";
                var nodes = await CategoriesRepository.ImportFromBaseAsync(ClassificationBasePath!, CancellationToken.None);
                MergeIntoTree(nodes);
                SelectedCategory ??= Root.FlattenTree().FirstOrDefault(n => !n.IsRequired) ?? Root.Children.FirstOrDefault();
                Status = $"自動取り込み: {nodes.Count} 件のカテゴリを反映しました。";
            }
            catch (Exception ex) { ShowMessage?.Invoke("エラー", $"自動取り込みに失敗しました: {ex.Message}"); }
            finally { IsBusy = false; }
        }

        // ルート直下に新規（★Displayは空、RelPathは自動採番で後から付与）
        private void OnAddRootCategory()
        {
            var rel = PathUtils.JoinRel("", "NewCategory");
            var node = new CategoryNode(rel, /*Display*/"", new ObservableCollection<CategoryNode>(), false);
            Root.Children.Add(node);
            SelectedCategory = node;
            // 表示名編集後に RecomputeRelPathsFromDisplay() で一括再計算
        }

        // 選択ノードの子に追加（★Displayは空、RelPathは後で一括再計算）
        private void OnAddCategory()
        {
            var parent = SelectedCategory ?? Root.Children.FirstOrDefault();
            if (parent == null) { OnAddRootCategory(); return; }
            if (parent.IsRequired) { OnAddRootCategory(); return; }

            var rel = PathUtils.JoinRel(parent.RelPath ?? "", "NewCategory");
            var node = new CategoryNode(rel, /*Display*/"", new ObservableCollection<CategoryNode>(), false);
            parent.Children.Add(node);
            SelectedCategory = node;
        }

        private void OnRenameCategory()
        {
            if (SelectedCategory == null || SelectedCategory.IsRequired) return;

            // Display は UI で編集済み想定。RelPath はユーザー値を使わず全体再計算で決める。
            RecomputeRelPathsFromDisplay();
        }

        private void OnRemoveCategory()
        {
            if (SelectedCategory == null) return;
            if (SelectedCategory.IsRequired) { ShowMessage?.Invoke("エラー", "未分類は削除できません。"); return; }
            Root.RemoveByRelPath(SelectedCategory.RelPath);
            SelectedCategory = null;
        }

        private void OnMoveCategory()
        {
            if (SelectedCategory == null || SelectedCategory.IsRequired) return;

            var target = SelectedCategory;
            Root.RemoveByRelPath(target.RelPath);
            Root.Children.Add(target);
            SelectedCategory = target;

            // 移動後に一括再計算
            RecomputeRelPathsFromDisplay();
        }

        private async Task OnSaveAsync()
        {
            if (!IsBasePicked) { ShowMessage?.Invoke("警告", "保存先ベースを選択してください。"); return; }

            // ★RelPath は常にプログラムが再計算（Display → 安全名、一意化）
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count > 0 || duplicates.Count > 0)
            {
                ShowMessage?.Invoke("エラー", string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"重複: {d}")).Take(10)));
                return;
            }

            var dst = LastCategoriesJsonPath;
            if (string.IsNullOrWhiteSpace(dst))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName");
                Directory.CreateDirectory(dir);
                dst = Path.Combine(dir, "categories.json");
            }

            try
            {
                await CategoriesRepository.SaveAsync(ClassificationBasePath!, Root.Children, dst!, CancellationToken.None);
                LastCategoriesJsonPath = dst;
                Status = $"保存しました: {dst}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("エラー", $"保存に失敗しました: {ex.Message}"); }
        }

        private async Task OnLoadAsync()
        {
            var src = LastCategoriesJsonPath;
            if (string.IsNullOrWhiteSpace(src))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName");
                src = Path.Combine(dir, "categories.json");
            }
            try
            {
                var list = await CategoriesRepository.LoadAsync(src!, CancellationToken.None);
                var newRoot = CategoryNode.UncategorizedRoot();
                foreach (var n in list) foreach (var child in n.Children.ToList()) newRoot.Children.Add(child);
                Root = newRoot;
                SelectedCategory = Root.FlattenTree().FirstOrDefault(n => !n.IsRequired) ?? Root.Children.FirstOrDefault();

                // 読み込み後も規則で RelPath を揃える
                RecomputeRelPathsFromDisplay();

                Status = $"読み込み完了: {src}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("エラー", $"読み込みに失敗しました: {ex.Message}"); }
        }

        private async Task OnExportAsync()
        {
            try
            {
                // 書き出し前に規則で整形
                RecomputeRelPathsFromDisplay();

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName");
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, $"categories_export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                await CategoriesRepository.SaveAsync(ClassificationBasePath ?? "", Root.Children, dst, CancellationToken.None);
                Status = $"エクスポートしました: {dst}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("エラー", $"エクスポートに失敗しました: {ex.Message}"); }
        }

        private void OnValidate()
        {
            // 規則で揃えたうえでチェック
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count == 0 && duplicates.Count == 0)
            {
                ShowMessage?.Invoke("OK", "カテゴリは妥当です。");
                Status = "検証: OK";
            }
            else
            {
                var msg = string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"重複: {d}")));
                System.Windows.MessageBox.Show(msg, "検証エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                Status = "検証: エラーあり";
            }
        }

        // ★Root 自体（仮想ルート）と必須ノードをスキップして検証
        private List<string> ValidateAll(out List<string> duplicates)
        {
            var errors = new List<string>();
            duplicates = new List<string>();
            var rels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in Root.FlattenTree())
            {
                if (ReferenceEquals(node, Root)) continue;      // 仮想ルート除外
                if (node.IsRequired) continue;                  // 未分類などの必須は除外

                if (string.IsNullOrWhiteSpace(node.RelPath))
                {
                    errors.Add($"空のRelPath: {node.Display}");
                    continue;
                }
                var norm = PathUtils.NormalizeRelPath(node.RelPath);

                if (!PathUtils.IsValidRelPath(norm))
                    errors.Add($"無効なRelPath: {node.RelPath}");

                if (!rels.Add(norm))
                    duplicates.Add(norm);
            }
            return errors;
        }

        // ★Display から RelPath を一括再計算（同名は _1, _2… で一意化）
        private void RecomputeRelPathsFromDisplay()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Walk(CategoryNode node, string parentRel)
            {
                if (ReferenceEquals(node, Root))
                {
                    foreach (var ch in node.Children) Walk(ch, "");
                    return;
                }

                if (node.IsRequired)
                {
                    // 必須ノードは既定の RelPath を尊重
                    var fixedRel = string.IsNullOrWhiteSpace(node.RelPath) ? "_Uncategorized" : node.RelPath!;
                    taken.Add(fixedRel);
                    foreach (var ch in node.Children) Walk(ch, fixedRel);
                    return;
                }

                var safe = PathUtils.MakeSafeName(string.IsNullOrWhiteSpace(node.Display) ? "NewCategory" : node.Display);
                if (string.IsNullOrWhiteSpace(safe)) safe = "NewCategory";

                var baseRel = PathUtils.JoinRel(parentRel, safe);
                var rel = baseRel;
                int i = 1;
                while (!string.IsNullOrEmpty(rel) && taken.Contains(rel))
                    rel = PathUtils.JoinRel(parentRel, $"{safe}_{i++}");

                node.RelPath = rel;
                taken.Add(rel);

                foreach (var ch in node.Children) Walk(ch, rel);
            }

            Walk(Root, "");
            Raise(nameof(Root));
        }

        // ===== BLOCK F: ドライラン（AI分類 → Plan生成） =====
        private async void OnRunDryRun()
        {
            if (!IsBasePicked)
            {
                ShowMessage?.Invoke("警告", "保存先ベースを選択してください。");
                return;
            }

            // ★RelPath を毎回プログラムで確定
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count > 0 || duplicates.Count > 0)
            {
                var first = string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"重複: {d}")).Take(5));
                ShowMessage?.Invoke("警告", "バリデーションエラーがあります:" + Environment.NewLine + first);
                return;
            }

            if (IsBusy) return;
            IsBusy = true;
            try
            {
                Status = "AI 分類（ドライラン）を実行中…";
                var ct = CancellationToken.None;

                var plan = await PlanBuilder.MakePlanAsync(
                    SelectedTargets.ToList(),
                    Root,
                    ClassificationBasePath!,
                    ct,
                    classifier: new GeminiClassifier(),
                    thresholdOverride: AiThreshold
                );

                Plan = plan;
                State = BuildState.DryRunPreview; // プレビューへ
                Status = $"Plan: 作成 {plan.Stats.CreateCount} / 移動 {plan.Stats.MoveCount} / 未分類 {plan.Stats.UnresolvedCount} / エラー {plan.Stats.ErrorCount}";
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("エラー", $"ドライラン失敗: {ex.Message}");
            }
            finally { IsBusy = false; RefreshCommands(); }
        }

        // ===== BLOCK G: 適用 =====
        private async Task OnApplyAsync()
        {
            if (Plan == null || !IsBasePicked) { ShowMessage?.Invoke("警告", "Plan がありません。"); return; }
            if (IsBusy) return;
            IsBusy = true;

            _applyCts?.Cancel();
            _applyCts = new CancellationTokenSource();
            _applier = new PlanApplier(ClassificationBasePath!);

            try
            {
                State = BuildState.Applying;
                Status = "適用中…";

                var prog = new Progress<ApplyProgress>(p =>
                {
                    Status = $"{p.Phase}: {p.Done}/{p.Total} {p.Current} (Errors:{p.Errors})";
                });

                await _applier.ApplyAsync(Plan, prog, _applyCts.Token);

                // ★適用成功後は初期化して対象選択へ戻る
                ResetForNewSession();
                State = BuildState.SelectTargets;
                Status = "適用完了。対象選択画面に戻りました。";
            }
            catch (OperationCanceledException)
            {
                Status = "キャンセルしました";
                State = BuildState.Design;
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("エラー", $"適用中に失敗: {ex.Message}");
                State = BuildState.Design;
            }
            finally { IsBusy = false; RefreshCommands(); }
        }

        private async Task OnUndoAsync()
        {
            if (_applier == null) return;
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                Status = "Undo 実行中…";
                await _applier.UndoAllAsync();
                Status = "Undo 完了";
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("エラー", $"Undo 失敗: {ex.Message}");
            }
            finally { IsBusy = false; RefreshCommands(); }
        }

        private void OnOpenInExplorer()
        {
            if (SelectedCategory == null || string.IsNullOrWhiteSpace(ClassificationBasePath)) return;
            var abs = PathUtils.CombineBase(ClassificationBasePath!, SelectedCategory.RelPath);
            try
            {
                Directory.CreateDirectory(abs);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo() { FileName = abs, UseShellExecute = true });
            }
            catch (Exception ex) { ShowMessage?.Invoke("エラー", $"フォルダを開けませんでした: {ex.Message}"); }
        }

        private bool HasUnsavedChanges()
        {
            return Root.Children.Count > 1 || (Root.Children.Count == 1 && Root.Children[0].Children.Count > 0);
        }

        private void MergeIntoTree(IReadOnlyList<CategoryNode> imported)
        {
            var root = Root;
            var existing = new HashSet<string>(root.FlattenRelPaths(), StringComparer.OrdinalIgnoreCase);
            foreach (var node in imported.SelectMany(n => n.Children))
            {
                foreach (var flat in node.FlattenTree())
                {
                    if (flat.IsRequired) continue;
                    if (string.IsNullOrWhiteSpace(flat.RelPath)) continue;
                    var norm = PathUtils.NormalizeRelPath(flat.RelPath);
                    if (!existing.Contains(norm))
                    {
                        root.AddByRelPath(norm, flat.Display, isRequired: false, flat.KeywordsJson, flat.ExtFilter, flat.AiHint);
                        existing.Add(norm);
                    }
                }
            }
            Raise(nameof(Root));
        }

        // ★ 適用完了後のセッション初期化
        private void ResetForNewSession()
        {
            Plan = null;              // PropertyChanged が飛ぶ
            SelectedTargets.Clear();
            TestFilePath = null;
        }
    }
}

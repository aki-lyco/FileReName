using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Explore.FileSystem; // NotifyBase, RelayCommand, PathUtils �Ȃ�

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

        // 1) �Ώ�
        public ObservableCollection<string> SelectedTargets { get; } = new();

        // 2) �ۑ��� & �c���[
        private string? _classificationBasePath;
        public string? ClassificationBasePath
        {
            get => _classificationBasePath;
            set { if (Set(ref _classificationBasePath, value)) { Raise(nameof(IsBasePicked)); Raise(nameof(BaseDisplay)); } }
        }
        public bool IsBasePicked => !string.IsNullOrWhiteSpace(ClassificationBasePath);
        public string BaseDisplay => string.IsNullOrWhiteSpace(ClassificationBasePath) ? "(���I��)" : ClassificationBasePath!;

        private CategoryNode _root = CategoryNode.UncategorizedRoot();
        public CategoryNode Root { get => _root; set => Set(ref _root, value); }

        private CategoryNode? _selectedCategory;
        public CategoryNode? SelectedCategory { get => _selectedCategory; set => Set(ref _selectedCategory, value); }

        // 3) IO
        private string? _lastCategoriesJsonPath;
        public string? LastCategoriesJsonPath { get => _lastCategoriesJsonPath; set => Set(ref _lastCategoriesJsonPath, value); }

        // �E�y�C��
        private double _aiThreshold = 0.55;
        public double AiThreshold { get => _aiThreshold; set => Set(ref _aiThreshold, value); }

        // �\���p
        private string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }

        // �e�X�g�p
        private string? _testFilePath;
        public string? TestFilePath { get => _testFilePath; set => Set(ref _testFilePath, value); }

        // BLOCK F: �v��
        private BuildPlan? _plan;
        public BuildPlan? Plan { get => _plan; set => Set(ref _plan, value); }

        // BLOCK G: �K�p
        private CancellationTokenSource? _applyCts;
        private PlanApplier? _applier;

        // ===== DryRun �v���r���[�p =====
        private int _dryNew, _dryMove, _dryUnres, _dryErr;
        public int DryNewFolders { get => _dryNew; private set => Set(ref _dryNew, value); }
        public int DryMoves { get => _dryMove; private set => Set(ref _dryMove, value); }
        public int DryUnresolved { get => _dryUnres; private set => Set(ref _dryUnres, value); }
        public int DryErrors { get => _dryErr; private set => Set(ref _dryErr, value); }

        public ObservableCollection<DryNode> DryTree { get; } = new();
        public ObservableCollection<DryIssue> DryUnresolvedList { get; } = new();
        public ObservableCollection<DryIssue> DryErrorsList { get; } = new();

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

        // BLOCK G: �K�p��Undo
        public ICommand ApplyPlan { get; }
        public ICommand UndoAll { get; }

        // DryRun�F�R�s�[
        public ICommand CopyPlanSummary { get; }

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

            CopyPlanSummary = new RelayCommand(OnCopyPlanSummary, () => Plan != null && State == BuildState.DryRunPreview);
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
            (CopyPlanSummary as RelayCommand)?.RaiseCanExecuteChanged();
        }

        // ���ցiSelectTargets -> Design�j
        private async Task OnDecideTargetsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                if (SelectedTargets.Count == 0)
                {
                    var res = System.Windows.MessageBox.Show(
                        "���ޑΏۂ����I���ł��B���̂܂ܐ݌v���[�h�֐i�݂܂����H",
                        "�m�F", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                    if (res != System.Windows.MessageBoxResult.Yes) return;
                }

                State = BuildState.Design;
                SelectedCategory ??= Root.Children.FirstOrDefault();

                // ���񂾂�������荞��
                bool onlyUncategorized = Root.Children.Count == 1 && Root.Children[0].IsRequired;
                if (IsBasePicked && onlyUncategorized)
                {
                    Status = "�ۑ���̃t�H���_�\����ǂݍ��ݒ��c";
                    var nodes = await CategoriesRepository.ImportFromBaseAsync(ClassificationBasePath!, CancellationToken.None);
                    MergeIntoTree(nodes);
                    SelectedCategory = Root.FlattenTree().FirstOrDefault(n => !n.IsRequired) ?? Root.Children.FirstOrDefault();
                    Status = "�ۑ���̃t�H���_�\������荞�݂܂����B";
                }
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("�G���[", $"�݌v���[�h�̏������ŃG���[: {ex.Message}");
            }
            finally { IsBusy = false; }
        }

        // �݌v -> �ΏۑI��
        private void OnBackToSelectTargets()
        {
            if (HasUnsavedChanges())
            {
                var ok = System.Windows.MessageBox.Show(
                    "�݌v���e�ɖ��ۑ��̕ύX������܂��B�ΏۑI���ɖ߂�܂����H�i�ύX�͕ێ�����܂����A�ۑ��͂���Ă��܂���j",
                    "�m�F",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (ok != System.Windows.MessageBoxResult.Yes) return;
            }
            State = BuildState.SelectTargets;
        }

        private void OnPickBasePath()
        {
            if (PickFolder == null) { ShowMessage?.Invoke("�G���[", "�t�H���_�I���T�[�r�X�����ݒ�ł��B"); return; }
            var picked = PickFolder.Invoke();
            if (string.IsNullOrWhiteSpace(picked)) return;

            if (HasUnsavedChanges())
            {
                if (System.Windows.MessageBox.Show("���ۑ��̕ύX������܂��B�x�[�X��؂�ւ��܂����H", "�m�F",
                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
                    return;
            }

            ClassificationBasePath = picked;
            Root = CategoryNode.UncategorizedRoot();
            SelectedCategory = Root.Children.FirstOrDefault();
            Status = $"�ۑ����ݒ�: {picked}";
        }

        private async Task OnAutoImportAsync()
        {
            if (!IsBasePicked) { ShowMessage?.Invoke("�x��", "�܂��ۑ���x�[�X��I�����Ă��������B"); return; }
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                Status = "�ۑ���̃t�H���_�\����ǂݍ��ݒ��c";
                var nodes = await CategoriesRepository.ImportFromBaseAsync(ClassificationBasePath!, CancellationToken.None);
                MergeIntoTree(nodes);
                SelectedCategory ??= Root.FlattenTree().FirstOrDefault(n => !n.IsRequired) ?? Root.Children.FirstOrDefault();
                Status = $"������荞��: {nodes.Count} ���̃J�e�S���𔽉f���܂����B";
            }
            catch (Exception ex) { ShowMessage?.Invoke("�G���[", $"������荞�݂Ɏ��s���܂���: {ex.Message}"); }
            finally { IsBusy = false; }
        }

        // ���[�g�����ɐV�K�iDisplay�͋󁨌�ňꊇ�Čv�Z�j
        private void OnAddRootCategory()
        {
            var rel = PathUtils.JoinRel("", "NewCategory");
            var node = new CategoryNode(rel, /*Display*/"", new ObservableCollection<CategoryNode>(), false);
            Root.Children.Add(node);
            SelectedCategory = node;
        }

        // �I���m�[�h�̎q�ɒǉ�
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
            RecomputeRelPathsFromDisplay();
        }

        private void OnRemoveCategory()
        {
            if (SelectedCategory == null) return;
            if (SelectedCategory.IsRequired) { ShowMessage?.Invoke("�G���[", "�����ނ͍폜�ł��܂���B"); return; }
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

            // �ړ���Ɉꊇ�Čv�Z
            RecomputeRelPathsFromDisplay();
        }

        private async Task OnSaveAsync()
        {
            if (!IsBasePicked) { ShowMessage?.Invoke("�x��", "�ۑ���x�[�X��I�����Ă��������B"); return; }

            // RelPath �͖���v���O�������Čv�Z
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count > 0 || duplicates.Count > 0)
            {
                ShowMessage?.Invoke("�G���[", string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"�d��: {d}")).Take(10)));
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
                Status = $"�ۑ����܂���: {dst}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("�G���[", $"�ۑ��Ɏ��s���܂���: {ex.Message}"); }
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

                // �ǂݍ��݌���K���� RelPath �𑵂���
                RecomputeRelPathsFromDisplay();

                Status = $"�ǂݍ��݊���: {src}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("�G���[", $"�ǂݍ��݂Ɏ��s���܂���: {ex.Message}"); }
        }

        private async Task OnExportAsync()
        {
            try
            {
                // �����o���O�ɋK���Ő��`
                RecomputeRelPathsFromDisplay();

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(appData, "FileReName");
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, $"categories_export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                await CategoriesRepository.SaveAsync(ClassificationBasePath ?? "", Root.Children, dst, CancellationToken.None);
                Status = $"�G�N�X�|�[�g���܂���: {dst}";
            }
            catch (Exception ex) { ShowMessage?.Invoke("�G���[", $"�G�N�X�|�[�g�Ɏ��s���܂���: {ex.Message}"); }
        }

        private void OnValidate()
        {
            // �K���ő����������Ń`�F�b�N
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count == 0 && duplicates.Count == 0)
            {
                ShowMessage?.Invoke("OK", "�J�e�S���͑Ó��ł��B");
                Status = "����: OK";
            }
            else
            {
                var msg = string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"�d��: {d}")));
                System.Windows.MessageBox.Show(msg, "���؃G���[", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                Status = "����: �G���[����";
            }
        }

        // Root ����/�K�{�m�[�h�����O���Č���
        private List<string> ValidateAll(out List<string> duplicates)
        {
            var errors = new List<string>();
            duplicates = new List<string>();
            var rels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in Root.FlattenTree())
            {
                if (ReferenceEquals(node, Root)) continue;      // ���z���[�g���O
                if (node.IsRequired) continue;                  // �����ނȂǂ̕K�{�͏��O

                if (string.IsNullOrWhiteSpace(node.RelPath))
                {
                    errors.Add($"���RelPath: {node.Display}");
                    continue;
                }
                var norm = PathUtils.NormalizeRelPath(node.RelPath);

                if (!PathUtils.IsValidRelPath(norm))
                    errors.Add($"������RelPath: {node.RelPath}");

                if (!rels.Add(norm))
                    duplicates.Add(norm);
            }
            return errors;
        }

        // Display �� RelPath ���ꊇ�Čv�Z�i������ _1, _2�c �ň�Ӊ��j
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

        // ===== BLOCK F: �h���C�����iAI���� �� Plan�����j =====
        private async void OnRunDryRun()
        {
            if (!IsBasePicked)
            {
                ShowMessage?.Invoke("�x��", "�ۑ���x�[�X��I�����Ă��������B");
                return;
            }

            // RelPath �𖈉�v���O�����Ŋm��
            RecomputeRelPathsFromDisplay();

            var errors = ValidateAll(out var duplicates);
            if (errors.Count > 0 || duplicates.Count > 0)
            {
                var first = string.Join(Environment.NewLine, errors.Concat(duplicates.Select(d => $"�d��: {d}")).Take(5));
                ShowMessage?.Invoke("�x��", "�o���f�[�V�����G���[������܂�:" + Environment.NewLine + first);
                return;
            }

            if (IsBusy) return;
            IsBusy = true;
            try
            {
                Status = "AI ���ށi�h���C�����j�����s���c";
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

                // DryRun �\���ޗ����\�z�i�c���[�ƃt�@�C�������j
                BuildDryView();

                State = BuildState.DryRunPreview; // �v���r���[��
                Status = $"Plan: �쐬 {DryNewFolders} / �ړ� {DryMoves} / ������ {DryUnresolved} / �G���[ {DryErrors}";
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("�G���[", $"�h���C�������s: {ex.Message}");
            }
            finally { IsBusy = false; RefreshCommands(); }
        }

        // ===== BLOCK G: �K�p =====
        private async Task OnApplyAsync()
        {
            if (Plan == null || !IsBasePicked) { ShowMessage?.Invoke("�x��", "Plan ������܂���B"); return; }
            if (IsBusy) return;
            IsBusy = true;

            _applyCts?.Cancel();
            _applyCts = new CancellationTokenSource();
            _applier = new PlanApplier(ClassificationBasePath!);

            try
            {
                State = BuildState.Applying;
                Status = "�K�p���c";

                var prog = new Progress<ApplyProgress>(p =>
                {
                    Status = $"{p.Phase}: {p.Done}/{p.Total} {p.Current} (Errors:{p.Errors})";
                });

                await _applier.ApplyAsync(Plan, prog, _applyCts.Token);

                // �K�p������͏��������đΏۑI���֖߂�
                ResetForNewSession();
                State = BuildState.SelectTargets;
                Status = "�K�p�����B�ΏۑI����ʂɖ߂�܂����B";
            }
            catch (OperationCanceledException)
            {
                Status = "�L�����Z�����܂���";
                State = BuildState.Design;
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("�G���[", $"�K�p���Ɏ��s: {ex.Message}");
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
                Status = "Undo ���s���c";
                await _applier.UndoAllAsync();
                Status = "Undo ����";
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke("�G���[", $"Undo ���s: {ex.Message}");
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
            catch (Exception ex) { ShowMessage?.Invoke("�G���[", $"�t�H���_���J���܂���ł���: {ex.Message}"); }
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

        // �K�p������̃Z�b�V����������
        private void ResetForNewSession()
        {
            Plan = null;
            SelectedTargets.Clear();
            TestFilePath = null;

            DryTree.Clear();
            DryUnresolvedList.Clear();
            DryErrorsList.Clear();
            DryNewFolders = DryMoves = DryUnresolved = DryErrors = 0;
        }

        // ========= DryRun �\���\�z�i�t�H���_�{���̃t�@�C���j =========
        private void BuildDryView()
        {
            DryTree.Clear();
            DryUnresolvedList.Clear();
            DryErrorsList.Clear();
            DryNewFolders = DryMoves = DryUnresolved = DryErrors = 0;

            if (Plan == null) return;

            // �\�Ȃ� Stats �𗘗p���A������Ό�ŕ₤
            try
            {
                var statsProp = Plan.GetType().GetProperty("Stats");
                if (statsProp?.GetValue(Plan) is object stats)
                {
                    DryNewFolders = GetInt(stats, "CreateCount");
                    DryMoves = GetInt(stats, "MoveCount");
                    DryUnresolved = GetInt(stats, "UnresolvedCount");
                    DryErrors = GetInt(stats, "ErrorCount");
                }
            }
            catch { /* ignore */ }

            // �V�K�t�H���_�W��
            var newFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in TryGetStringEnumerable(Plan, "CreateDirs", "CreateFolders", "Creates", "NewFolders"))
            {
                var norm = PathUtils.NormalizeRelPath(rel ?? "");
                if (!string.IsNullOrWhiteSpace(norm)) newFolders.Add(norm);
            }
            if (DryNewFolders == 0 && newFolders.Count > 0) DryNewFolders = newFolders.Count;

            // Moves ���c���[�ɍ������ށi�t�@�C���s�����j
            var moveCount = 0;
            foreach (var move in TryGetEnumerable(Plan, "Moves", "MoveItems", "Operations"))
            {
                var destRel = FirstNonEmpty(
                    GetString(move, "DestRelPath"),
                    GetString(move, "ToRelPath"),
                    GetString(move, "RelPath"),
                    GetString(move, "TargetRel"),
                    GetString(move, "DestRel"),
                    GetString(move, "Rel"));

                if (string.IsNullOrWhiteSpace(destRel))
                {
                    // �t���p�X���瑊�ΐ���
                    var destFull = FirstNonEmpty(
                        GetString(move, "DestFullPath"),
                        GetString(move, "ToFullPath"),
                        GetString(move, "NewFullPath"),
                        GetString(move, "FullPath"));
                    if (!string.IsNullOrWhiteSpace(destFull) && IsBasePicked)
                    {
                        try
                        {
                            var dir = Path.GetDirectoryName(destFull);
                            if (!string.IsNullOrWhiteSpace(dir))
                                destRel = PathUtils.NormalizeRelPath(Path.GetRelativePath(ClassificationBasePath!, dir));
                        }
                        catch { }
                    }
                }
                if (string.IsNullOrWhiteSpace(destRel)) continue;

                var srcFull = FirstNonEmpty(
                    GetString(move, "SourceFullPath"),
                    GetString(move, "SrcFullPath"),
                    GetString(move, "OldFullPath"),
                    GetString(move, "Path"),
                    GetString(move, "FilePath"),
                    GetString(move, "Source"),
                    GetString(move, "Src")) ?? "";

                var norm = PathUtils.NormalizeRelPath(destRel!);
                var isNew = newFolders.Contains(norm);

                AddMoveEntry(norm, srcFull, isNew);
                moveCount++;
            }
            if (DryMoves <= 0 && moveCount > 0) DryMoves = moveCount;

            // Unresolved / Errors
            foreach (var u in TryGetEnumerable(Plan, "Unresolved", "Unclassified", "Skipped"))
            {
                var path = FirstNonEmpty(GetString(u, "Path"), GetString(u, "FilePath"), GetString(u, "Source"), GetString(u, "Src"));
                var reason = FirstNonEmpty(GetString(u, "Reason"), GetString(u, "Message"), GetString(u, "Why"));
                DryUnresolvedList.Add(new DryIssue { Path = path ?? "(unknown)", Reason = reason ?? "" });
            }
            foreach (var e in TryGetEnumerable(Plan, "Errors", "Failures", "ErrorItems"))
            {
                var path = FirstNonEmpty(GetString(e, "Path"), GetString(e, "FilePath"), GetString(e, "Source"), GetString(e, "Src"));
                var reason = FirstNonEmpty(GetString(e, "Reason"), GetString(e, "Message"), GetString(e, "Why"));
                DryErrorsList.Add(new DryIssue { Path = path ?? "(unknown)", Reason = reason ?? "" });
            }
            if (DryUnresolved <= 0) DryUnresolved = DryUnresolvedList.Count;
            if (DryErrors <= 0) DryErrors = DryErrorsList.Count;

            // �e�m�[�h�ɏW�v
            foreach (var top in DryTree) top.UpdateAggregateCount();
        }

        // �w��rel�̃t�H���_�m�[�h�����i�Ȃ���ΊK�w�����j�A�����փt�@�C���s����������
        private void AddMoveEntry(string destRel, string sourceFullPath, bool isNewFolder)
        {
            var leaf = EnsurePathNode(destRel, isNewFolder);
            var fileName = string.IsNullOrWhiteSpace(sourceFullPath) ? "(file)" : Path.GetFileName(sourceFullPath);
            var file = new DryLeaf(fileName, sourceFullPath);
            leaf.Files.Add(file);
            leaf.Items.Add(file);
            leaf.Count += 1; // ���m�[�h�����̌���
        }

        // rel = "A/B/C" �̊K�w������čŏI�m�[�h��Ԃ�
        private DryNode EnsurePathNode(string rel, bool isNewFlag)
        {
            var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                // ���[�g�����ɂ܂Ƃ߂�t�H�[���o�b�N
                var root = DryTree.FirstOrDefault(n => n.Name.Equals("(root)", StringComparison.OrdinalIgnoreCase));
                if (root == null)
                {
                    root = new DryNode("(root)", isNewFlag);
                    DryTree.Add(root);
                }
                return root;
            }

            DryNode EnsureTop(string name, bool newFlag)
            {
                var top = DryTree.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (top == null)
                {
                    var firstRel = PathUtils.JoinRel("", name);
                    var flag = newFlag || !FolderExists(firstRel);
                    top = new DryNode(name, flag);
                    DryTree.Add(top);
                }
                return top;
            }

            var node = EnsureTop(parts[0], isNewFlag);
            var current = node;
            var accRel = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                accRel = PathUtils.JoinRel(accRel, parts[i]);
                var child = current.Children.FirstOrDefault(c => c.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    var childNew = isNewFlag || !FolderExists(accRel);
                    child = new DryNode(parts[i], childNew);
                    current.Children.Add(child);
                    current.Items.Add(child);
                }
                current = child;
            }
            return current;
        }

        private bool FolderExists(string rel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ClassificationBasePath)) return false;
                var abs = PathUtils.CombineBase(ClassificationBasePath!, rel);
                return Directory.Exists(abs);
            }
            catch { return false; }
        }

        // �N���b�v�{�[�h�֗v��R�s�[
        private void OnCopyPlanSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"�ۑ���: {BaseDisplay}");
            sb.AppendLine($"���v: �쐬 {DryNewFolders} / �ړ� {DryMoves} / ������ {DryUnresolved} / �G���[ {DryErrors}");
            if (DryTree.Any())
            {
                sb.AppendLine();
                sb.AppendLine("[�ړ���c���[�i�����j]");
                void Walk(DryNode n, string indent)
                {
                    sb.AppendLine($"{indent}{n.Name} ({n.AggregateCount}){(n.IsNew ? " *new" : "")}");
                    foreach (var c in n.Children) Walk(c, indent + "  ");
                    foreach (var f in n.Files) sb.AppendLine($"{indent}  - {f.Name}");
                }
                foreach (var top in DryTree) Walk(top, "");
            }
            System.Windows.Clipboard.SetText(sb.ToString());
            Status = "DryRun�T�v���R�s�[���܂����B";
        }

        // ---- ���˃��[�e�B���e�B ----
        private static IEnumerable<object> TryGetEnumerable(object obj, params string[] propNames)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name);
                if (p?.GetValue(obj) is System.Collections.IEnumerable en)
                {
                    foreach (var it in en) if (it != null) yield return it;
                    yield break;
                }
            }
        }
        private static IEnumerable<string?> TryGetStringEnumerable(object obj, params string[] propNames)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name);
                if (p?.GetValue(obj) is System.Collections.IEnumerable en)
                {
                    foreach (var it in en) yield return it?.ToString();
                    yield break;
                }
            }
        }
        private static string? GetString(object? obj, string propName)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(propName);
            if (p == null) return null;
            var v = p.GetValue(obj);
            return v?.ToString();
        }
        private static string? FirstNonEmpty(params string?[] xs)
        {
            foreach (var s in xs) if (!string.IsNullOrWhiteSpace(s)) return s;
            return null;
        }
        private static int GetInt(object obj, string prop)
        {
            var p = obj.GetType().GetProperty(prop);
            if (p?.GetValue(obj) is int i) return i;
            if (p?.GetValue(obj) is long l) return (int)l;
            if (int.TryParse(p?.GetValue(obj)?.ToString(), out var j)) return j;
            return 0;
        }
    }

    // ===== DryRun �\���p DTO =====
    public sealed class DryIssue
    {
        public string Path { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class DryLeaf
    {
        public string Name { get; }
        public string SourceFullPath { get; }
        public DryLeaf(string name, string sourceFullPath)
        {
            Name = name; SourceFullPath = sourceFullPath;
        }
    }

    public sealed class DryNode : NotifyBase
    {
        public string Name { get; }
        public bool IsNew { get; }

        public ObservableCollection<DryNode> Children { get; } = new();
        public ObservableCollection<DryLeaf> Files { get; } = new();

        // �c���[�\���p�i�t�H���_���t�@�C���̏��j
        public ObservableCollection<object> Items { get; } = new();

        private int _count;                     // ���m�[�h�����̈ړ������i���z���̃t�@�C�����j
        public int Count { get => _count; set => Set(ref _count, value); }

        private int _agg;
        public int AggregateCount { get => _agg; private set => Set(ref _agg, value); }

        public DryNode(string name, bool isNew)
        {
            Name = name; IsNew = isNew;
        }

        public void UpdateAggregateCount()
        {
            var sum = Count;
            foreach (var c in Children)
            {
                c.UpdateAggregateCount();
                sum += c.AggregateCount;
            }
            AggregateCount = sum;
        }
    }

    // ������ BuildPlan / PlanApplier / CategoryNode / CategoriesRepository / PathUtils / RelayCommand / NotifyBase ��
    // �v���W�F�N�g���̎��������̂܂܎Q�Ƃ��܂��B
}

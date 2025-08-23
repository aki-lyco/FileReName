using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        // �� �ʒm���v���p�e�B�ɕύX�iUI ���f�̂��߁j
        private string? _testFilePath;
        public string? TestFilePath
        {
            get => _testFilePath;
            set => Set(ref _testFilePath, value);
        }

        public string Status { get; set; } = "";

        // BLOCK F: �v��
        private BuildPlan? _plan;
        public BuildPlan? Plan { get => _plan; set => Set(ref _plan, value); }

        // BLOCK G: �K�p
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

        // BLOCK G: �K�p��Undo
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

        // ���[�g�����ɐV�K�i��Display�͋�ARelPath�͎����̔ԂŌォ��t�^�j
        private void OnAddRootCategory()
        {
            var rel = PathUtils.JoinRel("", "NewCategory");
            var node = new CategoryNode(rel, /*Display*/"", new ObservableCollection<CategoryNode>(), false);
            Root.Children.Add(node);
            SelectedCategory = node;
            // �\�����ҏW��� RecomputeRelPathsFromDisplay() �ňꊇ�Čv�Z
        }

        // �I���m�[�h�̎q�ɒǉ��i��Display�͋�ARelPath�͌�ňꊇ�Čv�Z�j
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

            // Display �� UI �ŕҏW�ςݑz��BRelPath �̓��[�U�[�l���g�킸�S�̍Čv�Z�Ō��߂�B
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

            // ��RelPath �͏�Ƀv���O�������Čv�Z�iDisplay �� ���S���A��Ӊ��j
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

        // ��Root ���́i���z���[�g�j�ƕK�{�m�[�h���X�L�b�v���Č���
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

        // ��Display ���� RelPath ���ꊇ�Čv�Z�i������ _1, _2�c �ň�Ӊ��j
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
                    // �K�{�m�[�h�͊���� RelPath �𑸏d
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

            // ��RelPath �𖈉�v���O�����Ŋm��
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
                State = BuildState.DryRunPreview; // �v���r���[��
                Status = $"Plan: �쐬 {plan.Stats.CreateCount} / �ړ� {plan.Stats.MoveCount} / ������ {plan.Stats.UnresolvedCount} / �G���[ {plan.Stats.ErrorCount}";
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

                // ���K�p������͏��������đΏۑI���֖߂�
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

        // �� �K�p������̃Z�b�V����������
        private void ResetForNewSession()
        {
            Plan = null;              // PropertyChanged �����
            SelectedTargets.Clear();
            TestFilePath = null;
        }
    }
}

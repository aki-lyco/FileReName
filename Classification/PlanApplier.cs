using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Explore.Indexing;

namespace Explore.Build
{
    public sealed class PlanApplier
    {
        private readonly string _base;
        private readonly IndexDatabase _db = new();

        private readonly List<Func<Task>> _undo = new(); // �Z�b�V������ Undo

        public PlanApplier(string classificationBasePath) => _base = classificationBasePath;

        // �����݊��i���K�p�j
        public Task ApplyAsync(BuildPlan plan, IProgress<ApplyProgress>? progress, CancellationToken ct)
            => ApplyAsync(plan, progress, ct, simulate: false);

        // �� �h���C�����Ή��isimulate=true �Ŏ��t�@�C�������������Ȃ��j
        public async Task ApplyAsync(BuildPlan plan, IProgress<ApplyProgress>? progress, CancellationToken ct, bool simulate)
        {
            await _db.EnsureCreatedAsync();

            // 1) CreateDirs
            for (int i = 0; i < plan.CreateDirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = plan.CreateDirs[i].RelPath;
                var abs = CombineBase(_base, rel);
                progress?.Report(new ApplyProgress("CreateDirs", i, plan.CreateDirs.Count, abs, 0));
                if (simulate) continue;

                try
                {
                    Directory.CreateDirectory(abs);
                    _undo.Add(() =>
                    {
                        try
                        {
                            if (Directory.Exists(abs) && Directory.GetFileSystemEntries(abs).Length == 0)
                                Directory.Delete(abs);
                        }
                        catch { }
                        return Task.CompletedTask;
                    });
                }
                catch { /* �ʈ��� */ }
            }

            // 2) RenameDirs�i�ȈՁF���݂��Ȃ����Create�ɔC����Bsimulate���̓X�L�b�v�j
            for (int i = 0; i < plan.RenameDirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = plan.RenameDirs[i];
                string oldAbs = CombineBase(_base, r.OldRelPath);
                string newAbs = CombineBase(_base, r.NewRelPath);
                progress?.Report(new ApplyProgress("RenameDirs", i, plan.RenameDirs.Count, $"{oldAbs} -> {newAbs}", 0));
                if (simulate) continue;

                try
                {
                    if (Directory.Exists(oldAbs))
                    {
                        string tmp = Path.Combine(Path.GetDirectoryName(newAbs)!, "__rename_tmp_" + Guid.NewGuid().ToString("N"));
                        Directory.Move(oldAbs, tmp);
                        _undo.Add(() => { try { if (Directory.Exists(tmp)) Directory.Move(tmp, oldAbs); } catch { } return Task.CompletedTask; });

                        Directory.CreateDirectory(Path.GetDirectoryName(newAbs)!);
                        Directory.Move(tmp, newAbs);
                        _undo.Add(() => { try { if (Directory.Exists(newAbs)) Directory.Move(newAbs, oldAbs); } catch { } return Task.CompletedTask; });
                    }
                }
                catch { /* ���� */ }
            }

            // 3) MoveItems�i����ċ����Ή��j
            int errCount = 0;
            for (int i = 0; i < plan.Moves.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var m = plan.Moves[i];
                progress?.Report(new ApplyProgress("MoveItems", i, plan.Moves.Count, m.SourceFullPath, errCount));

                try
                {
                    if (!IsUnderBase(m.DestFullPath)) { errCount++; continue; }

                    if (simulate) continue; // ���t�@�C���ɐG��Ȃ�

                    Directory.CreateDirectory(Path.GetDirectoryName(m.DestFullPath)!);

                    bool sameVolume = string.Equals(Path.GetPathRoot(m.SourceFullPath), Path.GetPathRoot(m.DestFullPath), StringComparison.OrdinalIgnoreCase);
                    var src = new FileInfo(m.SourceFullPath);

                    // �K�p���_�ň��悪���܂��Ă�����A�A�Ԃŋ󂫂����
                    var dest = new FileInfo(GetAvailableName(Path.GetDirectoryName(m.DestFullPath)!, Path.GetFileName(m.DestFullPath)));

                    if (sameVolume)
                    {
                        File.Move(src.FullName, dest.FullName, overwrite: false);
                        _undo.Add(() => { try { File.Move(dest.FullName, src.FullName, overwrite: false); } catch { } return Task.CompletedTask; });

                        await _db.UpdateFilePathAsync(FileKeyUtil.GetStableKey(src.FullName),
                                                      dest.FullName, dest.DirectoryName!, dest.Name, dest.Extension, ct);

                        await _db.InsertMoveAsync(FileKeyUtil.GetStableKey(src.FullName), src.FullName, dest.FullName, "move", m.Reason, ct);
                    }
                    else
                    {
                        File.Copy(src.FullName, dest.FullName, overwrite: false);
                        _undo.Add(() => { try { if (File.Exists(dest.FullName)) File.Delete(dest.FullName); } catch { } return Task.CompletedTask; });

                        await _db.UpsertFileFromFsAsync(dest.FullName, ct);
                        await _db.InsertMoveAsync(FileKeyUtil.GetStableKey(src.FullName), src.FullName, dest.FullName, "copy+delete", m.Reason, ct);

                        await _db.MigrateSuggestionsAsync(FileKeyUtil.GetStableKey(src.FullName), FileKeyUtil.GetStableKey(dest.FullName), ct);

                        File.Delete(src.FullName);
                        _undo.Add(async () => { try { File.Copy(dest.FullName, src.FullName, overwrite: false); await _db.UpsertFileFromFsAsync(src.FullName, ct); } catch { } });
                    }
                }
                catch
                {
                    errCount++;
                }
            }

            // 4) UpdateDb �t�F�[�Y�I�[�i����͓��ɖ����j
            progress?.Report(new ApplyProgress("UpdateDb", 1, 1, null, 0));

            // 5) Done
            progress?.Report(new ApplyProgress("Done", 1, 1, null, 0));
        }

        public async Task UndoAllAsync()
        {
            for (int i = _undo.Count - 1; i >= 0; i--)
            {
                try { await _undo[i](); } catch { /* ���� */ }
            }
            _undo.Clear();
        }

        private bool IsUnderBase(string abs)
        {
            var full = Path.GetFullPath(abs);
            var b = Path.GetFullPath(_base).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        private static string CombineBase(string @base, string rel)
            => Path.GetFullPath(Path.Combine(@base, rel.Replace('/', Path.DirectorySeparatorChar)));

        // �� PlanBuilder �Ɠ����̋󂫖��擾�i�K�p���_�ł�����x�m�F�j
        private static string GetAvailableName(string destDirAbs, string fileName)
        {
            Directory.CreateDirectory(destDirAbs);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            string candidate = Path.Combine(destDirAbs, fileName);
            if (!File.Exists(candidate)) return candidate;

            int i = 1;
            while (true)
            {
                candidate = Path.Combine(destDirAbs, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
                i++;
            }
        }
    }
}

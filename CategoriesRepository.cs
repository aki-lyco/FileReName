using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Build
{
    public static class CategoriesRepository
    {
        private static JsonSerializerOptions JsonOpts => new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null
        };

        private sealed class CategoryDto
        {
            [JsonPropertyName("rel_path")] public string RelPath { get; set; } = "";
            [JsonPropertyName("display")] public string Display { get; set; } = "";
            [JsonPropertyName("keywords")] public string[]? Keywords { get; set; }
            [JsonPropertyName("ext_filter")] public string? ExtFilter { get; set; }
            [JsonPropertyName("ai_hint")] public string? AiHint { get; set; }
        }

        public static async Task SaveAsync(string basePath, IEnumerable<CategoryNode> nodes, string dstJson, CancellationToken ct)
        {
            var list = new List<CategoryDto>();
            foreach (var n in nodes)
            {
                foreach (var flat in n.FlattenTree())
                {
                    if (flat.IsRequired) continue;
                    if (string.IsNullOrWhiteSpace(flat.RelPath)) continue;

                    string[]? keywords = null;
                    if (!string.IsNullOrWhiteSpace(flat.KeywordsJson))
                    {
                        try { keywords = JsonSerializer.Deserialize<string[]>(flat.KeywordsJson!); }
                        catch { keywords = flat.KeywordsJson!.Split(new[] { ',', ' ', '�@', ';' }, StringSplitOptions.RemoveEmptyEntries); }
                    }

                    list.Add(new CategoryDto
                    {
                        RelPath = PathUtils.NormalizeRelPath(flat.RelPath),
                        Display = flat.Display,
                        Keywords = keywords,
                        ExtFilter = flat.ExtFilter,
                        AiHint = flat.AiHint
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dstJson)!);
            await File.WriteAllTextAsync(dstJson, JsonSerializer.Serialize(list, JsonOpts), ct);
        }

        public static async Task<IReadOnlyList<CategoryNode>> LoadAsync(string srcJson, CancellationToken ct)
        {
            if (!File.Exists(srcJson)) return Array.Empty<CategoryNode>();
            var text = await File.ReadAllTextAsync(srcJson, ct);
            var list = JsonSerializer.Deserialize<List<CategoryDto>>(text, JsonOpts) ?? new();
            var root = CategoryNode.UncategorizedRoot();
            foreach (var dto in list)
            {
                if (string.IsNullOrWhiteSpace(dto.RelPath)) continue;
                root.AddByRelPath(dto.RelPath, dto.Display, false,
                    dto.Keywords is null ? null : JsonSerializer.Serialize(dto.Keywords),
                    dto.ExtFilter, dto.AiHint);
            }
            return new[] { root };
        }

        /// <summary>
        /// base �z���̎��t�H���_���O�ɋ����񋓂��ăc���[���iHidden/System/Reparse ���O�j
        /// </summary>
        public static async Task<IReadOnlyList<CategoryNode>> ImportFromBaseAsync(string basePath, CancellationToken ct)
        {
            var root = CategoryNode.UncategorizedRoot();
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                return new[] { root };

            await Task.Run(() =>
            {
                var q = new Queue<string>();
                q.Enqueue(basePath);

                while (q.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = q.Dequeue();

                    string[] subDirs;
                    try
                    {
                        subDirs = Directory.GetDirectories(current);
                    }
                    catch
                    {
                        continue; // �A�N�Z�X�s�\�Ȃǂ̓X�L�b�v
                    }

                    foreach (var dir in subDirs)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            var di = new DirectoryInfo(dir);
                            var attrs = di.Attributes;

                            if ((attrs & FileAttributes.Hidden) != 0) continue;
                            if ((attrs & FileAttributes.System) != 0) continue;
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue; // �V���{���b�N�����N���͏��O

                            var rel = PathUtils.NormalizeRelPath(Path.GetRelativePath(basePath, dir).Replace('\\', '/'));
                            if (string.IsNullOrWhiteSpace(rel)) continue;

                            root.AddByRelPath(rel, di.Name, false);
                            q.Enqueue(dir);
                        }
                        catch
                        {
                            // �ʃf�B���N�g���̖��͈����đ��s
                        }
                    }
                }
            }, ct);

            return new[] { root };
        }
    }
}

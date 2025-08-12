using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Explore.Indexing
{
    /// <summary>
    /// �ߋ��ɐ������� JSON ���Y�isnippet_output.json / classified_output.json / rename_suggestions.json�j��
    /// SQLite �Ɏ�荞�ނ��߂̃��[�e�B���e�B�B
    /// </summary>
    public sealed class JsonImporter
    {
        private readonly IndexDatabase _db;
        public JsonImporter(IndexDatabase db) => _db = db;

        public async Task<int> ImportFromFolderAsync(string folder)
        {
            await _db.EnsureCreatedAsync();

            var snippetPath = Path.Combine(folder, "snippet_output.json");
            var classPath = Path.Combine(folder, "classified_output.json");
            var renamePath = Path.Combine(folder, "rename_suggestions.json");

            var imported = 0;

            // 1) �X�j�y�b�g�̎�荞�݁i�K�{�ł͂Ȃ����ŏ��ɓǂށj
            if (File.Exists(snippetPath))
            {
                var json = await File.ReadAllTextAsync(snippetPath);
                // �t�H�[�}�b�g�̈Ⴂ�ɋ����p�[�T�F�z�� or �����A�v���p�e�B���̗h��ɑΉ�
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var path = Pick(el, "path", "Path", "fullPath", "FullPath");
                            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                            var fi = new FileInfo(path);
                            var r = new DbFileRecord
                            {
                                FileKey = FileKeyUtil.GetStableKey(path),
                                Path = fi.FullName,
                                Parent = fi.DirectoryName,
                                Name = fi.Name,
                                Ext = fi.Extension,
                                Size = fi.Exists ? fi.Length : 0,
                                MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                                CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                                Mime = null,
                                Summary = null,
                                Snippet = Pick(el, "snippet", "Snippet", "text", "content"),
                                Classified = null,
                            };
                            await _db.UpsertFileAsync(r);
                            imported++;
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var path = prop.Name;
                            if (!File.Exists(path)) continue;
                            var fi = new FileInfo(path);
                            var r = new DbFileRecord
                            {
                                FileKey = FileKeyUtil.GetStableKey(path),
                                Path = fi.FullName,
                                Parent = fi.DirectoryName,
                                Name = fi.Name,
                                Ext = fi.Extension,
                                Size = fi.Exists ? fi.Length : 0,
                                MTimeUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                                CTimeUnix = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
                                Mime = null,
                                Summary = null,
                                Snippet = prop.Value.GetString(),
                                Classified = null,
                            };
                            await _db.UpsertFileAsync(r);
                            imported++;
                        }
                    }
                }
                catch { /* �ǂ߂Ȃ���΃X�L�b�v */ }
            }

            // 2) ���ތ��ʂ̎�荞�݁i����΁j
            if (File.Exists(classPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(classPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var path = Pick(el, "path", "Path", "fullPath", "FullPath");
                            var cls = Pick(el, "class", "Class", "category", "Category");
                            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cls)) continue;
                            var key = FileKeyUtil.GetStableKey(path);
                            await _db.UpsertClassificationAsync(key, cls);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var path = prop.Name;
                            var cls = prop.Value.GetString();
                            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cls)) continue;
                            var key = FileKeyUtil.GetStableKey(path);
                            await _db.UpsertClassificationAsync(key, cls);
                        }
                    }
                }
                catch { /* �X�L�b�v */ }
            }

            // 3) ���l�[���Ă̎�荞�݁i����΁j
            if (File.Exists(renamePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(renamePath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var path = Pick(el, "path", "Path");
                            var sug = Pick(el, "suggestion", "name", "rename", "Suggestion");
                            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sug)) continue;
                            var key = FileKeyUtil.GetStableKey(path);
                            await _db.UpsertRenameSuggestionAsync(key, sug);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var path = prop.Name;
                            var sug = prop.Value.GetString();
                            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sug)) continue;
                            var key = FileKeyUtil.GetStableKey(path);
                            await _db.UpsertRenameSuggestionAsync(key, sug);
                        }
                    }
                }
                catch { /* �X�L�b�v */ }
            }

            return imported;
        }

        private static string? Pick(JsonElement el, params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) return v.GetString();
                    return v.ToString();
                }
            }
            return null;
        }
    }
}
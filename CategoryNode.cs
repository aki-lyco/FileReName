using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Explore.FileSystem;

namespace Explore.Build
{
    public sealed class CategoryNode : NotifyBase
    {
        private string _relPath;
        private string _display;
        private ObservableCollection<CategoryNode> _children;
        private bool _isRequired;
        private string? _keywordsJson;
        private string? _extFilter;
        private string? _aiHint;

        public string RelPath
        {
            get => _relPath;
            set => Set(ref _relPath, PathUtils.NormalizeRelPath(value ?? ""));
        }

        public string Display
        {
            get => _display;
            set
            {
                var safe = PathUtils.MakeSafeName(value ?? "");
                if (Set(ref _display, safe))
                {
                    if (!IsRequired)
                    {
                        // 表示名が変わったら、ユーザー入力に依存せず規則で RelPath を再構成
                        var parent = PathUtils.GetParentRel(RelPath);
                        RelPath = PathUtils.JoinRel(parent, safe);
                    }
                }
            }
        }

        public ObservableCollection<CategoryNode> Children => _children;
        public bool IsRequired { get => _isRequired; set => Set(ref _isRequired, value); }
        public string? KeywordsJson { get => _keywordsJson; set => Set(ref _keywordsJson, value); }
        public string? ExtFilter { get => _extFilter; set => Set(ref _extFilter, value); }
        public string? AiHint { get => _aiHint; set => Set(ref _aiHint, value); }

        public CategoryNode(string relPath, string display, ObservableCollection<CategoryNode>? children = null,
            bool isRequired = false, string? keywordsJson = null, string? extFilter = null, string? aiHint = null)
        {
            _relPath = PathUtils.NormalizeRelPath(relPath ?? "");
            _display = display ?? "";
            _children = children ?? new ObservableCollection<CategoryNode>();
            _isRequired = isRequired;
            _keywordsJson = keywordsJson;
            _extFilter = extFilter;
            _aiHint = aiHint;
        }

        public static CategoryNode UncategorizedRoot()
        {
            var root = new CategoryNode("", "", new ObservableCollection<CategoryNode>());
            // 固定名 & 固定 RelPath（表示は日本語、RelPath は英字固定で安全）
            root.Children.Add(new CategoryNode("_Uncategorized", "未分類",
                new ObservableCollection<CategoryNode>(), isRequired: true));
            return root;
        }

        public IEnumerable<CategoryNode> FlattenTree()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var x in c.FlattenTree())
                    yield return x;
        }

        public IEnumerable<string> FlattenRelPaths()
        {
            foreach (var n in FlattenTree())
                if (!string.IsNullOrWhiteSpace(n.RelPath))
                    yield return PathUtils.NormalizeRelPath(n.RelPath);
        }

        public bool ContainsRelPath(string rel)
        {
            var norm = PathUtils.NormalizeRelPath(rel);
            return FlattenRelPaths().Any(p => p.Equals(norm, StringComparison.OrdinalIgnoreCase));
        }

        public CategoryNode? FindByRelPath(string rel)
        {
            var norm = PathUtils.NormalizeRelPath(rel);
            return FlattenTree().FirstOrDefault(n => PathUtils.NormalizeRelPath(n.RelPath).Equals(norm, StringComparison.OrdinalIgnoreCase));
        }

        public bool RemoveByRelPath(string rel)
        {
            var norm = PathUtils.NormalizeRelPath(rel);
            return RemoveByRelPathCore(this, norm);
        }

        private bool RemoveByRelPathCore(CategoryNode parent, string normRel)
        {
            for (int i = 0; i < parent.Children.Count; i++)
            {
                var ch = parent.Children[i];
                if (PathUtils.NormalizeRelPath(ch.RelPath).Equals(normRel, StringComparison.OrdinalIgnoreCase))
                {
                    if (ch.IsRequired) return false;
                    parent.Children.RemoveAt(i);
                    return true;
                }
                if (RemoveByRelPathCore(ch, normRel)) return true;
            }
            return false;
        }

        public void RenameBranch(string oldRel, string newRel)
        {
            var node = FindByRelPath(oldRel);
            if (node == null) return;
            var map = new Dictionary<CategoryNode, string>();
            foreach (var n in node.FlattenTree())
            {
                if (n == node) map[n] = newRel;
                else
                {
                    if (n.RelPath.StartsWith(oldRel, StringComparison.OrdinalIgnoreCase))
                    {
                        var tail = n.RelPath.Substring(oldRel.Length).TrimStart('/', '\\');
                        map[n] = PathUtils.JoinRel(newRel, tail);
                    }
                }
            }
            foreach (var kv in map)
                kv.Key.RelPath = kv.Value;
            node.Display = PathUtils.GetFileName(newRel);
        }

        public void AddByRelPath(string relPath, string display, bool isRequired = false,
            string? keywordsJson = null, string? extFilter = null, string? aiHint = null)
        {
            var parts = PathUtils.SplitRel(relPath);
            var cur = this;
            var built = "";
            for (int i = 0; i < parts.Length; i++)
            {
                var name = parts[i];
                built = PathUtils.JoinRel(built, name);
                var next = cur.Children.FirstOrDefault(c => PathUtils.GetFileName(c.RelPath).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = new CategoryNode(built, name, new ObservableCollection<CategoryNode>());
                    cur.Children.Add(next);
                }
                cur = next;
            }
            cur.Display = display;
            cur.IsRequired = isRequired;
            cur.KeywordsJson = keywordsJson;
            cur.ExtFilter = extFilter;
            cur.AiHint = aiHint;
        }
    }
}

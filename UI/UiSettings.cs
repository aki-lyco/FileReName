// UI/UiSettings.cs
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Explore.UI
{
    public sealed class UiSettings
    {
        /// <summary>フォルダ選択時に自動インデックス</summary>
        public bool AutoIndexOnSelect { get; set; } = false;

        /// <summary>
        /// インデックス時のサブフォルダの深さ。
        /// -1 = 無制限, 0 = 直下のみ, 1 = 1階層, 2 = 2階層…
        /// </summary>
        public int IncludeDepth { get; set; } = -1;

        /// <summary>
        /// 旧プロパティ（後方互換）。
        /// get: IncludeDepth != 0
        /// set: true → -1（無制限）, false → 0（直下のみ）
        /// </summary>
        [JsonIgnore] // 新しい保存では書き出さない（読込は Load() でレガシー対応）
        public bool IncludeSubfolders
        {
            get => IncludeDepth != 0;
            set => IncludeDepth = value ? -1 : 0;
        }

        public event EventHandler? Changed;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileReName");
        private static readonly string JsonPath = Path.Combine(Dir, "ui-settings.json");

        public static UiSettings Instance { get; private set; } = Load();

        public static UiSettings Load()
        {
            try
            {
                if (!File.Exists(JsonPath)) return new UiSettings();

                var text = File.ReadAllText(JsonPath);

                // レガシーJSON対応：IncludeDepth が無く、IncludeSubfolders だけがある場合に補完する
                using (var doc = JsonDocument.Parse(text))
                {
                    var root = doc.RootElement;

                    bool hasDepth = root.TryGetProperty("IncludeDepth", out _);
                    if (!hasDepth && root.TryGetProperty("IncludeSubfolders", out var inc))
                    {
                        // まず通常デシリアライズ
                        var s0 = JsonSerializer.Deserialize<UiSettings>(text) ?? new UiSettings();
                        // 旧ON→-1, 旧OFF→0 にマップ
                        s0.IncludeDepth = inc.ValueKind == JsonValueKind.True ? -1 : 0;
                        return s0;
                    }
                }

                // 通常パス（IncludeDepth を含む新形式 or 既存全体）
                var s = JsonSerializer.Deserialize<UiSettings>(text);
                return s ?? new UiSettings();
            }
            catch
            {
                return new UiSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(JsonPath, json);
            }
            catch
            {
                // ignore
            }
        }
    }
}

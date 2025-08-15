// UI/UiSettings.cs
using System;
using System.IO;
using System.Text.Json;

namespace Explore.UI
{
    public sealed class UiSettings
    {
        public bool AutoIndexOnSelect { get; set; } = false;     // フォルダ選択時に自動インデックス
        public bool IncludeSubfolders { get; set; } = false;     // サブフォルダを含める

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
                if (File.Exists(JsonPath))
                {
                    var text = File.ReadAllText(JsonPath);
                    var s = JsonSerializer.Deserialize<UiSettings>(text);
                    if (s != null) return s;
                }
            }
            catch { /* default */ }
            return new UiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(JsonPath, json);
            }
            catch { /* ignore */ }
        }
    }
}

// UI/UiSettings.cs
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Explore.UI
{
    public sealed class UiSettings
    {
        /// <summary>�t�H���_�I�����Ɏ����C���f�b�N�X</summary>
        public bool AutoIndexOnSelect { get; set; } = false;

        /// <summary>
        /// �C���f�b�N�X���̃T�u�t�H���_�̐[���B
        /// -1 = ������, 0 = �����̂�, 1 = 1�K�w, 2 = 2�K�w�c
        /// </summary>
        public int IncludeDepth { get; set; } = -1;

        /// <summary>
        /// ���v���p�e�B�i����݊��j�B
        /// get: IncludeDepth != 0
        /// set: true �� -1�i�������j, false �� 0�i�����̂݁j
        /// </summary>
        [JsonIgnore] // �V�����ۑ��ł͏����o���Ȃ��i�Ǎ��� Load() �Ń��K�V�[�Ή��j
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

                // ���K�V�[JSON�Ή��FIncludeDepth �������AIncludeSubfolders ����������ꍇ�ɕ⊮����
                using (var doc = JsonDocument.Parse(text))
                {
                    var root = doc.RootElement;

                    bool hasDepth = root.TryGetProperty("IncludeDepth", out _);
                    if (!hasDepth && root.TryGetProperty("IncludeSubfolders", out var inc))
                    {
                        // �܂��ʏ�f�V���A���C�Y
                        var s0 = JsonSerializer.Deserialize<UiSettings>(text) ?? new UiSettings();
                        // ��ON��-1, ��OFF��0 �Ƀ}�b�v
                        s0.IncludeDepth = inc.ValueKind == JsonValueKind.True ? -1 : 0;
                        return s0;
                    }
                }

                // �ʏ�p�X�iIncludeDepth ���܂ސV�`�� or �����S�́j
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

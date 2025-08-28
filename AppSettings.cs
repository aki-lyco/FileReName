using System;
using System.IO;
using System.Text.Json;

namespace Explore
{
    public sealed class AppSettings
    {
        public DevSettings Dev { get; set; } = new();
        public FirstRunSettings FirstRun { get; set; } = new();

        // �� �ǉ��F�O���c�[���́g�����������̃p�X�h��ۑ��i����͂��ꂾ�� File.Exists �ő�����j
        public ExternalToolsSettings ExternalTools { get; set; } = new();

        public string[] SkipAttributes { get; set; } = new[] { "System", "Hidden", "Temporary", "Offline" };
        public string[] ExcludeDirectories { get; set; } = new[]
        {
            @"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\",
            @"C:\ProgramData\", @"C:\$Recycle.Bin\", @"System Volume Information\",
            @"C:\Users\*\AppData\", ".git\\", "node_modules\\", "bin\\", "obj\\",
            "target\\", "Library\\", "cache\\", "temp\\", "tmp\\"
        };
        public string[] ExcludeExtensions { get; set; } = new[]
            { "exe","dll","sys","msi","bat","cmd","ps1","lnk","tmp","log" };

        public bool FirstRunCompleted { get; set; } = false;

        public static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "FileReName", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                var p = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                if (File.Exists(p))
                {
                    var json = File.ReadAllText(p);
                    var s = JsonSerializer.Deserialize<AppSettings>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (s != null) return s;
                }
            }
            catch { /* ����Ƀt�H�[���o�b�N */ }
            var def = new AppSettings();
            def.Save();
            return def;
        }

        public void Save()
        {
            var p = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public sealed class DevSettings
    {
        public bool AlwaysShowFirstRun { get; set; } = true; // �J�����͖���
    }

    public sealed class FirstRunSettings
    {
        public bool HotStartEnabled { get; set; } = true;
        public int HotWindowDays { get; set; } = 90;
        public string[] HotExts { get; set; } =
            new[] { "pdf", "docx", "xlsx", "pptx", "txt", "md", "jpg", "png" };
        public int HotMaxFiles { get; set; } = 5000;
        public bool ColdIndexInBackground { get; set; } = true;
        public int MinWarmCount { get; set; } = 100;
    }

    // �� �ǉ�
    public sealed class ExternalToolsSettings
    {
        public string PdfToText { get; set; } = "";   // pdftotext.exe
        public string PdfToPpm { get; set; } = "";   // pdftoppm.exe
        public string Tesseract { get; set; } = "";   // tesseract.exe
        public string TessdataDir { get; set; } = ""; // tessdata �f�B���N�g��

        public DateTime? LastVerifiedUtc { get; set; }
        public string? AssetsVersion { get; set; }    // �C�ӁFAssetsManifest �� version ��
    }
}

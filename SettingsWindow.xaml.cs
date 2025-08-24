using System;
using System.IO;
using System.Text;
using System.Windows;
using Explore.UI; // UiSettings
using WpfMessageBox = System.Windows.MessageBox; // WPF��MessageBox�ɖ����G�C���A�X

namespace Explore
{
    public partial class SettingsWindow : Window
    {
        // %APPDATA%\Explore\gemini_api_key.txt
        private readonly string _appDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Explore");
        private string ApiKeyFilePath => Path.Combine(_appDataDir, "gemini_api_key.txt");

        public SettingsWindow()
        {
            InitializeComponent();

            // ������ UI �ݒ�i�C���f�b�N�X�n�j�𔽉f
            try
            {
                AutoIndexBox.IsChecked = UiSettings.Instance.AutoIndexOnSelect;
                IncludeSubsBox.IsChecked = UiSettings.Instance.IncludeSubfolders;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"�ݒ�̓ǂݍ��݂Ɏ��s���܂����B\n{ex.Message}",
                    "�ݒ�",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // ������ API �L�[��ǂݍ���ŕ\���i�ۑ�������������j
            LoadExistingGeminiKey();
        }

        // ===== OK / Cancel =====

        private void OnOK(object? sender, RoutedEventArgs e)
        {
            try
            {
                // 1) �C���f�b�N�X�n
                UiSettings.Instance.AutoIndexOnSelect = AutoIndexBox.IsChecked == true;
                UiSettings.Instance.IncludeSubfolders = IncludeSubsBox.IsChecked == true;
                UiSettings.Instance.Save();
                UiSettings.Instance.RaiseChanged();

                // 2) Gemini API �L�[�ۑ�
                var key = GetApiKeyInput().Trim();

                if (StoreEnvRadio.IsChecked == true)
                {
                    SaveToEnvironment(key);
                }
                else // StoreFileRadio
                {
                    SaveToFile(key);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this,
                    $"�ݒ�̕ۑ��Ɏ��s���܂����B\n{ex.Message}",
                    "�ݒ�",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ====== API �L�[�\���ؑ� ======
        private void OnShowApiChecked(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (ShowApiSwitch.IsChecked == true)
                {
                    // Password -> Plain �֓������ĕ\��
                    ApiKeyBoxPlain.Text = ApiKeyBox.Password;
                    ApiKeyBoxPlain.Visibility = Visibility.Visible;
                    ApiKeyBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Plain -> Password �֓������ĉB��
                    ApiKeyBox.Password = ApiKeyBoxPlain.Text;
                    ApiKeyBox.Visibility = Visibility.Visible;
                    ApiKeyBoxPlain.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // �������Ȃ��i�����ڂ̐ؑւ����j
            }
        }

        // ====== �ǂݍ��݁E�ۑ����� ======

        private void LoadExistingGeminiKey()
        {
            string? fileKey = null;
            if (File.Exists(ApiKeyFilePath))
            {
                try
                {
                    fileKey = File.ReadAllText(ApiKeyFilePath, Encoding.UTF8).Trim();
                }
                catch { /* �ǂ߂Ȃ��ꍇ�͖��� */ }
            }

            string? envUser = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            string? envProc = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Process);
            string? envKey = !string.IsNullOrWhiteSpace(envUser) ? envUser : envProc;

            // �D�揇�ʁF�t�@�C�� > ���ϐ��i���[�U�[/�v���Z�X�j
            if (!string.IsNullOrEmpty(fileKey))
            {
                SetApiKeyInput(fileKey);
                StoreFileRadio.IsChecked = true;
                StoreEnvRadio.IsChecked = false;
                ApiSourceNote.Text = $"����: �t�@�C���i{ApiKeyFilePath}�j����ǂݍ��݂܂����B";
            }
            else if (!string.IsNullOrEmpty(envKey))
            {
                SetApiKeyInput(envKey);
                StoreEnvRadio.IsChecked = true;
                StoreFileRadio.IsChecked = false;
                ApiSourceNote.Text = "����: ���[�U�[���ϐ� GEMINI_API_KEY ����ǂݍ��݂܂����B";
            }
            else
            {
                SetApiKeyInput(string.Empty);
                StoreEnvRadio.IsChecked = true; // ����͊��ϐ��ɕۑ�
                ApiSourceNote.Text = "����: �ۑ����ꂽ�L�[�͌�����܂���B";
            }
        }

        private void SaveToEnvironment(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                // �폜�i�N���A�j
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.Process);
                ApiSourceNote.Text = "���ϐ� GEMINI_API_KEY ���폜���܂����B";
                return;
            }

            // ���[�U�[���ϐ� + �J�����g�v���Z�X�ɔ��f
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
            ApiSourceNote.Text = "���ϐ� GEMINI_API_KEY �ɕۑ����܂����i���̃A�v���ł͒����ɗL���j�B";
        }

        private void SaveToFile(string key)
        {
            // %APPDATA%\Explore\gemini_api_key.txt �ɕۑ��i��Ȃ�폜�j
            Directory.CreateDirectory(_appDataDir);

            if (string.IsNullOrEmpty(key))
            {
                if (File.Exists(ApiKeyFilePath))
                    File.Delete(ApiKeyFilePath);
                ApiSourceNote.Text = $"�t�@�C���i{ApiKeyFilePath}�j���폜���܂����B";
                return;
            }

            File.WriteAllText(ApiKeyFilePath, key, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                // �B��������t����i���s���Ă��v���I�łȂ��j
                var attr = File.GetAttributes(ApiKeyFilePath);
                File.SetAttributes(ApiKeyFilePath, attr | FileAttributes.Hidden);
            }
            catch { /* ignore */ }

            // ���v���Z�X�ɂ����f���Ă����g����悤�ɂ���
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
            ApiSourceNote.Text = $"�t�@�C���i{ApiKeyFilePath}�j�ɕۑ����܂����i���̃A�v���ł͒����ɗL���j�B";
        }

        // ���͗��̎��o���E�ݒ�i�\���ؑւɑΉ��j
        private string GetApiKeyInput()
        {
            return (ShowApiSwitch.IsChecked == true) ? ApiKeyBoxPlain.Text : ApiKeyBox.Password;
        }

        private void SetApiKeyInput(string value)
        {
            ApiKeyBox.Password = value ?? string.Empty;
            ApiKeyBoxPlain.Text = value ?? string.Empty;
        }
    }
}

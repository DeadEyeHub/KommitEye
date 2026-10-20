using System.Windows;
using System.Windows.Controls;
using KommitEye.Models;
using KommitEye.Services;
using Controls = System.Windows.Controls;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

public partial class MainWindow
{
    private Window? _settingsWindow;
    private string? _settingsSection;
    private bool _settingsSaving;
    private bool _resetPromptPending;
    private bool _loadingProfileList;
    private string? _loadedProfileName;

    private void CaptureModelDraft()
    {
        if (_settingsSection != "models" || _loadingProfileList || _loadedProfileName is null) return;
        var profile = ReadProfileFromForm();
        var index = _models.Profiles.FindIndex(p => p.Name == _loadedProfileName);
        if (index >= 0) _models.Profiles[index] = profile;
        else _models.Profiles.Add(profile);
    }
    private readonly Dictionary<string, string?> _pendingMailConnections = new();
    private string? _draftMailSender;

    private void InitializeSettingsWindows()
    {
        SaveProfileButton.Visibility = Visibility.Collapsed;
        SaveCustomPromptButton.Visibility = Visibility.Collapsed;
        MainTabs.SelectedItem = DashboardTab;
        RefreshSettingsMenu();
    }

    private void RefreshSettingsMenu()
    {
        if (SettingsButtons is null) return;
        ProjectSettingsButton.Content = Localization.Text("Project", "Проект");
        ModelsSettingsButton.Content = Localization.Text("Models", "Модели");
        MailSettingsButton.Content = Localization.Text("Mail", "Почта");
        ((TextBlock)PromptSettingsButton.Content).Text = Localization.Text("System prompt", "Системный промпт");
        JournalSettingsButton.Content = Localization.Text("Journal", "Журнал");
        ReportsButton.Content = Localization.Text("Reports", "Отчёты");
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Controls.Button { Tag: string section }) OpenSettingsWindow(section);
    }

    private void OpenSettingsWindow(string section)
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        var tab = section switch { "project" => ProjectTab, "models" => ModelsTab, "mail" => MailTab,
            "prompt" => PromptTab, "journal" => LogTab, _ => throw new ArgumentException(nameof(section)) };
        _pendingSettings.Clear();
        _settingsSection = section;
        _draftMailSender = _mail.Settings.Provider;
        var content = (FrameworkElement)tab.Content;
        tab.Content = null;
        var window = new Window { Owner = this, Title = $"KommitEye — {tab.Header}",
            Width = 960, Height = 740, MinWidth = 880, MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"), Icon = Icon };
        _settingsWindow = window;
        var layout = new Grid { Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush") };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(content);
        var footer = new Border { Padding = new Thickness(20, 16, 20, 16), BorderThickness = new Thickness(0,1,0,0),
            BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush") };
        Grid.SetRow(footer, 1);
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Controls.Button Make(string text) => new() { Content = text, MinWidth = 100, Padding = new Thickness(14,7,14,7), Margin = new Thickness(8,0,0,0) };
        var apply = Make(Localization.Text("Apply changes", "Применить изменения"));
        var ok = Make("OK");
        ok.Style = (Style)FindResource("PrimaryButton");
        var cancel = Make(Localization.Text("Cancel", "Отмена"));
        ok.IsDefault = true; cancel.IsCancel = true;
        if (section != "journal") buttons.Children.Add(apply);
        buttons.Children.Add(ok);
        if (section != "journal") buttons.Children.Add(cancel);
        footer.Child = buttons; layout.Children.Add(footer); window.Content = layout;
        async Task Save(bool close)
        {
            if (_settingsSaving || _mailAccountBusy) return;
            _settingsSaving = true;
            apply.IsEnabled = ok.IsEnabled = cancel.IsEnabled = false;
            content.IsEnabled = false;
            try
            {
                if (section != "journal") await ApplySettingsSectionAsync(section);
                if (close) { _settingsSaving = false; window.Close(); }
            }
            catch (Exception error) { System.Windows.MessageBox.Show(window, error.Message, "KommitEye", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                _settingsSaving = false; content.IsEnabled = true;
                apply.IsEnabled = ok.IsEnabled = cancel.IsEnabled = true;
            }
        }
        apply.Click += async (_, _) => await Save(false);
        ok.Click += async (_, _) => await Save(true);
        cancel.Click += (_, _) => window.Close();
        window.Closing += (_, args) =>
        {
            if (_settingsSaving || _mailAccountBusy) { args.Cancel = true; _mailAuthorizationCancellation?.Cancel(); }
        };
        try { window.ShowDialog(); }
        finally
        {
            layout.Children.Remove(content);
            tab.Content = content;
            _settingsWindow = null;
            _settingsSection = null;
            RestoreSavedSettings(section);
            _pendingSettings.Clear();
        }
    }

    private async Task ApplySettingsSectionAsync(string section)
    {
        if (section == "mail")
        {
            if (_mailAccountBusy) throw new InvalidOperationException(Localization.Text("Wait for the mail operation.", "Дождитесь завершения операции с почтой."));
            using var cancellation = new CancellationTokenSource();
            _mailAuthorizationCancellation = cancellation;
            try
            {
                foreach (var (provider, configuration) in _pendingMailConnections.ToArray())
                {
                    if (configuration is null) await _mail.DisconnectAsync(provider, cancellation.Token);
                    else await _mail.ConnectAsync(provider, configuration, cancellation.Token);
                    _pendingMailConnections.Remove(provider);
                }
                foreach (var apply in _applyMailDrafts.Values) apply();
            }
            finally { _mailAuthorizationCancellation = null; foreach (var refresh in _refreshMailButtons.Values) refresh(); }
            RefreshMailAccountStatus();
            return;
        }
        _applyingChanges = true;
        _configuration.WritesEnabled = true;
        try
        {
            switch (section)
            {
                case "project":
                    var language = LanguageComboBox.SelectedItem as LanguageOption;
                    var oldLanguage = Localization.Language;
                    try
                    {
                        if (language is not null) Localization.SetLanguage(language.Code);
                        SaveProjectDraft();
                    }
                    catch { Localization.SetLanguage(oldLanguage); throw; }
                    _runner.UpdateSettings(_configuration.LoadSettings());
                    PromptTextBox.Text = _configuration.LoadPrompt();
                    ApplyLanguage();
                    break;
                case "models":
                    CaptureModelDraft();
                    PersistModelProfile(ReadProfileFromForm());
                    break;
                case "prompt":
                    var builtIn = AppPaths.ReadTemplate(Localization.Language == "ru" ? "system-prompt.ru.example.txt" : "system-prompt.example.txt");
                    if (_resetPromptPending && PromptTextBox.Text == builtIn) _configuration.ResetPrompt();
                    else _configuration.SavePrompt(PromptTextBox.Text);
                    _resetPromptPending = false;
                    break;
            }
            _pendingSettings.Clear();
        }
        finally { _configuration.WritesEnabled = false; _applyingChanges = false; }
    }

    private void RestoreSavedSettings(string section)
    {
        _configuration.WritesEnabled = false;
        switch (section)
        {
            case "project":
                _repositoryVersion++; // Ignore requests started by the discarded draft.
                var settings = _configuration.LoadSettings();
                _loadingLanguage = true;
                LanguageComboBox.SelectedItem = LanguageComboBox.Items.Cast<LanguageOption>().First(x => x.Code == settings.Language);
                _loadingLanguage = false;
                _authenticationNeedsDetection = settings.GitAuthenticationMode == "auto";
                LoadAuthenticationOptions(_authenticationNeedsDetection ? "ssh-agent" : settings.GitAuthenticationMode);
                LoadSettings(settings);
                break;
            case "models": _models = _configuration.LoadModels(); LoadProfiles(); break;
            case "prompt": _resetPromptPending = false; PromptTextBox.Text = _configuration.LoadPrompt(); break;
            case "mail":
                _pendingMailConnections.Clear(); _draftMailSender = null;
                _mailDrafts.Clear(); _captureMailDrafts.Clear(); _applyMailDrafts.Clear();
                _googleClientFile = _microsoftClientId = _microsoftTenantId = "";
                BuildMailPanel();
                break;
        }
    }
}

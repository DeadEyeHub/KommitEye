using System.Windows;
using KommitEye.Services;
using Controls = System.Windows.Controls;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

public partial class MainWindow
{
    private Controls.TextBlock? _mailCounts, _mailAccountStatus;
    private readonly List<Controls.TextBlock> _mailCountLabels = [];
    private readonly Dictionary<string, Action> _refreshMailButtons = new();
    private CancellationTokenSource? _mailAuthorizationCancellation;
    private bool _mailAccountBusy;
    private string _googleClientFile = "";

    private readonly Dictionary<string, Controls.TextBlock> _mailStatuses = new();
    private string _mailProviderTab = "gmail";
    private string _microsoftClientId = "";
    private string _microsoftTenantId = "";
    private readonly Dictionary<string, Action> _applyMailDrafts = new();
    private readonly Dictionary<string, Action> _captureMailDrafts = new();
    private readonly Dictionary<string, MailSettings> _mailDrafts = new();
    private void CaptureMailDrafts() { foreach (var capture in _captureMailDrafts.Values) capture(); }
    private void RefreshMailAccountStatus()
    {
        foreach (var (provider, label) in _mailStatuses)
        {
            var account = _mail.GetAccount(provider);
            var settings = _mail.GetSettings(provider);
            label.Text = account is null
                ? _mail.GetStatusError(provider) ?? Localization.Text("Account is not connected.", "Аккаунт не подключён.")
                : account.Address + (account.NeedsAuthorization || settings.AuthorizationRequired
                    ? Localization.Text(" — reconnect to resume sending.", " — подключите заново для возобновления отправки.")
                    : Localization.Text(" — connected.", " — подключён.")) +
                    (_mail.Settings.Provider == provider ? Localization.Text(" Active sender.", " Активный отправитель.") : "");
        }
    }

    private static string FindGoogleClientFile()
    {
        var folders = new List<string> { Path.Combine(Environment.CurrentDirectory, "authMail") };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && folders.Count < 7; directory = directory.Parent)
            folders.Add(Path.Combine(directory.FullName, "authMail"));
        foreach (var folder in folders.Distinct())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                var files = Directory.GetFiles(folder, "client_secret_*.json");
                if (files.Length == 1) return files[0];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return "";
    }

    private void BuildMailPanel()
    {
        if (_mail is null || _mailAccountBusy) return;
        MailTab.Header = Localization.Text("Mail", "Почта");
        MailPanel.Children.Clear();
        _mailStatuses.Clear();
        _refreshMailButtons.Clear();
        _mailCountLabels.Clear();
        var tabs = new Controls.TabControl();
        foreach (var provider in new[] { "gmail", "outlook", "smtp" })
        {
            var panel = new Controls.StackPanel { Margin = new Thickness(12), Tag = "mail:" + provider };
            var tab = new Controls.TabItem { Header = provider == "gmail" ? "Google" : provider == "smtp" ? "SMTP" : "Outlook", Content = panel, Tag = provider };
            tabs.Items.Add(tab);
            BuildMailProviderPanel(panel, provider);
            if (_mailProviderTab == provider) tabs.SelectedItem = tab;
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, tabs) && tabs.SelectedItem is Controls.TabItem tab) _mailProviderTab = (string)tab.Tag;
        };
        MailPanel.Children.Add(tabs);
    }

    private void BuildMailProviderPanel(Controls.StackPanel MailPanel, string provider)
    {
        var google = provider == "gmail";
        var smtp = provider == "smtp";
        var settings = _mailDrafts.GetValueOrDefault(provider) ?? _mail.GetSettings(provider);
        var smtpSettings = settings.Clone();
        MailPanel.Children.Add(new Controls.TextBlock { Text = smtp ? "SMTP" : google ? "Google · Gmail API" : "Outlook · Microsoft Graph", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _mailAccountStatus = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
        MailPanel.Children.Add(_mailAccountStatus);
        if (!smtp) _mailStatuses[provider] = _mailAccountStatus;
        RefreshMailAccountStatus();
        Controls.TextBox Field(string en, string ru, string value)
        {
            MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(en, ru), Margin = new Thickness(0, 6, 0, 3) });
            var box = new Controls.TextBox { Text = value, Width = 600, MaxWidth = 600, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            MailPanel.Children.Add(box);
            return box;
        }
        if (_googleClientFile.Length == 0 && _mail.GoogleAccount is null) _googleClientFile = FindGoogleClientFile();
        var clientFile = Field(google ? "Google Desktop OAuth JSON" : "Microsoft application (client) ID",
            google ? "OAuth JSON Google (Desktop app)" : "Идентификатор приложения Microsoft (client ID)", google ? _googleClientFile : _microsoftClientId);
        var tenantField = google ? null : Field("Microsoft Directory (tenant) ID", "Microsoft Directory (tenant) ID организации", _microsoftTenantId);
        MailPanel.Children.Add(new Controls.TextBlock { Text = google ? Localization.Text(
            "Choose the downloaded JSON for the first connection. Leave blank to reuse saved client settings when reconnecting. Sign-in opens in your browser; no email password is stored.",
            "Для первого подключения выберите скачанный JSON. При повторном входе поле можно оставить пустым для использования сохранённых настроек. Вход откроется в браузере; пароль почты не сохраняется.") : Localization.Text(
            "Copy Application (client) ID and Directory (tenant) ID from Overview. Do not use Secret ID or client secret. For personal/multitenant apps use common as tenant. Configure Desktop redirect http://localhost and delegated User.Read + Mail.Send. Blank fields reuse saved settings (new connections default to common).",
            "Скопируйте Application (client) ID и Directory (tenant) ID из Overview. Secret ID и секрет не нужны. Для личных аккаунтов / multitenant укажите common. Настройте Desktop redirect http://localhost и delegated User.Read + Mail.Send. Пустые поля используют сохранённые настройки (новое подключение — common)."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var accountButtons = new Controls.WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
        Controls.Button Button(string en, string ru) => new() { Content = Localization.Text(en, ru), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        var browse = Button("Browse…", "Выбрать файл…");
        var connect = Button(google ? "Connect / reconnect Google" : "Connect / reconnect Microsoft", google ? "Подключить Google / войти заново" : "Подключить Microsoft / войти заново");
        var disconnect = Button("Disconnect", "Отключить");
        var cancel = Button("Cancel sign-in", "Отменить вход");
        if (google) accountButtons.Children.Add(browse); accountButtons.Children.Add(connect); accountButtons.Children.Add(disconnect); accountButtons.Children.Add(cancel);
        MailPanel.Children.Add(accountButtons);
        if (smtp)
        {
            // SMTP connection details live in their own dialog; notification options stay in Mail.
            foreach (var child in MailPanel.Children.OfType<UIElement>().Skip(2).ToArray()) child.Visibility = Visibility.Collapsed;
            var configure = Button("SMTP server settings…", "Настройки SMTP-сервера…");
            configure.Click += (_, _) =>
            {
                var edited = EditSmtpSettings(smtpSettings);
                if (edited is null) return;
                smtpSettings = edited;
                _draftMailSender = provider;
                ((Controls.TextBlock)MailPanel.Children[1]).Text = smtpSettings.Host;
            };
            MailPanel.Children.Add(configure);
        }
        var enabled = new Controls.CheckBox { Content = Localization.Text("Enable email notifications", "Включить почтовые уведомления"),
            IsChecked = settings.Enabled, Margin = new Thickness(0, 0, 0, 12) };
        var toAuthor = new Controls.CheckBox { Content = Localization.Text("Also send to the commit author", "Отправлять также автору коммита"),
            IsChecked = settings.SendToAuthor, Margin = new Thickness(0, 0, 0, 8),
            ToolTip = Localization.Text("Uses the email from Git metadata. Enable only for trusted repositories.", "Используется email из Git. Включайте только для доверенных репозиториев.") };
        var noBugs = new Controls.CheckBox { Content = Localization.Text("Send even when no bugs are found", "Отправлять и при отсутствии ошибок"),
            IsChecked = settings.SendWithoutBugs, Margin = new Thickness(0, 0, 0, 8) };
        MailPanel.Children.Add(enabled); MailPanel.Children.Add(toAuthor); MailPanel.Children.Add(noBugs);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(
            "Failed reviews are sent only to the recipients below, without adding the commit author.",
            "При сбое проверки письмо отправляется только получателям ниже, без добавления автора коммита."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var recipients = Field("Recipients (separate with ;)", "Получатели (через ;)", settings.Recipients);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text("Report attachment format", "Формат вложения отчёта"), Margin = new Thickness(0, 6, 0, 3) });
        var attachmentFormat = new Controls.ComboBox { ItemsSource = new[] { "html", "md" }, SelectedItem = settings.AttachmentFormat,
            Width = 120, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        MailPanel.Children.Add(attachmentFormat);
        var buttons = new Controls.WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var save = Button("Use this account", "Использовать аккаунт");
        var test = Button("Send test email", "Отправить тестовое письмо");
        var retry = Button("Retry failed mail", "Повторить неотправленные");
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(retry);
        MailPanel.Children.Add(buttons);
        var status = new Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Text = _mail.LoadError ?? "" };
        MailPanel.Children.Add(status);
        _mailCounts = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        MailPanel.Children.Add(_mailCounts);
        _mailCountLabels.Add(_mailCounts);
        void UpdateButtons()
        {
            var connected = smtp || _mail.GetAccount(provider) is not null;
            browse.IsEnabled = connect.IsEnabled = clientFile.IsEnabled = !_mailAccountBusy;
            if (tenantField is not null) tenantField.IsEnabled = !_mailAccountBusy;
            disconnect.IsEnabled = connected && !_mailAccountBusy;
            cancel.IsEnabled = _mailAccountBusy && _mailAuthorizationCancellation is not null;
            save.IsEnabled = test.IsEnabled = retry.IsEnabled = connected && !_mailAccountBusy;
            RefreshMailAccountStatus();
            if (smtp) ((Controls.TextBlock)MailPanel.Children[1]).Text = string.IsNullOrWhiteSpace(smtpSettings.Host)
                ? Localization.Text("SMTP server is not configured.", "SMTP-сервер не настроен.") : smtpSettings.Host +
                    (_mail.Settings.Provider == provider ? Localization.Text(" — active sender.", " — активный отправитель.") : "");
        }
        MailSettings Read()
        {
            if (smtp)
            {
                var value = smtpSettings.Clone();
                value.Enabled = enabled.IsChecked == true;
                value.SendToAuthor = toAuthor.IsChecked == true;
                value.SendWithoutBugs = noBugs.IsChecked == true;
                value.Recipients = recipients.Text.Trim();
                value.AttachmentFormat = attachmentFormat.SelectedItem as string ?? "html";
                return value;
            }
            var account = _mail.GetAccount(provider) ?? throw new MailAuthorizationException();
            return new MailSettings { Provider = provider, AccountId = account.Id, From = account.Address,
                Enabled = enabled.IsChecked == true, SendToAuthor = toAuthor.IsChecked == true,
                SendWithoutBugs = noBugs.IsChecked == true, Recipients = recipients.Text.Trim(),
                AttachmentFormat = attachmentFormat.SelectedItem as string ?? "html" };
        }
        MailSettings Draft() => new() { Provider = provider, Enabled = enabled.IsChecked == true,
            SendToAuthor = toAuthor.IsChecked == true, SendWithoutBugs = noBugs.IsChecked == true,
            Recipients = recipients.Text.Trim(), AttachmentFormat = attachmentFormat.SelectedItem as string ?? "html" };
        _captureMailDrafts[provider] = () =>
        {
            if (google) _googleClientFile = clientFile.Text.Trim(); else if (!smtp) { _microsoftClientId = clientFile.Text.Trim(); _microsoftTenantId = tenantField!.Text.Trim(); }
            _mailDrafts[provider] = smtp ? Read() : Draft();
        };
        _applyMailDrafts[provider] = () =>
        {
            _captureMailDrafts[provider]();
            if (smtp || _mail.GetAccount(provider) is not null) _mail.SaveSettings(Read(), activate: _draftMailSender == provider);
            else if (enabled.IsChecked == true) throw new MailAuthorizationException();
            status.Text = Localization.Text("Mail settings applied.", "Настройки почты применены.");
            RefreshMailAccountStatus();
        };
        // All preferences, including checkboxes, remain drafts until Apply / OK.
        string Error(Exception error) => error is MailAuthorizationException or MailProviderException ? error.Message :
            error is OperationCanceledException ? Localization.Text("Operation canceled or timed out.", "Операция отменена или истекло время ожидания.") :
            Localization.Format("Mail operation failed ({0}). Check the connection settings and account permissions.",
                "Операция с почтой не выполнена ({0}). Проверьте настройки подключения и разрешения аккаунта.", error.GetType().Name);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Google OAuth JSON (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(_settingsWindow ?? this) == true)
            {
                clientFile.Text = _googleClientFile = dialog.FileName;
                MarkSettingsChanged("mail:" + provider);
            }
        };
        cancel.Click += (_, _) => _mailAuthorizationCancellation?.Cancel();
        connect.Click += (_, _) =>
        {
            if (_mailAccountBusy) return;
            _captureMailDrafts[provider]();
            var configuration = google ? clientFile.Text.Trim() : System.Text.Json.JsonSerializer.Serialize(new
            { clientId = clientFile.Text.Trim(), tenantId = tenantField!.Text.Trim() });
            _pendingMailConnections[provider] = configuration;
            _draftMailSender = provider;
            status.Text = Localization.Text("Click Apply changes or OK to sign in and save this connection.",
                "Нажмите «Применить изменения» или OK для входа и сохранения подключения.");
        };
        disconnect.Click += (_, _) =>
        {
            if (_mailAccountBusy) return;
            _pendingMailConnections[provider] = null;
            enabled.IsChecked = false;
            status.Text = Localization.Text("Account will be disconnected after Apply changes or OK.",
                "Аккаунт будет отключён после «Применить изменения» или OK.");
        };
        save.Click += (_, _) =>
        {
            _draftMailSender = provider;
            status.Text = Localization.Text("Sender selected. Click Apply changes or OK to save.",
                "Отправитель выбран. Нажмите «Применить изменения» или OK для сохранения.");
        };
        test.Click += async (_, _) =>
        {
            if (_mailAccountBusy) return;
            _mailAccountBusy = true; UpdateButtons();
            try
            {
                status.Text = Localization.Text("Sending test email…", "Отправляется тестовое письмо…");
                await _mail.TestAsync(Read(), CancellationToken.None, saveSettings: false);
                status.Text = Localization.Text("Test email accepted by the mail provider; inbox delivery is not confirmed.", "Тестовое письмо принято почтовым сервисом; доставка в ящик не подтверждена.");
            }
            catch (Exception error) { status.Text = Error(error); }
            finally { _mailAccountBusy = false; UpdateButtons(); }
        };
        retry.Click += (_, _) =>
        {
            try { if (_mail.Settings.Provider != provider) throw new MailProviderException(Localization.Text("Save / use this account first.", "Сначала сохраните / выберите этот аккаунт."));
                _mail.RetryFailed(); status.Text = Localization.Text("Failed mail for this account queued again.", "Неотправленные письма этого аккаунта возвращены в очередь."); }
            catch (Exception error) { status.Text = Error(error); }
        };
        _refreshMailButtons[provider] = UpdateButtons;
        UpdateButtons();
    }

    private MailSettings? EditSmtpSettings(MailSettings settings)
    {
        MailSettings? result = null;
        var window = CreateSmtpSettingsWindow(settings, value => result = value);
        window.ShowDialog();
        return result;
    }

    private Window CreateSmtpSettingsWindow(MailSettings settings, Action<MailSettings> accept)
    {
        var window = new Window { Owner = _settingsWindow ?? this,
            Title = Localization.Text("SMTP server settings", "Настройки SMTP-сервера"),
            Width = 540, Height = 640, MinWidth = 440, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = System.Windows.SystemColors.WindowBrush };
        var layout = new Controls.DockPanel { Margin = new Thickness(20), Background = System.Windows.SystemColors.WindowBrush };
        var footer = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Controls.Button { Content = "OK", IsDefault = true, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Controls.Button { Content = Localization.Text("Cancel", "Отмена"), IsCancel = true,
            MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        footer.Children.Add(ok); footer.Children.Add(cancel);
        Controls.DockPanel.SetDock(footer, Controls.Dock.Bottom); layout.Children.Add(footer);
        var fields = new Controls.StackPanel();
        layout.Children.Add(new Controls.ScrollViewer { Content = fields, VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto });
        window.Content = layout;
        void Label(string en, string ru) => fields.Children.Add(new Controls.TextBlock {
            Text = Localization.Text(en, ru), Margin = new Thickness(0, 10, 0, 4) });
        Controls.TextBox Field(string en, string ru, string value)
        {
            Label(en, ru);
            var input = new Controls.TextBox { Text = value };
            fields.Children.Add(input); return input;
        }
        var host = Field("SMTP host", "SMTP-сервер", settings.Host);
        var port = Field("Port", "Порт", settings.Port.ToString());
        Label("Connection security", "Защита соединения");
        var security = new Controls.ComboBox { ItemsSource = new[] { "StartTls", "SslOnConnect", "None" }, SelectedItem = settings.Security };
        fields.Children.Add(security);
        var username = Field("Username (optional)", "Имя пользователя (необязательно)", settings.Username);
        Label("Password / app password", "Пароль / пароль приложения");
        var password = new Controls.PasswordBox();
        fields.Children.Add(password);
        fields.Children.Add(new Controls.TextBlock { Text = Localization.Text(
            "Leave blank to keep the saved password.", "Оставьте пустым, чтобы сохранить текущий пароль."), TextWrapping = TextWrapping.Wrap });
        var clear = new Controls.CheckBox { Content = Localization.Text("Clear saved password", "Удалить сохранённый пароль"), Margin = new Thickness(0, 6, 0, 0) };
        fields.Children.Add(clear);
        var from = Field("Sender email", "E-mail отправителя", settings.From);
        fields.Children.Add(new Controls.TextBlock { Text = Localization.Text(
            "Click Apply changes or OK in Mail to save these settings.",
            "Для сохранения нажмите «Применить изменения» или OK в окне почты."),
            Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        var error = new Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        fields.Children.Add(error);
        ok.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var number)) throw new InvalidOperationException(Localization.Text("Enter a valid SMTP port.", "Укажите корректный SMTP-порт."));
                var value = settings.Clone();
                value.Provider = "smtp"; value.AuthorizationRequired = false;
                value.Host = host.Text.Trim(); value.Port = number;
                value.Security = security.SelectedItem as string ?? "StartTls";
                value.Username = username.Text.Trim(); value.From = from.Text.Trim();
                if (clear.IsChecked == true) value.SetPassword("");
                else if (password.Password.Length > 0) value.SetPassword(password.Password);
                // Recipients are edited in the parent Mail window.
                var validation = value.Clone(); validation.Recipients = ""; validation.SendToAuthor = true; validation.Validate();
                accept(value); window.DialogResult = true;
            }
            catch (Exception exception) { error.Text = exception is InvalidOperationException ? exception.Message :
                Localization.Text("Check the SMTP settings and sender address.", "Проверьте настройки SMTP и адрес отправителя."); }
        };
        return window;
    }
}

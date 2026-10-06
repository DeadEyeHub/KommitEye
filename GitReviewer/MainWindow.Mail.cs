using System.Windows;
using GitReviewer.Services;
using Controls = System.Windows.Controls;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    private Controls.TextBlock? _mailCounts, _mailAccountStatus;
    private readonly List<Controls.TextBlock> _mailCountLabels = [];
    private CancellationTokenSource? _mailAuthorizationCancellation;
    private bool _mailAccountBusy;
    private string _googleClientFile = "";

    private readonly Dictionary<string, Controls.TextBlock> _mailStatuses = new();
    private string _mailProviderTab = "gmail";
    private string _microsoftClientId = "";
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
        _mailCountLabels.Clear();
        var tabs = new Controls.TabControl();
        foreach (var provider in new[] { "gmail", "outlook" })
        {
            var panel = new Controls.StackPanel { Margin = new Thickness(12), Tag = "mail:" + provider };
            var tab = new Controls.TabItem { Header = provider == "gmail" ? "Google" : "Outlook", Content = panel, Tag = provider };
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
        var settings = _mailDrafts.GetValueOrDefault(provider) ?? _mail.GetSettings(provider);
        MailPanel.Children.Add(new Controls.TextBlock { Text = google ? "Google · Gmail API" : "Outlook · Microsoft Graph", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _mailAccountStatus = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
        MailPanel.Children.Add(_mailAccountStatus);
        _mailStatuses[provider] = _mailAccountStatus;
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
        MailPanel.Children.Add(new Controls.TextBlock { Text = google ? Localization.Text(
            "Choose the downloaded JSON for the first connection. Leave blank to reuse saved client settings when reconnecting. Sign-in opens in your browser; no email password is stored.",
            "Для первого подключения выберите скачанный JSON. При повторном входе поле можно оставить пустым для использования сохранённых настроек. Вход откроется в браузере; пароль почты не сохраняется.") : Localization.Text(
            "Use a public Desktop app registered for personal and work/school accounts, with http://localhost redirect and delegated User.Read + Mail.Send permissions. No client secret is needed. Leave blank to reuse saved client settings.",
            "Укажите client ID Desktop-приложения для личных и рабочих аккаунтов, с redirect http://localhost и delegated-разрешениями User.Read + Mail.Send. Client secret не нужен. Пустое поле использует сохранённый client ID."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var accountButtons = new Controls.WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
        Controls.Button Button(string en, string ru) => new() { Content = Localization.Text(en, ru), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        var browse = Button("Browse…", "Выбрать файл…");
        var connect = Button(google ? "Connect / reconnect Google" : "Connect / reconnect Microsoft", google ? "Подключить Google / войти заново" : "Подключить Microsoft / войти заново");
        var disconnect = Button("Disconnect", "Отключить");
        var cancel = Button("Cancel sign-in", "Отменить вход");
        if (google) accountButtons.Children.Add(browse); accountButtons.Children.Add(connect); accountButtons.Children.Add(disconnect); accountButtons.Children.Add(cancel);
        MailPanel.Children.Add(accountButtons);
        var enabled = new Controls.CheckBox { Content = Localization.Text("Enable email notifications", "Включить почтовые уведомления"),
            IsChecked = settings.Enabled, Margin = new Thickness(0, 0, 0, 12) };
        var toAuthor = new Controls.CheckBox { Content = Localization.Text("Also send to the commit author", "Отправлять также автору коммита"),
            IsChecked = settings.SendToAuthor, Margin = new Thickness(0, 0, 0, 8),
            ToolTip = Localization.Text("Uses the email from Git metadata. Enable only for trusted repositories.", "Используется email из Git. Включайте только для доверенных репозиториев.") };
        var noBugs = new Controls.CheckBox { Content = Localization.Text("Send even when no bugs are found", "Отправлять и при отсутствии ошибок"),
            IsChecked = settings.SendWithoutBugs, Margin = new Thickness(0, 0, 0, 8) };
        MailPanel.Children.Add(enabled); MailPanel.Children.Add(toAuthor); MailPanel.Children.Add(noBugs);
        var recipients = Field("Recipients (separate with ;)", "Получатели (через ;)", settings.Recipients);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text("Report attachment format", "Формат вложения отчёта"), Margin = new Thickness(0, 6, 0, 3) });
        var attachmentFormat = new Controls.ComboBox { ItemsSource = new[] { "html", "md" }, SelectedItem = settings.AttachmentFormat,
            Width = 120, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        MailPanel.Children.Add(attachmentFormat);
        var buttons = new Controls.WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var save = Button("Save / use this account", "Сохранить / использовать аккаунт");
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
            var connected = _mail.GetAccount(provider) is not null;
            browse.IsEnabled = connect.IsEnabled = clientFile.IsEnabled = !_mailAccountBusy;
            disconnect.IsEnabled = connected && !_mailAccountBusy;
            cancel.IsEnabled = _mailAccountBusy && _mailAuthorizationCancellation is not null;
            save.IsEnabled = test.IsEnabled = retry.IsEnabled = connected && !_mailAccountBusy;
            RefreshMailAccountStatus();
        }
        MailSettings Read()
        {
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
            if (google) _googleClientFile = clientFile.Text.Trim(); else _microsoftClientId = clientFile.Text.Trim();
            _mailDrafts[provider] = Draft();
        };
        _applyMailDrafts[provider] = () =>
        {
            _captureMailDrafts[provider]();
            if (_mail.GetAccount(provider) is not null) _mail.SaveSettings(Read(), activate: false);
            else if (enabled.IsChecked == true) throw new MailAuthorizationException();
            status.Text = Localization.Text("Mail settings applied.", "Настройки почты применены.");
            RefreshMailAccountStatus();
        };
        // Checkbox actions apply on their own and never create an Apply changes notification.
        void CheckboxChanged(object sender, RoutedEventArgs args)
        {
            if (_mailAccountBusy) return;
            try
            {
                _captureMailDrafts[provider]();
                if (_mail.GetAccount(provider) is not null)
                {
                    var saved = _mail.GetSettings(provider);
                    var account = _mail.GetAccount(provider)!;
                    saved.Provider = provider; saved.AccountId = account.Id; saved.From = account.Address;
                    saved.Enabled = enabled.IsChecked == true; saved.SendToAuthor = toAuthor.IsChecked == true;
                    saved.SendWithoutBugs = noBugs.IsChecked == true;
                    _mail.SaveSettings(saved, activate: false);
                }
                else if (enabled.IsChecked == true) throw new MailAuthorizationException();
            }
            catch (Exception error)
            {
                status.Text = Error(error);
                var saved = _mail.GetSettings(provider);
                _mailAccountBusy = true;
                try
                {
                    enabled.IsChecked = saved.Enabled;
                    toAuthor.IsChecked = saved.SendToAuthor;
                    noBugs.IsChecked = saved.SendWithoutBugs;
                }
                finally { _mailAccountBusy = false; }
            }
        }
        foreach (var box in new[] { enabled, toAuthor, noBugs }) { box.Checked += CheckboxChanged; box.Unchecked += CheckboxChanged; }
        string Error(Exception error) => error is MailAuthorizationException or MailProviderException ? error.Message :
            error is OperationCanceledException ? Localization.Text("Operation canceled or timed out.", "Операция отменена или истекло время ожидания.") :
            Localization.Format("Mail operation failed ({0}). Check the OAuth client settings, connection and account permissions.",
                "Операция с почтой не выполнена ({0}). Проверьте настройки OAuth-клиента, соединение и разрешения аккаунта.", error.GetType().Name);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Google OAuth JSON (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
            {
                clientFile.Text = _googleClientFile = dialog.FileName;
                MarkSettingsChanged("mail:" + provider);
            }
        };
        cancel.Click += (_, _) => _mailAuthorizationCancellation?.Cancel();
        connect.Click += async (_, _) =>
        {
            if (_mailAccountBusy) return;
            _mailAccountBusy = true;
            using var cancellation = new CancellationTokenSource();
            _mailAuthorizationCancellation = cancellation;
            UpdateButtons();
            try
            {
                if (google) _googleClientFile = clientFile.Text.Trim(); else _microsoftClientId = clientFile.Text.Trim();
                status.Text = Localization.Text("Complete sign-in in your browser…", "Завершите вход в браузере…");
                await _mail.ConnectAsync(provider, clientFile.Text.Trim(), cancellation.Token);
                enabled.IsChecked = _mail.Settings.Enabled;
                status.Text = Localization.Text("Connected. Choose recipients, enable notifications and save settings.", "Аккаунт подключён. Укажите получателей, включите уведомления и сохраните настройки.");
            }
            catch (Exception error) { status.Text = Error(error); }
            finally { _mailAuthorizationCancellation = null; _mailAccountBusy = false; UpdateButtons(); }
        };
        disconnect.Click += async (_, _) =>
        {
            if (_mailAccountBusy) return;
            _mailAccountBusy = true; UpdateButtons();
            try
            {
                await _mail.DisconnectAsync(provider, CancellationToken.None);
                status.Text = Localization.Text("Disconnected. Local tokens removed. Queued reports are preserved for their original account.", "Аккаунт отключён. Локальные токены удалены. Очередь сохранена для исходного аккаунта.");
            }
            catch (Exception error) { status.Text = Error(error); }
            finally { enabled.IsChecked = false; _mailAccountBusy = false; UpdateButtons(); }
        };
        save.Click += (_, _) =>
        {
            try { _mail.SaveSettings(Read()); status.Text = Localization.Text("Mail settings saved.", "Настройки почты сохранены."); }
            catch (Exception error) { status.Text = Error(error); }
        };
        test.Click += async (_, _) =>
        {
            if (_mailAccountBusy) return;
            _mailAccountBusy = true; UpdateButtons();
            try
            {
                status.Text = Localization.Text("Sending test email…", "Отправляется тестовое письмо…");
                await _mail.TestAsync(Read(), CancellationToken.None);
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
        UpdateButtons();
    }
}

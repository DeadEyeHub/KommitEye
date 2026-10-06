using System.Windows;
using GitReviewer.Services;
using Controls = System.Windows.Controls;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    private Controls.TextBlock? _mailCounts, _mailAccountStatus;
    private CancellationTokenSource? _mailAuthorizationCancellation;
    private bool _mailAccountBusy;
    private string _googleClientFile = "";

    private void RefreshMailAccountStatus()
    {
        if (_mailAccountStatus is null) return;
        var account = _mail.GoogleAccount;
        _mailAccountStatus.Text = account is null
            ? _mail.GoogleStatusError ?? Localization.Text("Google account is not connected.", "Аккаунт Google не подключён.")
            : Localization.Text("Google account: ", "Аккаунт Google: ") + account.Address +
              (account.NeedsAuthorization || _mail.Settings.AuthorizationRequired
                  ? Localization.Text(" — reconnect to resume sending.", " — подключите заново для возобновления отправки.")
                  : Localization.Text(" — connected.", " — подключён."));
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
        var settings = _mail.Settings;
        MailPanel.Children.Add(new Controls.TextBlock { Text = "Google · Gmail API", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _mailAccountStatus = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
        MailPanel.Children.Add(_mailAccountStatus);
        RefreshMailAccountStatus();
        Controls.TextBox Field(string en, string ru, string value)
        {
            MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(en, ru), Margin = new Thickness(0, 6, 0, 3) });
            var box = new Controls.TextBox { Text = value, Width = 600, MaxWidth = 600, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            MailPanel.Children.Add(box);
            return box;
        }
        if (_googleClientFile.Length == 0 && _mail.GoogleAccount is null) _googleClientFile = FindGoogleClientFile();
        var clientFile = Field("Google Desktop OAuth JSON", "OAuth JSON Google (Desktop app)", _googleClientFile);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(
            "Choose the downloaded JSON for the first connection. Leave blank to reuse saved client settings when reconnecting. Sign-in opens in your browser; no email password is stored.",
            "Для первого подключения выберите скачанный JSON. При повторном входе поле можно оставить пустым для использования сохранённых настроек. Вход откроется в браузере; пароль почты не сохраняется."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var accountButtons = new Controls.WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
        Controls.Button Button(string en, string ru) => new() { Content = Localization.Text(en, ru), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        var browse = Button("Browse…", "Выбрать файл…");
        var connect = Button("Connect / reconnect Google", "Подключить Google / войти заново");
        var disconnect = Button("Disconnect", "Отключить");
        var cancel = Button("Cancel sign-in", "Отменить вход");
        accountButtons.Children.Add(browse); accountButtons.Children.Add(connect); accountButtons.Children.Add(disconnect); accountButtons.Children.Add(cancel);
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
        var save = Button("Save", "Сохранить");
        var test = Button("Send test email", "Отправить тестовое письмо");
        var retry = Button("Retry failed mail", "Повторить неотправленные");
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(retry);
        MailPanel.Children.Add(buttons);
        var status = new Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Text = _mail.LoadError ?? "" };
        MailPanel.Children.Add(status);
        _mailCounts = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        MailPanel.Children.Add(_mailCounts);
        void UpdateButtons()
        {
            var connected = _mail.GoogleAccount is not null;
            browse.IsEnabled = connect.IsEnabled = clientFile.IsEnabled = !_mailAccountBusy;
            disconnect.IsEnabled = connected && !_mailAccountBusy;
            cancel.IsEnabled = _mailAccountBusy && _mailAuthorizationCancellation is not null;
            save.IsEnabled = test.IsEnabled = retry.IsEnabled = connected && !_mailAccountBusy;
            RefreshMailAccountStatus();
        }
        MailSettings Read()
        {
            var account = _mail.GoogleAccount ?? throw new MailAuthorizationException();
            return new MailSettings { Provider = "gmail", AccountId = account.Id, From = account.Address,
                Enabled = enabled.IsChecked == true, SendToAuthor = toAuthor.IsChecked == true,
                SendWithoutBugs = noBugs.IsChecked == true, Recipients = recipients.Text.Trim(),
                AttachmentFormat = attachmentFormat.SelectedItem as string ?? "html" };
        }
        string Error(Exception error) => error is MailAuthorizationException or MailProviderException ? error.Message :
            error is OperationCanceledException ? Localization.Text("Operation canceled or timed out.", "Операция отменена или истекло время ожидания.") :
            Localization.Format("Mail operation failed ({0}). Check the JSON file, connection and account permissions.",
                "Операция с почтой не выполнена ({0}). Проверьте JSON, соединение и разрешения аккаунта.", error.GetType().Name);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Google OAuth JSON (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) clientFile.Text = _googleClientFile = dialog.FileName;
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
                _googleClientFile = clientFile.Text.Trim();
                status.Text = Localization.Text("Complete Google sign-in in your browser…", "Завершите вход в Google в браузере…");
                await _mail.ConnectGoogleAsync(_googleClientFile, cancellation.Token);
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
                await _mail.DisconnectGoogleAsync(CancellationToken.None);
                status.Text = Localization.Text("Disconnected. Queued reports are preserved for their original account.", "Аккаунт отключён. Очередь сохранена для исходного аккаунта.");
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
                status.Text = Localization.Text("Test email accepted by Gmail API; inbox delivery is not confirmed.", "Тестовое письмо принято Gmail API; доставка в ящик не подтверждена.");
            }
            catch (Exception error) { status.Text = Error(error); }
            finally { _mailAccountBusy = false; UpdateButtons(); }
        };
        retry.Click += (_, _) =>
        {
            try { _mail.RetryFailed(); status.Text = Localization.Text("Failed mail for this account queued again.", "Неотправленные письма этого аккаунта возвращены в очередь."); }
            catch (Exception error) { status.Text = Error(error); }
        };
        UpdateButtons();
    }
}

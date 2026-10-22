using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KommitEye;
using KommitEye.Services;

class Program
{
    [STAThread] static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-windows-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", root);
        // Load the same application resources as the real desktop entry point.
        // Constructing Application alone omits Theme.xaml and breaks StaticResource lookups.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/KommitEye;component/Theme.xaml", UriKind.Relative)
        });
        var window = new MainWindow();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        void Apply(string section) => ((Task)Call("ApplySettingsSectionAsync", section)!).GetAwaiter().GetResult();
        var count = 0;
        void Check(bool ok, string name) { if(!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var store = new ConfigurationStore();
        var projectFile = Path.Combine(root, "projects.json");
        var original = File.ReadAllText(projectFile);
        var interval = Field<TextBox>("IntervalTextBox");
        Field<CheckBox>("AutoStartCheckBox").IsChecked = true;
        interval.Text = "90";
        Field<CheckBox>("PullEnabledCheckBox").IsChecked = false;
        Call("SaveAuthenticationPreferences");
        Check(File.ReadAllText(projectFile) == original, "editing does not persist preferences");
        Apply("project");
        Check(store.LoadSettings().AutoStart && store.LoadSettings().PollIntervalSeconds == 90 && !store.LoadSettings().PullEnabled, "Apply saves fields and checkboxes");
        interval.Text = "invalid";
        try { Apply("project"); throw new Exception("invalid accepted"); } catch (InvalidOperationException) { }
        Check(store.LoadSettings().PollIntervalSeconds == 90, "failed validation preserves committed settings");
        Call("RestoreSavedSettings", "project");
        Check(interval.Text == "90", "Cancel restores last applied values");
        var prompt = Field<TextBox>("PromptTextBox");
        var oldPrompt = store.LoadPrompt();
        prompt.Text = "Draft prompt";
        Check(store.LoadPrompt() == oldPrompt, "prompt remains draft");
        Apply("prompt");
        Check(store.LoadPrompt() == "Draft prompt", "prompt applied");
        prompt.Text = "Discard me"; Call("RestoreSavedSettings", "prompt");
        Check(prompt.Text == "Draft prompt", "prompt canceled");
        var endpoint = Field<TextBox>("EndpointTextBox");
        var savedEndpoint = endpoint.Text;
        endpoint.Text = "https://draft.example.invalid/v1";
        Call("RestoreSavedSettings", "models");
        Check(endpoint.Text == savedEndpoint, "model edits canceled without saving");
        _ = Field<MailNotifications>("_mail").DisposeAsync();
        var mail = new MailNotifications(root, _ => {}, startWorker:false, providers:[new Provider("gmail"), new Provider("outlook")]);
        Set("_mail", mail); Call("BuildMailPanel");
        var mailPanel = Field<StackPanel>("MailPanel");
        var googlePanel = (StackPanel)((TabItem)((TabControl)mailPanel.Children[0]).Items[0]).Content;
        var recipients = googlePanel.Children.OfType<TextBox>().Last();
        recipients.Text = "recipient@example.invalid";
        googlePanel.Children.OfType<CheckBox>().First().IsChecked = true;
        Check(!File.Exists(Path.Combine(root,"mail-settings.json")), "mail checkbox and recipient do not save");
        Set("_draftMailSender", "gmail"); Apply("mail");
        Check(mail.Settings.Enabled && mail.Settings.Recipients == recipients.Text, "mail Apply persists preferences");
        Field<Dictionary<string,string?>>("_pendingMailConnections")["gmail"] = null;
        Field<Dictionary<string,string?>>("_pendingMailConnections")["outlook"] = "draft-client";
        Call("RestoreSavedSettings", "mail");
        Check(Field<Dictionary<string,string?>>("_pendingMailConnections").Count == 0,
            "Cancel discards queued OAuth and disconnect without provider calls");
        var tabs = Field<TabControl>("MainTabs");
        Check(ReferenceEquals(tabs.SelectedItem, Field<TabItem>("DashboardTab")), "main stays on dashboard");
        var settingsButtons = Field<UniformGrid>("SettingsButtons");
        var sections = new[] { "project", "models", "mail", "prompt", "journal" };
        var navigation = settingsButtons.Children.OfType<Button>().ToArray();
        Check(navigation.Where(b => b.Tag is string).Select(b => (string)b.Tag).Order()
            .SequenceEqual(sections.Order()), "all settings sections available as navigation buttons");
        var reportsButton = Field<Button>("ReportsButton");
        Check(navigation.Contains(reportsButton) && reportsButton.IsEnabled,
            "reports available as a separate navigation button");
        void Render(FrameworkElement surface, string name, double width, double height)
        {
            surface.Measure(new Size(width,height)); surface.Arrange(new Rect(0,0,width,height)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory,name+".png")); encoder.Save(file);
        }
        ((Grid)window.Content).Background = SystemColors.WindowBrush;
        Render((FrameworkElement)window.Content,"dashboard",940,660);
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        reportsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var reportsWindow = Field<ReportsWindow>("_reportsWindow");
        Check(reportsWindow.IsVisible && reportsWindow.Owner == window, "reports opens a separate owned window");
        Check(reportsWindow.FindName("SearchBox") is TextBox &&
              reportsWindow.FindName("SortBox") is ComboBox &&
              reportsWindow.FindName("OutcomeBox") is ComboBox, "reports exposes search, sorting and outcome filter");
        reportsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(ReferenceEquals(reportsWindow, Field<ReportsWindow>("_reportsWindow")), "reports reuses its open window");
        reportsWindow.Close();
        Check(Field<ReportsWindow?>("_reportsWindow") is null, "reports ownership cleared on close");
        Check(ReferenceEquals(tabs.SelectedItem, Field<TabItem>("DashboardTab")), "reports leaves main dashboard selected");
        foreach(var section in sections)
        {
            Exception? failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Field<Window>("_settingsWindow");
                try
                {
                    Check(dialog is not null && dialog.Owner == window, section+" separate owned window");
                    var surface = (FrameworkElement)dialog!.Content;
                    Render(surface,section,surface.ActualWidth,surface.ActualHeight);
                    if(section == "project")
                    {
                        interval.Text = "120";
                        var grid = (Grid)dialog.Content;
                        var footer = (Border)grid.Children[1];
                        var buttons = ((StackPanel)footer.Child).Children.OfType<Button>().ToArray();
                        buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Check(dialog.IsVisible && store.LoadSettings().PollIntervalSeconds == 120, "Apply keeps window open");
                        interval.Text = "130";
                        buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Check(!dialog.IsVisible && store.LoadSettings().PollIntervalSeconds == 130, "OK saves and closes");
                    }
                    else dialog.Close();
                }
                catch(Exception error) { failure=error; dialog?.Close(); }
            }));
            navigation.Single(b => b.Tag is string tag && tag == section)
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(failure is not null) throw failure;
            Check(Field<Window?>("_settingsWindow") is null, "dialog ownership cleared");
        }
        Console.WriteLine($"{count} settings window checks passed; images: {AppContext.BaseDirectory}");
        Call("ExitApplicationAsync"); app.Shutdown();
    }
    sealed class Provider(string id) : IEmailProvider
    {
        public string Id => id;
        public EmailAccount Account => new(id,id+"-id",id+"@example.invalid",false);
        public string? StatusError => null;
        public Task<EmailAccount> ConnectAsync(string config,CancellationToken token) => throw new Exception("Unexpected OAuth");
        public Task DisconnectAsync(CancellationToken token) => throw new Exception("Unexpected disconnect");
        public Task SendAsync(MailJob job,CancellationToken token) => throw new Exception("Unexpected send");
    }
}

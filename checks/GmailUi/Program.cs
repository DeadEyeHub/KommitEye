global using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KommitEye.Services;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

// Offscreen host for the production panel: no browser, real account or messages.
public partial class MainWindow : Window
{
    private readonly MailNotifications _mail;
    private Window? _settingsWindow;
    private string? _draftMailSender;
    private readonly Dictionary<string, string?> _pendingMailConnections = new();
    private readonly StackPanel MailPanel = new() { Margin = new Thickness(16) };
    private readonly TabItem MailTab = new();
    public MainWindow(string root, bool connected, string provider)
    {
        _mail = new MailNotifications(root, _ => { }, startWorker: false,
            providers: [new PreviewProvider(connected)]);
        _googleClientFile = "C:\\KommitEye\\authMail\\client_secret_example.json";
        _mailProviderTab = provider;
        BuildMailPanel();
        Content = new ScrollViewer { Background = SystemColors.WindowBrush, Content = MailPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void MarkSettingsChanged(string scope) { }

    public void Render(string path, int width = 880, int height = 700, bool dispose = true)
    {
        var surface = (FrameworkElement)Content;
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        png.Save(output);
        if (dispose) _mail.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [STAThread]
    public static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "gmail-ui-" + Guid.NewGuid());
        foreach (var language in new[] { "en", "ru" })
        foreach (var connected in new[] { false, true })
        foreach (var provider in new[] { "gmail", "outlook", "smtp" })
        {
            Localization.SetLanguage(language);
            var window = new MainWindow(root, connected, provider);
            if (provider == "smtp" && !connected)
            {
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var dialog = window.CreateSmtpSettingsWindow(new MailSettings(), _ => { });
                var original = window.Content;
                var content = dialog.Content; dialog.Content = null;
                window.Content = content;
                window.Render(Path.Combine(AppContext.BaseDirectory, $"smtp-dialog-{language}.png"), 520, 600, dispose: false);
                window.Content = original;
            }
            var path = Path.Combine(AppContext.BaseDirectory, $"mail-{provider}-{language}-{connected}.png");
            window.Render(path);
            Console.WriteLine(path);
        }
    }

    private sealed class PreviewProvider(bool connected) : IEmailProvider
    {
        public string Id => "gmail";
        public EmailAccount? Account => connected ? new("gmail", "preview", "reviewer@example.invalid", true) : null;
        public string? StatusError => null;
        public Task<EmailAccount> ConnectAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
        public Task SendAsync(MailJob job, CancellationToken token) => throw new NotSupportedException();
    }
}

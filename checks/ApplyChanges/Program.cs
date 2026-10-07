using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KommitEye;
using KommitEye.Services;

class Program
{
    [STAThread] static void Main()
    {
        Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(Path.GetTempPath(), "apply-check-" + Guid.NewGuid()));
        var app = new Application();
        var window = new MainWindow();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        var bar = Field<Border>("ApplyChangesBar");
        var tabs = Field<TabControl>("MainTabs");
        var count = 0;
        void Check(bool result, string name) { if(!result) throw new Exception(name); count++; }
        Check(bar.Visibility == Visibility.Collapsed, "initially hidden");
        Field<CheckBox>("PullEnabledCheckBox").IsChecked = false;
        Check(bar.Visibility == Visibility.Collapsed, "checkbox does not show banner");
        var input = Field<TextBox>("IntervalTextBox");
        Check((string?)Call("SettingsScope", input) == "project", "project input mapped");
        Call("MarkSettingsChanged", "project");
        tabs.SelectedItem = Field<TabItem>("ModelsTab");
        Check(bar.Visibility == Visibility.Visible, "banner survives navigation");
        input.Text = "invalid";
        try { Call("ApplyPendingSettings"); throw new Exception("invalid accepted"); }
        catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) { }
        Check(bar.Visibility == Visibility.Visible, "failed apply retains banner");
        input.Text = "90";
        Call("ApplyPendingSettings");
        Check(bar.Visibility == Visibility.Collapsed, "successful apply hides banner");
        Check(new ConfigurationStore().LoadSettings().PollIntervalSeconds == 90, "project settings persisted");
        var actions = Field<Dictionary<string, Action>>("_applyMailDrafts");
        var applied = 0;
        actions["gmail"] = () => applied++;
        actions["outlook"] = () => applied++;
        Call("MarkSettingsChanged", "mail:gmail"); Call("MarkSettingsChanged", "mail:outlook");
        Call("ApplyPendingSettings");
        Check(applied == 2 && bar.Visibility == Visibility.Collapsed, "both mail drafts apply");
        Call("MarkSettingsChanged", "applied");
        tabs.SelectedItem = Field<TabItem>("MailTab");
        var surface = (FrameworkElement)window.Content;
        ((Grid)surface).Background = SystemColors.WindowBrush;
        surface.Measure(new Size(900, 650)); surface.Arrange(new Rect(0,0,900,650)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900,650,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var image = Path.Combine(AppContext.BaseDirectory,"apply-changes.png");
        using(var output = File.Create(image)) encoder.Save(output);
        Check(Grid.GetRow(bar) == 2 && bar.Parent is Grid, "bar outside scrolling tabs");
        Console.WriteLine($"{count} Apply changes checks passed; {image}");
        Call("ExitApplicationAsync");
        app.Shutdown();
    }
}

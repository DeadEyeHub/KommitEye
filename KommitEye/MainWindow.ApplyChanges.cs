using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using System.Windows.Threading;
using KommitEye.Models;
using KommitEye.Services;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

public partial class MainWindow
{
    private readonly HashSet<string> _pendingSettings = [];
    private bool _applyingChanges;

    private void InitializeApplyChanges()
    {
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox { IsReadOnly: false, IsKeyboardFocusWithin: true } box)
                MarkInputChanged(box);
        }), true);
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is PasswordBox { IsKeyboardFocusWithin: true } box) MarkInputChanged(box);
        }), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox { IsKeyboardFocusWithin: true } box)
                MarkInputChanged(box);
        }), true);
        // Browse buttons and other direct actions can update a field without focusing it.
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node is not null && node is not ButtonBase)
                node = LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null);
            if (node is not Button button || button == ApplyChangesButton) return;
            var scope = SettingsScope(button);
            if (scope is null) return;
            var before = InputSnapshot(MainTabs);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (before != InputSnapshot(MainTabs)) MarkSettingsChanged(scope);
            }));
        }), true);
    }

    private static string InputSnapshot(DependencyObject root)
    {
        var values = new List<string>();
        void Visit(DependencyObject node)
        {
            if (node is TextBox { IsReadOnly: false } text) values.Add(text.Text);
            else if (node is PasswordBox password) values.Add(password.Password);
            else if (node is ComboBox combo) values.Add(combo.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
        }
        Visit(root);
        return string.Join("\u001f", values);
    }

    private string? SettingsScope(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null;)
        {
            if (node is FrameworkElement { Tag: string tag } && tag.StartsWith("mail:")) return tag;
            if (ReferenceEquals(node, ProjectTab)) return "project";
            if (ReferenceEquals(node, ModelsTab)) return "models";
            if (ReferenceEquals(node, PromptTab)) return "prompt";
            node = LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null);
        }
        return null;
    }

    private void MarkInputChanged(FrameworkElement input)
    {
        if (!input.IsLoaded || _switchingProject || _loadingLanguage || _applyingChanges) return;
        if (SettingsScope(input) is { } scope) MarkSettingsChanged(scope);
    }

    private void MarkSettingsChanged(string scope)
    {
        if (_applyingChanges || _switchingProject) return;
        _pendingSettings.Add(scope);
        RefreshApplyChangesBar();
    }

    private void RefreshApplyChangesBar()
    {
        if (ApplyChangesBar is null) return;
        ApplyChangesBar.Visibility = _pendingSettings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyChangesButton.Content = Localization.Text("Apply changes", "Применить изменения");
        ApplyChangesText.Text = Localization.Text("Settings changed", "Настройки изменены");
    }

    private void PersistModelProfile(ModelProfile profile)
    {
        var index = _models.Profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _models.Profiles.Add(profile); else _models.Profiles[index] = profile;
        _models.ActiveProfile = profile.Name;
        _configuration.SaveModels(_models);
        LoadProfiles(profile.Name);
        _runner.UpdateProfile(profile);
    }

    private void ApplyPendingSettings()
    {
        if (_mailAccountBusy) throw new InvalidOperationException(Localization.Text(
            "Wait for the mail operation to finish.", "Дождитесь завершения операции с почтой."));
        _applyingChanges = true;
        try
        {
            // Validate first so a malformed model or schedule is not partially applied.
            var profile = _pendingSettings.Contains("models") ? ReadProfileFromForm() : null;
            if (_pendingSettings.Contains("project"))
            {
                _ = new DailyModelSchedule(new AppSettings { QuietHoursEnabled = QuietHoursCheckBox.IsChecked == true,
                    QuietHoursStart = QuietStartTextBox.Text.Trim(), QuietHoursEnd = QuietEndTextBox.Text.Trim() });
                SaveProjectDraft();
                _runner.UpdateSettings(_configuration.LoadSettings());
            }
            if (profile is not null) PersistModelProfile(profile);
            if (_pendingSettings.Contains("prompt")) _configuration.SavePrompt(PromptTextBox.Text);
            foreach (var scope in _pendingSettings.Where(s => s.StartsWith("mail:")).ToArray())
                if (_applyMailDrafts.TryGetValue(scope[5..], out var apply)) apply();
            _pendingSettings.Clear();
        }
        finally { _applyingChanges = false; RefreshApplyChangesBar(); }
    }

    private void ApplyChanges_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyPendingSettings(); AppendLog(Localization.Text("Changes applied.", "Изменения применены.")); }
        catch (Exception error) { ShowError(error.Message); }
    }
}

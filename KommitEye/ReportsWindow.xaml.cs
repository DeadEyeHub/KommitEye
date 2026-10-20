using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KommitEye.Services;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

public partial class ReportsWindow : Window
{
    private sealed record Option(string Value, string Label);
    private readonly ReportWriter _reports;
    private CancellationTokenSource? _debounce;
    private int _generation, _selectionGeneration, _offset;
    private bool _ready, _closed;
    private string? _markdown;
    private const int PageSize = 50;

    public ReportsWindow(ReportWriter reports)
    {
        _reports = reports;
        InitializeComponent();
        Title = Heading.Text = Localization.Text("Reports", "Отчёты");
        Description.Text = Localization.Text("One entry per commit. Search SHA, author, subject, branch or report text.",
            "Одна запись на коммит. Поиск по SHA, автору, теме, ветке и тексту отчёта.");
        SearchLabel.Content = Localization.Text("Search reports", "Поиск по отчётам");
        GroupLabel.Content = Localization.Text("Repository / branch", "Репозиторий / ветка");
        SortLabel.Content = Localization.Text("Sort by", "Сортировка");
        OrderLabel.Content = Localization.Text("Order", "Порядок");
        OutcomeLabel.Content = OutcomeColumn.Header = Localization.Text("Bug / No bug", "Баг / Не баг");
        OutcomeBox.ItemsSource = new[] {
            new Option("", Localization.Text("All results", "Все результаты")),
            new Option("bug", Localization.Text("Bug", "Баг")),
            new Option("clean", Localization.Text("No bug", "Не баг")),
            new Option("failed", Localization.Text("Review failed", "Ошибка проверки")),
            new Option("empty", Localization.Text("Empty diff", "Нет изменений")),
            new Option("unknown", Localization.Text("Unknown", "Не определено")) };
        OutcomeBox.SelectedIndex = 0;
        RefreshButton.Content = Localization.Text("Refresh", "Обновить");
        PreviousButton.Content = Localization.Text("Previous", "Назад");
        NextButton.Content = Localization.Text("Next", "Далее");
        SaveButton.Content = Localization.Text("Save review…", "Сохранить проверку…");
        BranchButton.Content = Localization.Text("Full branch report", "Полный отчёт ветки");
        SubjectColumn.Header = Localization.Text("Subject", "Тема");
        AuthorColumn.Header = Localization.Text("Author", "Автор");
        BranchColumn.Header = Localization.Text("Branch", "Ветка");
        DateColumn.Header = Localization.Text("Saved", "Сохранено");
        SortBox.ItemsSource = new[] {
            new Option("saved", Localization.Text("Saved date", "Дата сохранения")),
            new Option("date", Localization.Text("Commit date", "Дата коммита")),
            new Option("author", Localization.Text("Author", "Автор")),
            new Option("subject", Localization.Text("Subject", "Тема")),
            new Option("branch", Localization.Text("Branch", "Ветка")),
            new Option("repository", Localization.Text("Repository", "Репозиторий")),
            new Option("outcome", Localization.Text("Result", "Результат")),
            new Option("sha", "SHA") };
        SortBox.SelectedIndex = 0;
        OrderBox.Items.Add(Localization.Text("Descending", "По убыванию"));
        OrderBox.Items.Add(Localization.Text("Ascending", "По возрастанию"));
        OrderBox.SelectedIndex = 0;
        GroupBox.ItemsSource = new[] { new Option("", Localization.Text("All reports", "Все отчёты")) };
        GroupBox.SelectedIndex = 0;
        PreviewTitle.Text = Localization.Text("Select a review to read it", "Выберите проверку для просмотра");
        Loaded += async (_, _) => { _ready = true; SearchBox.Focus(); await LoadAsync(); };
        Closed += (_, _) => { _closed = true; _debounce?.Cancel(); _debounce?.Dispose(); };
    }

    private async void FiltersChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        _offset = 0;
        _generation++; // Invalidate a response immediately, including during debounce.
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cancellation = _debounce = new CancellationTokenSource();
        try { await Task.Delay(250, cancellation.Token); await LoadAsync(); }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAsync()
    {
        if (_closed) return;
        var generation = ++_generation;
        var query = SearchBox.Text;
        var sort = SortBox.SelectedValue as string ?? "saved";
        var descending = OrderBox.SelectedIndex == 0;
        var group = GroupBox.SelectedValue as string;
        var outcome = OutcomeBox.SelectedValue as string;
        var offset = _offset;
        Status.Text = Localization.Text("Loading reports…", "Загрузка отчётов…");
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        Entries.ItemsSource = null;
        try
        {
            var data = await Task.Run(() => (_reports.Browse(query, sort, descending, offset, PageSize,
                string.IsNullOrEmpty(group) ? null : group, outcome), _reports.List()));
            if (_closed || generation != _generation) return;
            _ready = false;
            try
            {
                GroupBox.ItemsSource = new[] { new Option("", Localization.Text("All reports", "Все отчёты")) }
                    .Concat(data.Item2.Select(r => new Option(r.Name, r.Name))).ToArray();
                GroupBox.SelectedValue = data.Item2.Any(r => r.Name == group) ? group : "";
            }
            finally { _ready = true; }
            if (!string.IsNullOrEmpty(group) && !data.Item2.Any(r => r.Name == group))
            { _offset = 0; await LoadAsync(); return; }
            var page = data.Item1;
            if (offset > 0 && offset >= page.Total) { _offset = 0; await LoadAsync(); return; }
            Entries.ItemsSource = page.Items;
            Status.Text = page.Total == 0
                ? Localization.Text("No matching reviews. Try another search or open the full branch report.",
                    "Проверки не найдены. Измените запрос или откройте полный отчёт ветки.")
                : Localization.Format("Reviews found: {0}", "Найдено проверок: {0}", page.Total);
            PageLabel.Text = page.Total == 0 ? "0" : $"{offset + 1}–{offset + page.Items.Count} / {page.Total}";
            PreviousButton.IsEnabled = offset > 0;
            NextButton.IsEnabled = offset + PageSize < page.Total;
            BranchButton.IsEnabled = !string.IsNullOrEmpty(GroupBox.SelectedValue as string);
            if (page.Items.Count > 0) Entries.SelectedIndex = 0;
        }
        catch (Exception error)
        {
            if (!_closed && generation == _generation)
                Status.Text = Localization.Text("Could not load reports. Click Refresh. ", "Не удалось загрузить отчёты. Нажмите «Обновить». ") + error.Message;
        }
    }

    private async void EntrySelected(object sender, SelectionChangedEventArgs e)
    {
        var generation = ++_selectionGeneration;
        _markdown = null;
        SaveButton.IsEnabled = false;
        Preview.Document = new FlowDocument();
        PreviewTitle.Text = Localization.Text("Select a review to read it", "Выберите проверку для просмотра");
        if (Entries.SelectedItem is not ReviewReportEntry entry) return;
        PreviewTitle.Text = entry.ShortSha + " · " + entry.ReportName +
            (entry.Legacy ? Localization.Text(" · Imported (saved date is the original file date)", " · Импортирован (дата сохранения — дата исходного файла)") : "");
        BranchButton.IsEnabled = true;
        try
        {
            var markdown = await Task.Run(() => _reports.ReadReview(entry.Id));
            if (_closed || generation != _selectionGeneration) return;
            _markdown = markdown;
            Preview.Document = Render(markdown ?? Localization.Text("This review is no longer available.", "Проверка больше недоступна."));
            SaveButton.IsEnabled = markdown is not null;
        }
        catch (Exception error) { if (!_closed && generation == _selectionGeneration) Status.Text = error.Message; }
    }

    // Plain Runs keep Git/model content inert; never interpret report text as XAML or HTML.
    private static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(18) };
        var fence = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(new string((char)96, 3), StringComparison.Ordinal)) { fence = !fence; continue; }
            if (!fence && line.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var heading = !fence && line.StartsWith('#') ? line.TakeWhile(c => c == '#').Count() : 0;
            var value = heading > 0 ? line[heading..].TrimStart() : line;
            var paragraph = new Paragraph(new Run(value)) { Margin = new Thickness(0, 0, 0, fence ? 0 : 8) };
            if (heading > 0) { paragraph.FontSize = heading <= 2 ? 19 : 16; paragraph.FontWeight = FontWeights.SemiBold; }
            if (fence) paragraph.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) { _debounce?.Cancel(); await LoadAsync(); }
    private async void Previous_Click(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - PageSize); await LoadAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { _offset += PageSize; await LoadAsync(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_markdown is null || Entries.SelectedItem is not ReviewReportEntry entry) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = $"review-{entry.ShortSha}.md" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _markdown, new UTF8Encoding(false)); }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private async void Branch_Click(object sender, RoutedEventArgs e)
    {
        var name = (Entries.SelectedItem as ReviewReportEntry)?.ReportName ?? GroupBox.SelectedValue as string;
        if (string.IsNullOrEmpty(name)) return;
        var generation = ++_selectionGeneration;
        try
        {
            var markdown = await Task.Run(() => _reports.Read(name));
            if (_closed || generation != _selectionGeneration) return;
            PreviewTitle.Text = name;
            Preview.Document = Render(markdown ?? Localization.Text("Report unavailable.", "Отчёт недоступен."));
            SaveButton.IsEnabled = false;
        }
        catch (Exception error) { if (!_closed && generation == _selectionGeneration) Status.Text = error.Message; }
    }
}

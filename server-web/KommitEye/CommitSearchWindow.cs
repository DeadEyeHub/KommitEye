using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using Localization = KommitEye.Services.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KommitEye.Models;
using KommitEye.Services;

namespace KommitEye;

public sealed class CommitSearchWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBox _query = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox _results = new() { DisplayMemberPath = nameof(CommitChoice.Display) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<CommitChoice> _commits = [];
    private readonly Button _loadMore = new() { Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 8, 8, 0) };
    private bool _loading, _closed;
    private bool _hasMore = true;
    private string? _head, _loadError;
    private const int PageSize = 100;
    public string SelectedSha { get; private set; } = string.Empty;

    public CommitSearchWindow(GitService git, string path, string branch)
    {
        Style = (Style)FindResource(typeof(Window));
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Title = Localization.Text("Find commit", "Поиск коммита");
        Width = 850; Height = 520; MinWidth = 500; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        Content = panel;
        var label = new TextBlock { Text = Localization.Text(
            "Search loaded commits by SHA or description. Load more to search older history, or enter a SHA directly.",
            "Поиск загруженных коммитов по хешу или описанию. Загрузите ещё для поиска в старой истории или введите SHA напрямую."), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        DockPanel.SetDock(_query, Dock.Top); panel.Children.Add(_query);
        var select = new Button { Content = Localization.Text("Select", "Выбрать"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var actions = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        DockPanel.SetDock(_loadMore, Dock.Left); actions.Children.Add(_loadMore);
        actions.Children.Add(select);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        panel.Children.Add(_results);
        _query.TextChanged += (_, _) => Filter();
        select.Click += (_, _) => SelectCommit();
        _results.MouseDoubleClick += (_, _) => { if (_results.SelectedItem is CommitChoice) SelectCommit(); };
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) SelectCommit(); };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); };
        _loadMore.Click += async (_, _) => await LoadMoreAsync();
        Loaded += async (_, _) => { _query.Focus(); await LoadMoreAsync(); };

        async Task LoadMoreAsync()
        {
            if (_loading || !_hasMore || _closed) return;
            _loading = true;
            _loadError = null;
            Filter();
            try
            {
                var token = _cancellation.Token;
                _head ??= await git.GetBranchHeadAsync(path, branch, token);
                var page = await git.GetCommitPageAsync(path, _head, _commits.Count, PageSize + 1, token);
                if (_closed) return;
                _commits.AddRange(page.Take(PageSize));
                _hasMore = page.Count > PageSize;
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception error) { _loadError = error.Message; }
            finally
            {
                _loading = false;
                if (!_closed) Filter();
            }
        }
    }

    private void Filter()
    {
        var query = _query.Text.Trim();
        var matches = _commits.Where(c => c.Sha.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = _results.SelectedItem;
        _results.ItemsSource = matches;
        if (selected is CommitChoice choice && matches.Contains(choice)) _results.SelectedItem = choice;
        _loadMore.IsEnabled = !_loading && _hasMore;
        _loadMore.Content = _loading ? Localization.Text("Loading…", "Загрузка…") :
            _loadError is not null ? Localization.Text("Retry loading", "Повторить загрузку") :
            Localization.Text("Load 100 more", "Загрузить ещё 100");
        _status.Text = Localization.Format("Matching: {0} · Loaded: {1}", "Найдено: {0} · Загружено: {1}", matches.Count, _commits.Count);
        if (!_hasMore) _status.Text += Localization.Text(" · All commits loaded", " · Все коммиты загружены");
        if (_loadError is not null) _status.Text += "\n" + _loadError;
    }

    private void SelectCommit()
    {
        var sha = (_results.SelectedItem as CommitChoice)?.Sha ?? _query.Text.Trim();
        if (sha.Length is < 4 or > 40 || !sha.All(Uri.IsHexDigit)) return;
        SelectedSha = sha;
        DialogResult = true;
    }
}


using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace IMCBedwarsOverlay;

public partial class KitBattleLeaderboardWindow : Window
{
    readonly StatsApiClient _api;
    CancellationTokenSource? _request;
    int _requestVersion;
    int _page = 1;
    int _totalPages = 1;
    bool _closing;

    internal KitBattleLeaderboardWindow(StatsApiClient api)
    {
        _api = api;
        InitializeComponent();
        LeaderboardGrid.ItemsSource = new ObservableCollection<KitBattleLeaderboardRow>();
        Loaded += async (_, _) => await LoadPageAsync();
        Closing += (_, _) => { _closing = true; _request?.Cancel(); };
    }

    string Metric => (MetricCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "kills";

    async Task LoadPageAsync(bool force = false)
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        var version = ++_requestVersion;
        var metric = Metric;
        LoadButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        MetricCombo.IsEnabled = false;
        LoadStatusText.Text = "正在读取 IMC.RE 榜单…";

        try
        {
            var result = await _api.GetKitBattleLeaderboardAsync(metric, _page, force, token);
            if (_closing || version != _requestVersion || token.IsCancellationRequested) return;
            var data = result.Data;
            _page = Math.Max(1, data.Page);
            _totalPages = Math.Max(1, (int)Math.Ceiling(data.Total / (double)Math.Max(1, data.PageSize)));
            LeaderboardGrid.ItemsSource = new ObservableCollection<KitBattleLeaderboardRow>(data.Players.Select(p => new KitBattleLeaderboardRow(p, metric)));
            MetricValueColumn.Header = metric switch { "kd" => "K/D", "exp" => "经验", _ => "击杀" };
            PageText.Text = $"第 {_page} / {_totalPages} 页";
            SubtitleText.Text = $"IMC.RE 官方分页榜单 · 每页 {data.PageSize} 名 · 共 {data.Total:N0} 名玩家";
            LoadStatusText.Text = result.IsFresh ? "已更新" : data.Players.Count > 0 ? "显示缓存数据" : "当前无法读取榜单";
            FooterText.Text = data.Players.Count > 0
                ? $"显示服务器返回的原始名次：#{data.Players[0].Position}–#{data.Players[^1].Position}。KitBattle 榜单不使用 BW Rating Pro。"
                : "当前页没有可显示的数据。";
            PreviousButton.IsEnabled = _page > 1;
            NextButton.IsEnabled = _page < _totalPages;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!_closing && version == _requestVersion)
            {
                LoadButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                MetricCombo.IsEnabled = true;
            }
        }
    }

    async void Load_Click(object sender, RoutedEventArgs e) { _page = 1; await LoadPageAsync(); }
    async void Refresh_Click(object sender, RoutedEventArgs e) { await LoadPageAsync(force: true); }
    async void Previous_Click(object sender, RoutedEventArgs e) { if (_page > 1) { _page--; await LoadPageAsync(); } }
    async void Next_Click(object sender, RoutedEventArgs e) { if (_page < _totalPages) { _page++; await LoadPageAsync(); } }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

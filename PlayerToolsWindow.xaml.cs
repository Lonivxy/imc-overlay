using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace IMCBedwarsOverlay;

internal enum PlayerToolKind { Party, Vs }

public partial class PlayerToolsWindow : Window
{
    readonly StatsApiClient _api;
    readonly PlayerToolKind _kind;
    CancellationTokenSource? _request;
    int _requestVersion;
    bool _closing;
    string _copyText = "";

    internal PlayerToolsWindow(StatsApiClient api, PlayerToolKind kind)
    {
        _api = api;
        _kind = kind;
        InitializeComponent();
        Title = kind == PlayerToolKind.Party ? "Party 战术分析" : "玩家 VS 对比";
        WindowTitle.Text = kind == PlayerToolKind.Party ? "Party 战术分析" : "玩家 VS 对比";
        WindowSubtitle.Text = kind == PlayerToolKind.Party
            ? "查询 2–4 名玩家，按 BedWars 或 CounterEmber 数据分配互补职责。"
            : "逐项对照两名玩家的模式数据；离线重名玩家保留前缀 -。";
        PartyInputPanel.Visibility = kind == PlayerToolKind.Party ? Visibility.Visible : Visibility.Collapsed;
        VsInputPanel.Visibility = kind == PlayerToolKind.Vs ? Visibility.Visible : Visibility.Collapsed;
        ModeNote.Text = "BedWars 使用 Rating Pro；CounterEmber 使用 IMC.RE 官方 Rating。";
        if (kind == PlayerToolKind.Vs)
            ModeCombo.Items.Add(new ComboBoxItem { Content = "KitBattle", Tag = "kitbattle" });
        Closing += (_, _) => { _closing = true; _request?.Cancel(); };
    }

    string CurrentMode => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "bedwars";

    void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeNote is null || ModeCombo.SelectedItem is not ComboBoxItem item) return;
        ModeNote.Text = (item.Tag as string) switch
        {
            "counterember" => "使用 IMC.RE CounterEmber 官方 Rating。",
            "kitbattle" => "KitBattle 没有 Rating Pro；对比 K/D、击杀、经验、挑战胜场、最佳连杀和官方击杀榜名次。",
            _ => "Rating Pro 为项目估算值；少于 10 场会显示 *。"
        };
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == PlayerToolKind.Party) await RunPartyAsync();
        else await RunVsAsync();
    }

    async Task RunPartyAsync()
    {
        ClearResults();
        var names = PartyNamesBox.Text.Split([' ', ',', '，', ';', '；', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length is < 2 or > 4)
        {
            StatusText.Text = "Party 需要 2–4 名玩家。";
            return;
        }
        if (names.Any(name => !PlayerNameRules.IsValid(name)))
        {
            StatusText.Text = "玩家名需要是 3–16 位英文、数字或下划线；离线重名玩家可在前面加 -。";
            return;
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            StatusText.Text = "请移除重复的玩家名。";
            return;
        }

        await RunRequestsAsync(async token =>
        {
            var rows = await Task.WhenAll(names.Select(name => _api.FetchAsync(name, CurrentMode, token)));
            var invalid = rows.Where(row => row.State != "ok" || row.Matches <= 0).ToArray();
            if (invalid.Length > 0)
            {
                StatusText.Text = "无法完整分析：" + string.Join("、", invalid.Select(row =>
                    $"{row.Username}（{(row.State == "unregistered" ? "无记录" : row.State != "ok" ? row.Error : "无有效对局数据")}）"));
                return;
            }

            var result = PlayerAnalysis.AnalyzeParty(rows, CurrentMode);
            PartyResultsGrid.Columns[2].Header = CurrentMode == "counterember" ? "官方 Rating" : "Rating Pro";
            PartyResultsGrid.ItemsSource = new ObservableCollection<PartyMemberResult>(result.Members);
            PartyResultsGrid.Visibility = Visibility.Visible;
            VsResultsGrid.Visibility = Visibility.Collapsed;
            SummaryText.Text = result.Summary;
            var ratingName = CurrentMode == "counterember" ? "官方 Rating" : "Rating Pro";
            _copyText = result.Summary + Environment.NewLine + string.Join(Environment.NewLine,
                result.Members.Select(m => $"{m.Username} · {m.Role} · {ratingName} {m.Rating} · 胜率 {m.WinRate} · {m.Matches} 场 · {m.Metrics}"));
            CopyResultButton.IsEnabled = true;
            StatusText.Text = $"已分析 {result.Members.Count} 名玩家。";
        });
    }

    async Task RunVsAsync()
    {
        ClearResults();
        var redName = RedNameBox.Text.Trim();
        var blueName = BlueNameBox.Text.Trim();
        if (!PlayerNameRules.IsValid(redName) || !PlayerNameRules.IsValid(blueName))
        {
            StatusText.Text = "玩家名需要是 3–16 位英文、数字或下划线；离线重名玩家可在前面加 -。";
            return;
        }
        if (string.Equals(redName, blueName, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "红方和蓝方请输入不同的玩家名。";
            return;
        }

        var mode = CurrentMode;
        await RunRequestsAsync(async token =>
        {
            var results = await Task.WhenAll(_api.FetchAsync(redName, mode, token), _api.FetchAsync(blueName, mode, token));
            var invalid = results.Where(row => row.State != "ok").ToArray();
            if (invalid.Length > 0)
            {
                StatusText.Text = string.Join("；", invalid.Select(row => $"{row.Username}：{(row.State == "unregistered" ? "无记录" : row.Error)}"));
                return;
            }

            var comparison = PlayerAnalysis.Compare(results[0], results[1], mode);
            VsResultsGrid.ItemsSource = new ObservableCollection<ComparisonMetricResult>(comparison.Metrics);
            VsResultsGrid.Visibility = Visibility.Visible;
            PartyResultsGrid.Visibility = Visibility.Collapsed;
            SummaryText.Text = comparison.Summary;
            _copyText = $"{mode.ToUpperInvariant()} 玩家对比\n红方 {results[0].Username} VS 蓝方 {results[1].Username}\n{comparison.Summary}" + Environment.NewLine +
                string.Join(Environment.NewLine, comparison.Metrics.Select(m => $"{m.Metric}：{m.RedValue} VS {m.BlueValue} · {m.Advantage}"));
            CopyResultButton.IsEnabled = true;
            StatusText.Text = $"已比较 {comparison.Metrics.Count(m => m.Advantage != "不可比")} 项数据。";
        });
    }

    void ClearResults()
    {
        PartyResultsGrid.Visibility = Visibility.Collapsed;
        VsResultsGrid.Visibility = Visibility.Collapsed;
        CopyResultButton.IsEnabled = false;
        _copyText = "";
        SummaryText.Text = "分析结果会显示在这里。";
    }

    async Task RunRequestsAsync(Func<CancellationToken, Task> action)
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        var version = ++_requestVersion;
        RunButton.IsEnabled = false;
        ModeCombo.IsEnabled = false;
        StatusText.Text = "正在查询玩家数据…";
        try
        {
            await action(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closing && version == _requestVersion) StatusText.Text = $"分析失败：{ex.Message}";
        }
        finally
        {
            if (!_closing && version == _requestVersion)
            {
                RunButton.IsEnabled = true;
                ModeCombo.IsEnabled = true;
            }
        }
    }

    void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_copyText)) return;
        try { Clipboard.SetText(_copyText); StatusText.Text = "分析结果已复制。"; }
        catch { StatusText.Text = "无法访问剪贴板，请重试。"; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

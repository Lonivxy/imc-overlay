using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IMCBedwarsOverlay;

public partial class MainWindow : Window
{
    const int VK_SPACE = 0x20, VK_TAB = 0x09, VK_BACKSLASH = 0xDC;
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
    const int WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    readonly StatsApiClient _statsApi;
    readonly IOverlayImages _overlayImages;
    readonly bool _interactiveServices;
    readonly ConfigService _config = new();
    readonly MinecraftLogWatcher _logWatcher;
    CancellationTokenSource _rosterRequests = new();
    CancellationTokenSource _searchRequests = new();
    bool _closing;
    string _sortKey = "Rating"; bool _sortDescending = true;
    string _mode = "bedwars";
    int _searchVersion;
    int _cardVersion;
    int _leaderboardVersion;
    int _leaderboardViewVersion;
    int _myRankRequestVersion;
    BitmapSource? _cardImage;
    string _cardName = "";
    double _resizeWidth;
    string _statusBeforeResize = "";
    static readonly Regex UserRegex = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    string _myPlayerName = "";
    readonly ObservableCollection<PlayerRow> LeaderboardRows = [];
    readonly List<Button> _counterEmberLeaderboardTabs = [];
    string _lbSortKey = "Rating"; bool _lbSortDescending = true;
    LeaderboardData _currentLeaderboard = LeaderboardData.Empty;
    readonly ObservableCollection<PlayerRow> Players = [];
    readonly Dictionary<string, PlayerRow> ByName = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer KeyTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    HwndSource? Source; PlayerRow? LastSearchResult; bool _hidden, _forceImc = true, _hotkeyRegistered, _wasHotkey, _wasTab, _wasSpace;
    int _hotkeyVk = VK_BACKSLASH;
    uint _hotkeyModifiers = 0;
    string _hotkeyName = "\\";
    bool _recordingHotkey, _initialized;
    bool _threatAlert = true;
    readonly HashSet<string> _alertedThreatPlayers = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _recentSearches = new();
    Button? _counterEmberDetailsButton;
    DateTime _spaceAt; const double AspectRatio = 980d / 552d;

    public MainWindow() : this(new StatsApiClient(), true) { }

    internal MainWindow(StatsApiClient statsApi, bool startLogWatcher, IOverlayImages? overlayImages = null)
    {
        _statsApi = statsApi; _overlayImages = overlayImages ?? new OverlayImages(); _interactiveServices = startLogWatcher;
        InitializeComponent();
        AddCounterEmberControls();
        PlayersGrid.ItemsSource = Players;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, _sortDescending);
        PlayersGrid.Columns.First(c => c.SortMemberPath == _sortKey).SortDirection = ListSortDirection.Descending;
        LeaderboardGrid.ItemsSource = LeaderboardRows;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(LeaderboardRows)).CustomSort = new PlayerComparer(_lbSortKey, _lbSortDescending);
        LeaderboardGrid.Columns.FirstOrDefault(c => c.SortMemberPath == _lbSortKey)?.SortDirection = ListSortDirection.Descending;
        LoadConfig();
        _logWatcher = new MinecraftLogWatcher(OnLogEvent, _forceImc);
        _logWatcher.ActiveLogDetected += OnActiveLogDetected;
        ApplyMode(_mode, false);
        if (_interactiveServices) { KeyTimer.Tick += PollKeys; KeyTimer.Start(); }
        if (startLogWatcher) _logWatcher.Start();
        if (_interactiveServices) { _ = CheckStartupConnectivityAsync(); _ = RefreshLeaderboardAsync(_mode); }
        Players.CollectionChanged += (_, _) => UpdateSummary(); _initialized = true;
    }

    void AddCounterEmberControls()
    {
        AddCounterEmberColumn(PlayersGrid, "KAST", "Kast", nameof(PlayerRow.KastLabel));
        AddCounterEmberColumn(PlayersGrid, "下包", "Plants", nameof(PlayerRow.PlantsLabel));
        AddCounterEmberColumn(PlayersGrid, "拆包", "Defuses", nameof(PlayerRow.DefusesLabel));
        AddCounterEmberColumn(PlayersGrid, "最高 Rating", "MaxRating", nameof(PlayerRow.MaxRatingLabel));
        AddCounterEmberColumn(LeaderboardGrid, "KAST", "Kast", nameof(PlayerRow.KastLabel));
        AddCounterEmberColumn(LeaderboardGrid, "下包", "Plants", nameof(PlayerRow.PlantsLabel));
        AddCounterEmberColumn(LeaderboardGrid, "拆包", "Defuses", nameof(PlayerRow.DefusesLabel));
        AddCounterEmberColumn(LeaderboardGrid, "最高 Rating", "MaxRating", nameof(PlayerRow.MaxRatingLabel));

        AddCounterEmberTab("Kast", "KAST 榜");
        AddCounterEmberTab("Plants", "下包榜");
        AddCounterEmberTab("Defuses", "拆包榜");
        AddCounterEmberTab("MaxRating", "最高 Rating 榜");

        _counterEmberDetailsButton = new Button
        {
            Content = "近期比赛与趋势",
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromRgb(31, 42, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 77, 102)),
            Foreground = (Brush)FindResource("Text"),
            Cursor = Cursors.Hand
        };
        _counterEmberDetailsButton.Click += CounterEmberDetails_Click;
        ((StackPanel)CardButton.Parent).Children.Insert(0, _counterEmberDetailsButton);
    }

    static void AddCounterEmberColumn(DataGrid grid, string header, string key, string bindingPath)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            SortMemberPath = key,
            Binding = new Binding(bindingPath),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Visibility = Visibility.Collapsed
        });
    }

    void AddCounterEmberTab(string key, string label)
    {
        var button = new Button
        {
            Tag = key,
            Content = label,
            Style = (Style)FindResource("LbTabButton"),
            Visibility = Visibility.Collapsed
        };
        button.Click += LbCategory_Click;
        LbCategoryBar.Children.Add(button);
        _counterEmberLeaderboardTabs.Add(button);
    }

    void LoadConfig()
    {
        var settings = _config.Load();
        _forceImc = settings.ForceImc;
        _mode = settings.GameMode;
        _myPlayerName = settings.PlayerName;
        _hotkeyVk = settings.ToggleKey;
        _hotkeyModifiers = settings.ToggleModifiers;
        _hotkeyName = string.IsNullOrEmpty(settings.ToggleKeyName) ? "\\" : settings.ToggleKeyName;
        _threatAlert = settings.ThreatAlert;

        OpacitySlider.Value = settings.Opacity;
        MyPlayerNameBox.Text = _myPlayerName;
        ForceImcBox.IsChecked = _forceImc;
        if (ThreatAlertBox != null) ThreatAlertBox.IsChecked = _threatAlert;
        ModeCombo.SelectedIndex = _mode == "counterember" ? 1 : 0;
        Opacity = OpacitySlider.Value;
        if (HotkeyBox != null) HotkeyBox.Text = _hotkeyName;
        if (HotkeyHintText != null) HotkeyHintText.Text = $"按 {_hotkeyName} 键显示或隐藏悬浮窗。";
    }
    void SaveConfig()
    {
        if (!_initialized || OpacitySlider == null) return;
        double? left = WindowState == WindowState.Normal ? Left : null;
        double? top = WindowState == WindowState.Normal ? Top : null;
        double? width = WindowState == WindowState.Normal ? Width : null;
        double? height = WindowState == WindowState.Normal ? Height : null;
        _config.Save(new AppSettings(
            _forceImc,
            OpacitySlider.Value,
            _mode,
            _myPlayerName,
            _hotkeyVk,
            _hotkeyModifiers,
            _hotkeyName,
            left,
            top,
            width,
            height,
            _threatAlert
        ));
    }
    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_interactiveServices) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        Source = HwndSource.FromHwnd(hwnd);
        Source.AddHook(WndProc);

        ApplyHotkey(_hotkeyVk, _hotkeyModifiers, _hotkeyName, false);

        var settings = _config.Load();
        if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue &&
            settings.WindowWidth.HasValue && settings.WindowHeight.HasValue)
        {
            double left = settings.WindowLeft.Value;
            double top = settings.WindowTop.Value;
            double width = settings.WindowWidth.Value;
            double height = settings.WindowHeight.Value;

            double screenW = SystemParameters.VirtualScreenWidth;
            double screenH = SystemParameters.VirtualScreenHeight;
            double screenLeft = SystemParameters.VirtualScreenLeft;
            double screenTop = SystemParameters.VirtualScreenTop;

            if (width >= 400 && height >= 250 &&
                left >= screenLeft - width + 100 && left <= screenLeft + screenW - 100 &&
                top >= screenTop && top <= screenTop + screenH - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_HOTKEY && wp.ToInt32() == 1) { ToggleVisibility(); handled = true; }
        return IntPtr.Zero;
    }
    void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeWidth = ActualWidth;
        _statusBeforeResize = FooterStatus.Text;
        FooterStatus.Text = "拖动调整大小，松开后应用";
    }
    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _resizeWidth = Math.Clamp(_resizeWidth + e.HorizontalChange * ActualWidth / 980.0, MinWidth, 1600);
        FooterStatus.Text = $"目标宽度 {Math.Round(_resizeWidth)} · 松开后应用";
    }
    void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        FooterStatus.Text = _statusBeforeResize;
        if (e.Canceled) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(_resizeWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(_resizeWidth / AspectRatio * dpi.DpiScaleY);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010);
        Width = _resizeWidth;
        Height = _resizeWidth / AspectRatio;
        SaveConfig();
    }
    void PlayersGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        _sortDescending = _sortKey == key ? !_sortDescending : key != "Username";
        _sortKey = key;
        foreach (var column in PlayersGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(key, _sortDescending);
        e.Handled = true;
    }
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true; _rosterRequests.Cancel(); _searchRequests.Cancel(); _logWatcher.Dispose(); if (_interactiveServices) SaveConfig(); KeyTimer.Stop(); var hwnd = new WindowInteropHelper(this).Handle; if (_hotkeyRegistered) UnregisterHotKey(hwnd, 1); Source?.RemoveHook(WndProc);
    }
    void ToggleVisibility()
    {
        _hidden = !_hidden; var hwnd = new WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, _hidden ? 0 : 4);
        if (!_hidden) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    void PollKeys(object? sender, EventArgs e)
    {
        bool space = GetAsyncKeyState(VK_SPACE) < 0, tab = GetAsyncKeyState(VK_TAB) < 0;
        if (!_hotkeyRegistered) { bool hotkey = GetAsyncKeyState(_hotkeyVk) < 0; if (hotkey && !_wasHotkey) ToggleVisibility(); _wasHotkey = hotkey; }
        if (space && !_wasSpace) _spaceAt = DateTime.UtcNow;
        if (tab && !_wasTab && DateTime.UtcNow - _spaceAt < TimeSpan.FromSeconds(4)) { _logWatcher.ArmScan(); FooterStatus.Text = "检测到扫描操作，正在读取大厅玩家…"; }
        _wasSpace = space; _wasTab = tab;
    }
    void OnLogEvent(LogEvent item)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (item.Kind == LogEventKind.Roster) { SetRoster(item.Names); FooterStatus.Text = $"已读取同局玩家（{item.Names.Length} 人）"; }
            else AddPlayer(item.Names[0]);
        });
    }
    void SetRoster(IEnumerable<string> names)
    {
        _rosterRequests.Cancel(); _rosterRequests.Dispose(); _rosterRequests = new();
        Players.Clear(); ByName.Clear(); _alertedThreatPlayers.Clear(); foreach (var n in names) AddPlayer(n);
    }
    void AddPlayer(string name)
    {
        if (!UserRegex.IsMatch(name)) return;
        if (ByName.ContainsKey(name)) return;
        var mode = _mode;
        var row = new PlayerRow { Username = name, State = "loading", GameMode = mode };
        if (_overlayImages.TryGetAvatar(name, out var cached) && cached is not null)
            row.Avatar = cached;
        ByName[name] = row;
        Players.Add(row);
        _ = LoadStats(name, row, mode, _rosterRequests.Token);
        _ = LoadAvatar(name, row);
    }
    async Task LoadAvatar(string name, PlayerRow row)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_overlayImages.TryGetAvatar(name, out var cached) && cached is not null)
        {
            row.Avatar = cached;
            return;
        }

        try
        {
            var image = await _overlayImages.GetAvatarAsync(name);
            if (image != null && !_closing && Players.Contains(row)) row.Avatar = image;
        }
        catch { }
    }

    async Task LoadStats(string name, PlayerRow target, string mode, CancellationToken token)
    {
        try
        {
            var result = await _statsApi.FetchAsync(name, mode, token);
            if (!token.IsCancellationRequested && !_closing && _mode == mode
                && ByName.TryGetValue(name, out var current) && ReferenceEquals(current, target)
                && Players.Contains(target))
            {
                var curAvatar = target.Avatar;
                result.ApplyLeaderboard(_currentLeaderboard);
                target.CopyFrom(result);
                if (target.Avatar == null)
                {
                    if (curAvatar != null) target.Avatar = curAvatar;
                    else if (_overlayImages.TryGetAvatar(name, out var cached)) target.Avatar = cached;
                }
                if (_threatAlert && target.IsHighRating && _alertedThreatPlayers.Add(name))
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                }
                CollectionViewSource.GetDefaultView(Players).Refresh();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    async void SearchSubmit_Click(object sender, RoutedEventArgs e) => await Search();
    async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Search(); } }
    async Task Search()
    {
        var name = SearchBox.Text.Trim(); if (!UserRegex.IsMatch(name)) { ModalMessage.Text = "请输入 3 到 16 位英文、数字或下划线组成的玩家名。"; return; }
        _searchRequests.Cancel(); _searchRequests.Dispose(); _searchRequests = new(); var token = _searchRequests.Token;
        var mode = _mode; var version = ++_searchVersion;
        ModalMessage.Text = "正在查询，遇到服务限流时会自动重试…"; CopyButton.Visibility = Visibility.Collapsed; CardButton.Visibility = Visibility.Collapsed;
        if (_counterEmberDetailsButton != null) _counterEmberDetailsButton.Visibility = Visibility.Collapsed;
        PlayerRow result;
        try { result = await _statsApi.FetchAsync(name, mode, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (token.IsCancellationRequested || version != _searchVersion || mode != _mode || _closing) return; result?.ApplyLeaderboard(_currentLeaderboard); LastSearchResult = result;
        _recentSearches.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        _recentSearches.Insert(0, name);
        if (_recentSearches.Count > 6) _recentSearches.RemoveAt(_recentSearches.Count - 1);
        UpdateRecentSearchChips();
        if (result?.State == "ok")
        {
            ModalMessage.Text = FormatStats(result);
            CopyButton.Visibility = Visibility.Visible;
            CardButton.Visibility = Visibility.Visible;
            if (_counterEmberDetailsButton != null)
                _counterEmberDetailsButton.Visibility = result.GameMode == "counterember" && (result.RatingTrend.Length > 0 || result.RecentMatches.Count > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        else ModalMessage.Text = result?.State == "unregistered" ? "未找到该玩家在当前模式的记录。" : $"查询失败：{result?.Error ?? "请检查网络后重试。"}";
    }

    void CounterEmberDetails_Click(object sender, RoutedEventArgs e)
    {
        if (LastSearchResult is not { State: "ok", GameMode: "counterember" } player) return;
        var details = new CounterEmberDetailsWindow(player) { Owner = this };
        details.ShowDialog();
    }
    void ShowPanel(Border panel)
    {
        var from = panel.Visibility == Visibility.Visible ? panel.Opacity : 0;
        panel.Tag = new object();
        panel.BeginAnimation(OpacityProperty, null);
        panel.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else panel.Opacity = 1;
    }
    void HidePanel(Border panel)
    {
        var from = panel.Opacity;
        var marker = new object(); panel.Tag = marker;
        panel.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation) { panel.Visibility = Visibility.Collapsed; panel.Opacity = 1; return; }
        var animation = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) => { if (ReferenceEquals(panel.Tag, marker)) { panel.Visibility = Visibility.Collapsed; panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 1; } };
        panel.BeginAnimation(OpacityProperty, animation);
    }
    void Search_Click(object sender, RoutedEventArgs e) { _searchRequests.Cancel(); _searchVersion++; LastSearchResult = null; ModalTitle.Text = "查询玩家战绩"; SearchBox.Text = ""; ModalMessage.Text = "输入玩家名，最多自动重试 3 次。"; CopyButton.Visibility = Visibility.Collapsed; CardButton.Visibility = Visibility.Collapsed; if (_counterEmberDetailsButton != null) _counterEmberDetailsButton.Visibility = Visibility.Collapsed; UpdateRecentSearchChips(); ShowPanel(ModalShade); SearchBox.Focus(); }
    void OnActiveLogDetected(LogSource? source)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (source is null)
            {
                LogPathBox.Text = "未检测到运行中的 Minecraft 客户端日志";
                LogPathBox.ToolTip = "启动 Minecraft 客户端后会自动识别其 latest.log。";
                FooterStatus.ToolTip = null;
                if (FooterStatus.Text.StartsWith("就绪 · 客户端日志", StringComparison.Ordinal))
                    FooterStatus.Text = "就绪";
                return;
            }

            var path = source.Path;
            var sourceDescription = source.ConfirmedByOpenHandle
                ? "已通过该 Java 进程正在打开的日志文件句柄确认"
                : "无法读取该 Java 进程的文件句柄，按游戏目录推测";
            LogPathBox.Text = path;
            LogPathBox.ToolTip = $"{sourceDescription}：{path}";
            FooterStatus.ToolTip = $"当前客户端日志（{sourceDescription}）：{path}";
            if (FooterStatus.Text is "就绪" or "" || FooterStatus.Text.StartsWith("就绪 · 客户端日志"))
            {
                var fileName = System.IO.Path.GetFileName(path);
                var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
                FooterStatus.Text = $"就绪 · 客户端日志: {parent}\\{fileName}";
            }
        });
    }
    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_logWatcher.ActiveLogSource is { } source)
            OnActiveLogDetected(source);
        ShowPanel(SettingsShade);
    }
    void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Opacity = e.NewValue; }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ModalClose_Click(object sender, RoutedEventArgs e) { _searchRequests.Cancel(); _searchVersion++; HidePanel(ModalShade); }
    void SettingsClose_Click(object sender, RoutedEventArgs e) { _forceImc = ForceImcBox.IsChecked == true; _myPlayerName = MyPlayerNameBox.Text.Trim(); _logWatcher.SetForceImc(_forceImc); ApplyMode(ModeCombo.SelectedIndex == 1 ? "counterember" : "bedwars", true); SaveConfig(); HidePanel(SettingsShade); }
    void ApplyMode(string mode, bool refresh)
    {
        bool changed = _mode != mode;
        if (changed)
        {
            _leaderboardVersion++;
            _leaderboardViewVersion++;
            _lbSortKey = "Rating";
            _lbSortDescending = true;
            _currentLeaderboard = LeaderboardData.Empty;
            LeaderboardRows.Clear();
        }
        _mode = mode; _searchRequests.Cancel(); _searchVersion++;
        ModeTitle.Text = mode == "counterember" ? "CounterEmber" : "起床战争";
        bool ce = mode == "counterember";
        foreach (var col in PlayersGrid.Columns)
        {
            var key = col.SortMemberPath;
            bool bwOnly = key is "threat" or "Fkdr" or "BedsBroken" or "FinalKills";
            bool ceOnly = key is "KD" or "Adr" or "HsRate" or "Kast" or "Plants" or "Defuses" or "MaxRating";
            col.Visibility = (bwOnly && ce) || (ceOnly && !ce) ? Visibility.Collapsed : Visibility.Visible;
        }
        _sortKey = "Rating"; _sortDescending = true;
        foreach (var col in PlayersGrid.Columns) col.SortDirection = col.SortMemberPath == _sortKey ? ListSortDirection.Descending : null;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, true);
        foreach (var col in LeaderboardGrid.Columns)
        {
            var key = col.SortMemberPath;
            bool bwOnly = key is "Fkdr" or "BedsBroken" or "FinalKills";
            bool ceOnly = key is "KD" or "Adr" or "HsRate" or "Kast" or "Plants" or "Defuses" or "MaxRating";
            col.Visibility = (bwOnly && ce) || (ceOnly && !ce) ? Visibility.Collapsed : Visibility.Visible;
            col.SortDirection = changed && key == "Rating" ? ListSortDirection.Descending : null;
        }
        if (LbCategoryBar != null)
        {
            LbCatFkdr.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatBeds.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatFinals.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatKd.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            LbCatAdr.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            LbCatHsRate.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            foreach (var tab in _counterEmberLeaderboardTabs)
                tab.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            UpdateCategoryButtons(_lbSortKey);
        }
        if (_interactiveServices) UpdateMyRankSummary();
        if (changed && refresh) { var names = Players.Select(p => p.Username).ToArray(); SetRoster(names); FooterStatus.Text = "已切换模式，正在重新查询名单…"; LastSearchResult = null; }
        if (_interactiveServices) _ = RefreshLeaderboardAsync(mode);
    }
    async Task RefreshLeaderboardAsync(string mode)
    {
        var version = ++_leaderboardVersion;
        try
        {
            var lb = await _statsApi.GetLeaderboardAsync(mode).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || version != _leaderboardVersion || mode != _mode) return;
                _currentLeaderboard = lb;
                foreach (var p in Players) p.ApplyLeaderboard(lb);
                LastSearchResult?.ApplyLeaderboard(lb);
                CollectionViewSource.GetDefaultView(Players).Refresh();
                UpdateMyRankSummary();
                if (LeaderboardShade.Visibility == Visibility.Visible)
                    _ = SwitchLeaderboardCategoryAsync(_lbSortKey, _lbSortDescending);
            });
        }
        catch { }
    }
    void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); SaveConfig(); } catch { } } }
    void PlayersGrid_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && PlayersGrid.SelectedItem is PlayerRow row && row.State == "ok" && e.ClickCount == 1) Copy(row);
    }
    void PlayersGrid_DoubleClick(object sender, MouseButtonEventArgs e) { if (PlayersGrid.SelectedItem is PlayerRow p) { LastSearchResult = p; Copy(p); } }
    void CopySearch_Click(object sender, RoutedEventArgs e) { if (LastSearchResult is { State: "ok" } p) Copy(p); }
    async void CardView_Click(object sender, RoutedEventArgs e)
    {
        if (LastSearchResult is not { State: "ok" } player) return;
        int version = ++_cardVersion; _cardImage = null; _cardName = player.Username;

        CardButton.IsEnabled = false;
        var originalContent = CardButton.Content;
        CardButton.Content = "生成中…";

        try
        {
            // 1. Ensure avatar is loaded before showing the card
            if (player.Avatar == null)
            {
                if (_overlayImages.TryGetAvatar(player.Username, out var cached) && cached is not null)
                {
                    player.Avatar = cached;
                }
                else
                {
                    var fetched = await _overlayImages.GetAvatarAsync(player.Username);
                    if (fetched != null) player.Avatar = fetched;
                }
            }

            // 2. Ensure wallpaper is loaded
            var wallpaper = await _overlayImages.GetOfficialWallpaperAsync();
            if (version != _cardVersion || _closing) return;

            // 3. Render card bitmap
            _cardImage = StatsCardRenderer.Render(player, wallpaper, DateTime.Now);
            CardPreview.Source = _cardImage;
            SaveCardButton.IsEnabled = true;
            CardSaveStatus.Text = "保存后将自动复制图片，文件保存在「图片\\IMC.RE」文件夹。";

            // 4. Guaranteed to show panel ONLY after avatar & background are loaded
            ShowPanel(CardShade);
        }
        catch (Exception ex)
        {
            if (version == _cardVersion)
            {
                ModalMessage.Text = $"战绩图片生成失败：{ex.Message}";
            }
        }
        finally
        {
            CardButton.IsEnabled = true;
            CardButton.Content = originalContent;
        }
    }
    void CloseCard_Click(object sender, RoutedEventArgs e) { _cardVersion++; HidePanel(CardShade); }
    void SaveCard_Click(object sender, RoutedEventArgs e)
    {
        if (_cardImage == null) return;
        string path;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "IMC.RE");
            path = SaveCardPng(_cardImage, directory, _cardName, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { CardSaveStatus.Text = "保存失败：" + ex.Message; return; }
        try
        {
            Clipboard.SetImage(_cardImage);
            CardSaveStatus.Text = "已保存并复制图片：" + path;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            CardSaveStatus.Text = "图片已保存，但复制失败，请重试。路径：" + path;
        }
    }
    static string SaveCardPng(BitmapSource image, string directory, string name, DateTime now)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}-{now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
        return path;
    }
    void Copy(PlayerRow p)
    {
        try { Clipboard.SetText(FormatStats(p)); FooterStatus.Text = $"已复制 {p.Username} 的战绩"; } catch { FooterStatus.Text = "复制失败，请稍后重试"; }
    }
    static string FormatStats(PlayerRow p) => p.GameMode == "counterember"
        ? $"[IMC.RE CounterEmber] {p.Username} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | Rating: {p.RatingLabel} (最高 {(p.Matches > 0 ? p.MaxRating.ToString("F2") : "—")}) | K/D: {p.KD:F2} | KAST: {p.Kast:P1} | ADR: {p.Adr:F1} | 爆头率: {p.HsRate:P1} | MVP: {p.MVPs} | 击杀: {p.Kills} | 下包: {p.Plants} | 拆包: {p.Defuses}"
        : $"[IMC.RE 起床战争] {p.Username} | 估算 Rating: {p.RatingLabel} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | FKDR: {p.Fkdr:F2} | 拆床: {p.BedsBroken} | 终杀: {p.FinalKills} | MVP: {p.MVPs} | 击杀: {p.Kills}";
    void UpdateSummary()
    {
        EmptyPanel.Visibility = Players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task CheckStartupConnectivityAsync()
    {
        try
        {
            var (success, msg, ms) = await _statsApi.CheckConnectivityAsync().ConfigureAwait(false);
            if (!success)
            {
                Dispatcher.Invoke(() =>
                {
                    NetWarningBanner.Visibility = Visibility.Visible;
                    FooterStatus.Text = "网络异常：无法连接到 api.imc.re，请检查防火墙或网络设置";
                });
            }
        }
        catch { }
    }

    async void TestApiButton_Click(object sender, RoutedEventArgs e)
    {
        TestApiButton.IsEnabled = false;
        TestApiStatus.Text = "正在测试连接 api.imc.re…";
        TestApiStatus.Foreground = (SolidColorBrush)FindResource("Muted");
        var (success, msg, ms) = await _statsApi.CheckConnectivityAsync().ConfigureAwait(true);
        TestApiButton.IsEnabled = true;
        if (success)
        {
            TestApiStatus.Text = $"✓ 连接成功（延迟 {ms} ms）";
            TestApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            NetWarningBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            TestApiStatus.Text = "✗ 无法连接到 api.imc.re，请检查防火墙或网络设置";
            TestApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            NetWarningBanner.Visibility = Visibility.Visible;
        }
    }

    async void RetryNetworkTest_Click(object sender, RoutedEventArgs e)
    {
        FooterStatus.Text = "正在重新测试连接 api.imc.re…";
        var (success, msg, ms) = await _statsApi.CheckConnectivityAsync().ConfigureAwait(true);
        if (success)
        {
            NetWarningBanner.Visibility = Visibility.Collapsed;
            FooterStatus.Text = $"连接恢复正常（{ms} ms）";
        }
        else
        {
            NetWarningBanner.Visibility = Visibility.Visible;
            FooterStatus.Text = "重试失败：无法连接到 api.imc.re，请检查防火墙或网络设置";
        }
    }

    void CloseWarningBanner_Click(object sender, RoutedEventArgs e)
    {
        NetWarningBanner.Visibility = Visibility.Collapsed;
    }

    void MyPlayerNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _myPlayerName = MyPlayerNameBox.Text.Trim();
        UpdateMyRankSummary();
    }

    void UpdateMyRankSummary()
    {
        var requestVersion = ++_myRankRequestVersion;
        var name = _myPlayerName;
        var mode = _mode;
        if (string.IsNullOrWhiteSpace(name))
        {
            MyRankStatus.Text = "";
            return;
        }

        if (_currentLeaderboard.Mode == mode && _currentLeaderboard.Players.Count > 0)
        {
            var found = _currentLeaderboard.Players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                if (found.Matches <= 0)
                {
                    MyRankStatus.Text = "暂无榜单记录";
                    return;
                }
                double rating = mode == "counterember" ? found.Metrics.Rating : (BedwarsRatingCalculator.Calculate(found.Matches, found.Wins, found.Metrics.Fkdr, found.Metrics.Kd) ?? 0.0);
                int? rank = _currentLeaderboard.GetRankRating(rating);
                string ratingLabel = $"{rating:F2}{(found.Matches < 10 ? "*" : "")}";
                MyRankStatus.Text = rank.HasValue ? $"全服 #{rank} (Rating {ratingLabel})" : $"未入榜 (Rating {ratingLabel})";
                return;
            }
        }
        MyRankStatus.Text = _currentLeaderboard.Mode == mode ? "查询中…" : "等待当前模式榜单…";
        _ = FetchMyRankSummaryAsync(name, mode, requestVersion);
    }

    async Task FetchMyRankSummaryAsync(string name, string mode, int requestVersion)
    {
        try
        {
            var row = await _statsApi.FetchAsync(name, mode, CancellationToken.None).ConfigureAwait(true);
            if (_closing || requestVersion != _myRankRequestVersion || mode != _mode
                || !string.Equals(_myPlayerName, name, StringComparison.OrdinalIgnoreCase)) return;
            if (_currentLeaderboard.Mode != mode || _currentLeaderboard.Players.Count == 0)
            {
                MyRankStatus.Text = "等待当前模式榜单…";
                return;
            }
            if (row.State == "ok" && row.OverallRating.HasValue)
            {
                int? rank = _currentLeaderboard.GetRankRating(row.OverallRating.Value);
                MyRankStatus.Text = rank.HasValue ? $"全服 #{rank} (Rating {row.RatingLabel})" : $"未入榜 (Rating {row.RatingLabel})";
            }
            else MyRankStatus.Text = "暂无榜单记录";
        }
        catch
        {
            if (!_closing && requestVersion == _myRankRequestVersion && mode == _mode
                && string.Equals(_myPlayerName, name, StringComparison.OrdinalIgnoreCase))
                MyRankStatus.Text = "未查到战绩";
        }
    }

    void Leaderboard_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(LeaderboardShade);
        _ = SwitchLeaderboardCategoryAsync(_lbSortKey, _lbSortDescending);
    }

    void LeaderboardClose_Click(object sender, RoutedEventArgs e)
    {
        HidePanel(LeaderboardShade);
    }

    async void LeaderboardRefresh_Click(object sender, RoutedEventArgs e)
    {
        var mode = _mode;
        var version = ++_leaderboardVersion;
        FooterStatus.Text = "正在刷新天梯排行榜…";
        var result = await _statsApi.GetLeaderboardWithStatusAsync(mode, force: true).ConfigureAwait(true);
        if (_closing || version != _leaderboardVersion || mode != _mode) return;
        var lb = result.Data;
        _currentLeaderboard = lb;
        foreach (var p in Players) p.ApplyLeaderboard(lb);
        LastSearchResult?.ApplyLeaderboard(lb);
        UpdateMyRankSummary();
        await SwitchLeaderboardCategoryAsync(_lbSortKey, _lbSortDescending).ConfigureAwait(true);
        if (_closing || version != _leaderboardVersion || mode != _mode) return;
        FooterStatus.Text = result.IsFresh
            ? $"天梯排行榜已刷新（{lb.Players.Count} 位达标玩家）"
            : lb.Players.Count > 0
                ? $"刷新失败，当前显示缓存数据（{lb.Players.Count} 位玩家）"
                : "刷新失败，暂无可用榜单数据";
    }

    async void LbCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            await SwitchLeaderboardCategoryAsync(key, true).ConfigureAwait(true);
        }
    }

    void UpdateCategoryButtons(string activeKey)
    {
        if (LbCategoryBar == null) return;
        var activeBg = (SolidColorBrush?)FindResource("Blue") ?? new SolidColorBrush(Color.FromRgb(2, 132, 199));
        var inactiveBg = new SolidColorBrush(Color.FromRgb(23, 34, 51));
        var activeBorder = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        var inactiveBorder = new SolidColorBrush(Color.FromRgb(42, 57, 77));
        var activeFg = Brushes.White;
        var inactiveFg = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        foreach (var child in LbCategoryBar.Children)
        {
            if (child is Button btn && btn.Tag is string tag)
            {
                bool isActive = string.Equals(tag, activeKey, StringComparison.OrdinalIgnoreCase);
                btn.Background = isActive ? activeBg : inactiveBg;
                btn.Foreground = isActive ? activeFg : inactiveFg;
                btn.BorderBrush = isActive ? activeBorder : inactiveBorder;
                btn.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }
    }

    static StatRankInfo? GetRankForMetric(PlayerRow row, string metric) => metric switch
    {
        "Rating" => row.RatingRank,
        "WinRate" => row.WinRateRank,
        "MVPs" => row.MVPRank,
        "Fkdr" => row.FkdrRank,
        "BedsBroken" => row.BedsRank,
        "FinalKills" => row.FinalsRank,
        "Kills" => row.KillsRank,
        "Matches" => row.MatchesRank,
        "KD" => row.KDRank,
        "Adr" => row.AdrRank,
        "HsRate" => row.HsRateRank,
        "Kast" => row.KastRank,
        "Plants" => row.PlantsRank,
        "Defuses" => row.DefusesRank,
        "MaxRating" => row.MaxRatingRank,
        _ => row.RatingRank
    };

    async Task SwitchLeaderboardCategoryAsync(string metric, bool descending = true)
    {
        var mode = _mode;
        var leaderboardVersion = _leaderboardVersion;
        var viewVersion = ++_leaderboardViewVersion;
        _lbSortKey = metric;
        _lbSortDescending = descending;

        if (_currentLeaderboard.Players.Count == 0 || _currentLeaderboard.Mode != mode)
        {
            var lb = await _statsApi.GetLeaderboardAsync(mode).ConfigureAwait(true);
            if (_closing || mode != _mode || leaderboardVersion != _leaderboardVersion || viewVersion != _leaderboardViewVersion) return;
            _currentLeaderboard = lb;
            foreach (var p in Players) p.ApplyLeaderboard(_currentLeaderboard);
            LastSearchResult?.ApplyLeaderboard(_currentLeaderboard);
        }

        if (_closing || mode != _mode || leaderboardVersion != _leaderboardVersion || viewVersion != _leaderboardViewVersion) return;

        bool ce = mode == "counterember";
        foreach (var col in LeaderboardGrid.Columns)
        {
            var k = col.SortMemberPath;
            bool bwOnly = k is "Fkdr" or "BedsBroken" or "FinalKills";
            bool ceOnly = k is "KD" or "Adr" or "HsRate" or "Kast" or "Plants" or "Defuses" or "MaxRating";
            col.Visibility = (bwOnly && ce) || (ceOnly && !ce) ? Visibility.Collapsed : Visibility.Visible;
            col.SortDirection = col.SortMemberPath == metric ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
        }

        if (LbCategoryBar != null)
        {
            LbCatFkdr.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatBeds.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatFinals.Visibility = ce ? Visibility.Collapsed : Visibility.Visible;
            LbCatKd.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            LbCatAdr.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            LbCatHsRate.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            foreach (var tab in _counterEmberLeaderboardTabs)
                tab.Visibility = ce ? Visibility.Visible : Visibility.Collapsed;
            UpdateCategoryButtons(metric);
        }

        string metricName = metric switch
        {
            "Rating" => "Rating 榜",
            "WinRate" => "胜率榜",
            "MVPs" => "MVP 榜",
            "Fkdr" => "FKDR 榜",
            "BedsBroken" => "拆床榜",
            "FinalKills" => "终杀榜",
            "Kills" => "击杀榜",
            "Matches" => "场次榜",
            "KD" => "K/D 榜",
            "Adr" => "ADR 榜",
            "HsRate" => "爆头率榜",
            "Kast" => "KAST 榜",
            "Plants" => "下包榜",
            "Defuses" => "拆包榜",
            "MaxRating" => "最高 Rating 榜",
            _ => "Rating 榜"
        };
        LbSubTitle.Text = $" (Top 50 · {metricName}) — {(ce ? "CounterEmber" : "起床战争")}";

        var allRows = _currentLeaderboard.Players
            .Select(p => FromLeaderboardPlayer(p, mode, _currentLeaderboard))
            .ToList();

        IEnumerable<PlayerRow> sorted = metric switch
        {
            "Rating" => allRows.OrderByDescending(r => r.OverallRating ?? 0.0).ThenByDescending(r => r.Matches),
            "WinRate" => allRows.OrderByDescending(r => r.WinRate).ThenByDescending(r => r.Matches),
            "MVPs" => allRows.OrderByDescending(r => r.MVPs).ThenByDescending(r => r.Matches),
            "Fkdr" => allRows.OrderByDescending(r => r.Fkdr).ThenByDescending(r => r.FinalKills),
            "BedsBroken" => allRows.OrderByDescending(r => r.BedsBroken).ThenByDescending(r => r.Matches),
            "FinalKills" => allRows.OrderByDescending(r => r.FinalKills).ThenByDescending(r => r.Matches),
            "Kills" => allRows.OrderByDescending(r => r.Kills).ThenByDescending(r => r.Matches),
            "Matches" => allRows.OrderByDescending(r => r.Matches).ThenByDescending(r => r.Wins),
            "KD" => allRows.OrderByDescending(r => r.KD).ThenByDescending(r => r.Kills),
            "Adr" => allRows.OrderByDescending(r => r.Adr).ThenByDescending(r => r.Matches),
            "HsRate" => allRows.OrderByDescending(r => r.HsRate).ThenByDescending(r => r.Matches),
            "Kast" => allRows.OrderByDescending(r => r.Kast).ThenByDescending(r => r.Matches),
            "Plants" => allRows.OrderByDescending(r => r.Plants).ThenByDescending(r => r.Matches),
            "Defuses" => allRows.OrderByDescending(r => r.Defuses).ThenByDescending(r => r.Matches),
            "MaxRating" => allRows.OrderByDescending(r => r.MaxRating).ThenByDescending(r => r.Matches),
            _ => allRows.OrderByDescending(r => r.OverallRating ?? 0.0).ThenByDescending(r => r.Matches)
        };

        if (!descending)
            sorted = sorted.Reverse();

        var top50 = sorted.Take(50).ToList();
        for (int i = 0; i < top50.Count; i++)
        {
            var r = top50[i];
            var rk = GetRankForMetric(r, metric);
            if (rk == null || rk.Rank == null || rk.Rank <= 0)
                rk = new StatRankInfo(i + 1);
            r.LeaderboardDisplayRank = rk;
        }

        LeaderboardRows.Clear();

        if (!string.IsNullOrWhiteSpace(_myPlayerName))
        {
            PlayerRow? myRow = allRows.FirstOrDefault(r => string.Equals(r.Username, _myPlayerName, StringComparison.OrdinalIgnoreCase));
            if (myRow == null)
            {
                try
                {
                    var fetched = await _statsApi.FetchAsync(_myPlayerName, mode, CancellationToken.None).ConfigureAwait(true);
                    if (_closing || mode != _mode || leaderboardVersion != _leaderboardVersion || viewVersion != _leaderboardViewVersion) return;
                    if (fetched.State == "ok")
                    {
                        myRow = new PlayerRow();
                        myRow.CopyFrom(fetched);
                        myRow.ApplyLeaderboard(_currentLeaderboard);
                    }
                }
                catch { }
            }

            if (myRow != null)
            {
                var pinned = new PlayerRow();
                pinned.CopyFrom(myRow);
                pinned.IsPinnedSelf = true;
                pinned.LeaderboardDisplayRank = GetRankForMetric(pinned, metric) ?? StatRankInfo.None;
                if (pinned.Avatar == null && _overlayImages.TryGetAvatar(pinned.Username, out var myAvatar))
                    pinned.Avatar = myAvatar;
                LeaderboardRows.Add(pinned);

                string valStr = metric switch
                {
                    "WinRate" => $"胜率: {pinned.WinRate:P1}",
                    "MVPs" => $"MVP: {pinned.MVPs}",
                    "Fkdr" => $"FKDR: {pinned.Fkdr:F2}",
                    "BedsBroken" => $"拆床: {pinned.BedsBroken}",
                    "FinalKills" => $"终杀: {pinned.FinalKills}",
                    "Kills" => $"击杀: {pinned.Kills}",
                    "Matches" => $"场次: {pinned.Matches}",
                    "KD" => $"K/D: {pinned.KD:F2}",
                    "Adr" => $"ADR: {pinned.Adr:F1}",
                    "HsRate" => $"爆头率: {pinned.HsRate:P1}",
                    "Kast" => $"KAST: {pinned.Kast:P1}",
                    "Plants" => $"下包: {pinned.Plants}",
                    "Defuses" => $"拆包: {pinned.Defuses}",
                    "MaxRating" => $"最高 Rating: {pinned.MaxRatingLabel}",
                    _ => $"Rating: {pinned.RatingLabel}"
                };
                LbMySummary.Text = $"我的全服排名: {pinned.LeaderboardDisplayRank.Tag} ({metricName}) · {valStr} · Rating: {pinned.RatingLabel} ({pinned.RatingRank.Tag})";
            }
            else
            {
                var placeholder = new PlayerRow
                {
                    Username = _myPlayerName,
                    State = "unregistered",
                    GameMode = _mode,
                    IsPinnedSelf = true,
                    LeaderboardDisplayRank = StatRankInfo.None
                };
                placeholder.ApplyLeaderboard(_currentLeaderboard);
                LeaderboardRows.Add(placeholder);
                LbMySummary.Text = "我的全服天梯排名: #- · 暂无战绩记录";
            }
        }
        else
        {
            LbMySummary.Text = "提示：在【设置】中绑定您的游戏 ID，即可在此置顶查看个人全服排名。";
        }

        foreach (var r in top50)
        {
            LeaderboardRows.Add(r);
        }

        foreach (var r in LeaderboardRows)
        {
            if (r.Avatar == null)
            {
                _ = LoadAvatar(r.Username, r);
            }
        }

        ((ListCollectionView)CollectionViewSource.GetDefaultView(LeaderboardRows)).CustomSort = new PlayerComparer(metric, descending);
    }

    PlayerRow FromLeaderboardPlayer(LeaderboardPlayer p, string mode, LeaderboardData lb)
    {
        var row = new PlayerRow
        {
            Username = p.Name,
            Uuid = p.Uuid,
            GameMode = mode,
            State = "ok",
            Matches = p.Matches,
            Wins = p.Wins,
            MVPs = p.Mvps,
            WinRate = p.WinRate,
            Fkdr = p.Metrics.Fkdr,
            BedsBroken = p.Metrics.BedsBroken,
            FinalKills = p.Metrics.FinalKills,
            Kills = p.Metrics.Kills,
            AvgBeds = p.Metrics.AvgBeds,
            KD = p.Metrics.Kd,
            Rating = p.Metrics.Rating,
            Adr = p.Metrics.Adr,
            HsRate = p.Metrics.HsRate,
            Kast = p.Metrics.Kast,
            Plants = p.Metrics.Plants,
            Defuses = p.Metrics.Defuses,
            MaxRating = p.Metrics.MaxRating
        };
        if (_overlayImages.TryGetAvatar(p.Name, out var cached))
        {
            row.Avatar = cached;
        }
        row.ApplyLeaderboard(lb);
        return row;
    }

    async void LeaderboardGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var key = column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;

        if (key == "Username")
        {
            bool desc = _lbSortKey == "Username" ? !_lbSortDescending : false;
            _lbSortKey = "Username";
            _lbSortDescending = desc;
            foreach (var c in LeaderboardGrid.Columns) c.SortDirection = null;
            column.SortDirection = desc ? ListSortDirection.Descending : ListSortDirection.Ascending;
            ((ListCollectionView)CollectionViewSource.GetDefaultView(LeaderboardRows)).CustomSort = new PlayerComparer("Username", desc);
            return;
        }

        string targetMetric = key switch
        {
            "名次" => _lbSortKey is not "Username" ? _lbSortKey : "Rating",
            "OverallRating" or "Rating" => "Rating",
            "WinRate" => "WinRate",
            "MVPs" => "MVPs",
            "Fkdr" => "Fkdr",
            "BedsBroken" => "BedsBroken",
            "FinalKills" => "FinalKills",
            "Kills" => "Kills",
            "Matches" => "Matches",
            "KD" => "KD",
            "Adr" => "Adr",
            "HsRate" => "HsRate",
            "Kast" => "Kast",
            "Plants" => "Plants",
            "Defuses" => "Defuses",
            "MaxRating" => "MaxRating",
            _ => "Rating"
        };

        bool descending = _lbSortKey == targetMetric ? !_lbSortDescending : true;
        await SwitchLeaderboardCategoryAsync(targetMetric, descending).ConfigureAwait(true);
    }

    void LeaderboardGrid_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && LeaderboardGrid.SelectedItem is PlayerRow row && row.State == "ok" && e.ClickCount == 1)
            Copy(row);
    }

    void LeaderboardGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LeaderboardGrid.SelectedItem is PlayerRow p && p.State == "ok")
        {
            LastSearchResult = p;
            Copy(p);
        }
    }

    void ClearRoster_Click(object sender, RoutedEventArgs e)
    {
        _rosterRequests.Cancel();
        Players.Clear();
        ByName.Clear();
        _alertedThreatPlayers.Clear();
        UpdateSummary();
        FooterStatus.Text = "已清空玩家列表";
    }

    void ThreatAlertBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _threatAlert = ThreatAlertBox.IsChecked == true;
        SaveConfig();
    }

    void UpdateRecentSearchChips()
    {
        if (RecentSearchesPanel == null || RecentSearchesRow == null) return;
        RecentSearchesPanel.Children.Clear();
        if (_recentSearches.Count == 0)
        {
            RecentSearchesRow.Visibility = Visibility.Collapsed;
            return;
        }
        RecentSearchesRow.Visibility = Visibility.Visible;
        foreach (var sName in _recentSearches)
        {
            var btn = new Button
            {
                Content = sName,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 4),
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x3B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x4D, 0x66)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                Cursor = Cursors.Hand
            };
            btn.Click += async (_, _) =>
            {
                SearchBox.Text = sName;
                await Search();
            };
            RecentSearchesPanel.Children.Add(btn);
        }
    }

    void ApplyHotkey(int vk, uint mods, string name, bool saveConfig)
    {
        if (_interactiveServices)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                if (_hotkeyRegistered)
                {
                    UnregisterHotKey(hwnd, 1);
                    _hotkeyRegistered = false;
                }
                bool ok = RegisterHotKey(hwnd, 1, mods | 0x4000, (uint)vk);
                if (!ok)
                    ok = RegisterHotKey(hwnd, 1, mods, (uint)vk);
                _hotkeyRegistered = ok;
            }
        }
        _hotkeyVk = vk;
        _hotkeyModifiers = mods;
        _hotkeyName = name;
        if (HotkeyBox != null) HotkeyBox.Text = name;
        if (HotkeyHintText != null) HotkeyHintText.Text = $"按 {name} 键显示或隐藏悬浮窗。";
        if (HotkeyStatus != null)
        {
            if (_hotkeyRegistered)
            {
                HotkeyStatus.Text = $"快捷键已生效：[{name}]";
                HotkeyStatus.Foreground = (Brush)FindResource("Green");
            }
            else
            {
                HotkeyStatus.Text = $"快捷键 [{name}] 已设置 (注册全局热键失败，可能被占用)";
                HotkeyStatus.Foreground = (Brush)FindResource("Amber");
            }
        }
        if (saveConfig) SaveConfig();
    }

    void RecordHotkey_Click(object sender, RoutedEventArgs e) => StartRecordingHotkey();
    void HotkeyBox_MouseDown(object sender, MouseButtonEventArgs e) => StartRecordingHotkey();

    void StartRecordingHotkey()
    {
        _recordingHotkey = true;
        HotkeyBox.Focus();
        HotkeyBox.Text = "请按下快捷键... (按 Esc 取消)";
        HotkeyStatus.Text = "正在监听键盘输入... 支持 F1~F12, 反斜杠, 字母等, 可配合 Ctrl/Alt/Shift";
        HotkeyStatus.Foreground = (Brush)FindResource("Amber");
    }

    void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey)
        {
            _recordingHotkey = false;
            HotkeyBox.Text = _hotkeyName;
            HotkeyStatus.Text = $"当前快捷键：[{_hotkeyName}]";
            HotkeyStatus.Foreground = (Brush)FindResource("Dim");
        }
    }

    void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = false;
        ApplyHotkey(VK_BACKSLASH, 0, "\\", true);
    }

    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey)
        {
            StartRecordingHotkey();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _recordingHotkey = false;
            HotkeyBox.Text = _hotkeyName;
            HotkeyStatus.Text = "已取消快捷键修改";
            HotkeyStatus.Foreground = (Brush)FindResource("Dim");
            e.Handled = true;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        uint mods = 0;
        var parts = new List<string>();
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) { mods |= 0x0002; parts.Add("Ctrl"); }
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) { mods |= 0x0001; parts.Add("Alt"); }
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) { mods |= 0x0004; parts.Add("Shift"); }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        string keyName = GetKeyFriendlyName(key, vk);
        string fullName = parts.Count > 0 ? $"{string.Join(" + ", parts)} + {keyName}" : keyName;

        ApplyHotkey(vk, mods, fullName, true);
        _recordingHotkey = false;
        e.Handled = true;
    }

    static string GetKeyFriendlyName(Key key, int vk)
    {
        return key switch
        {
            Key.Oem5 => "\\",
            Key.Oem3 => "`",
            Key.Oem1 => ";",
            Key.OemPlus => "+",
            Key.OemComma => ",",
            Key.OemMinus => "-",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            Key.Oem4 => "[",
            Key.Oem6 => "]",
            Key.Oem7 => "'",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Space => "Space",
            _ when key >= Key.F1 && key <= Key.F24 => key.ToString(),
            _ when key >= Key.A && key <= Key.Z => key.ToString(),
            _ when key >= Key.D0 && key <= Key.D9 => key.ToString().TrimStart('D'),
            _ when key >= Key.NumPad0 && key <= Key.NumPad9 => "Num" + key.ToString().Substring(6),
            _ => key.ToString()
        };
    }

}

using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly HttpClient WallpaperHttp = new() { Timeout = TimeSpan.FromSeconds(45) };
    readonly StatsApiClient _statsApi;
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
    BitmapSource? _cardImage;
    BitmapSource? _officialWallpaper;
    string _cardName = "";
    double _resizeWidth;
    string _statusBeforeResize = "";
    static readonly ConcurrentDictionary<string, ImageSource> AvatarCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim AvatarSlots = new(4, 4);
    static readonly Regex UserRegex = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    LeaderboardData _currentLeaderboard = LeaderboardData.Empty;
    readonly ObservableCollection<PlayerRow> Players = [];
    readonly Dictionary<string, PlayerRow> ByName = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer KeyTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    HwndSource? Source; PlayerRow? LastSearchResult; bool _hidden, _forceImc = true, _hotkeyRegistered, _wasHotkey, _wasTab, _wasSpace;
    DateTime _spaceAt; const double AspectRatio = 980d / 552d;

    public MainWindow() : this(new StatsApiClient(), true) { }

    internal MainWindow(StatsApiClient statsApi, bool startLogWatcher)
    {
        _statsApi = statsApi; _interactiveServices = startLogWatcher;
        InitializeComponent(); PlayersGrid.ItemsSource = Players;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, _sortDescending);
        PlayersGrid.Columns.First(c => c.SortMemberPath == _sortKey).SortDirection = ListSortDirection.Descending;
        if (!Http.DefaultRequestHeaders.UserAgent.Any()) Http.DefaultRequestHeaders.UserAgent.ParseAdd("IMC-BedWars-Overlay/2.0");
        LoadConfig();
        _logWatcher = new MinecraftLogWatcher(OnLogEvent, _forceImc);
        _logWatcher.ActiveLogDetected += OnActiveLogDetected;
        ApplyMode(_mode, false);
        if (_interactiveServices) { KeyTimer.Tick += PollKeys; KeyTimer.Start(); }
        if (startLogWatcher) _logWatcher.Start();
        if (_interactiveServices) _ = RefreshLeaderboardAsync(_mode);
        Players.CollectionChanged += (_, _) => UpdateSummary();
    }

    void LoadConfig()
    {
        var settings = _config.Load(); _forceImc = settings.ForceImc; _mode = settings.GameMode; OpacitySlider.Value = settings.Opacity;
        ForceImcBox.IsChecked = _forceImc;
        ModeCombo.SelectedIndex = _mode == "counterember" ? 1 : 0;
        Opacity = OpacitySlider.Value;
    }
    void SaveConfig()
    {
        _config.Save(new AppSettings(_forceImc, OpacitySlider.Value, _mode));
    }
    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_interactiveServices) return;
        var hwnd = new WindowInteropHelper(this).Handle; Source = HwndSource.FromHwnd(hwnd); Source.AddHook(WndProc);
        _hotkeyRegistered = RegisterHotKey(hwnd, 1, 0, VK_BACKSLASH); SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
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
        if (!_hotkeyRegistered) { bool hotkey = GetAsyncKeyState(VK_BACKSLASH) < 0; if (hotkey && !_wasHotkey) ToggleVisibility(); _wasHotkey = hotkey; }
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
        Players.Clear(); ByName.Clear(); foreach (var n in names) AddPlayer(n);
    }
    void AddPlayer(string name)
    {
        if (!UserRegex.IsMatch(name)) return;
        if (ByName.ContainsKey(name)) return;
        var mode = _mode; var row = new PlayerRow { Username = name, State = "loading", GameMode = mode }; ByName[name] = row; Players.Add(row); _ = LoadStats(name, row, mode, _rosterRequests.Token); _ = LoadAvatar(name, row);
    }
    async Task LoadAvatar(string name, PlayerRow row)
    {
        if (AvatarCache.TryGetValue(name, out var cached)) { row.Avatar = cached; return; }
        await AvatarSlots.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync("https://mc-heads.net/avatar/" + Uri.EscapeDataString(name) + "/32");
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            AvatarCache[name] = image; row.Avatar = image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or FormatException or ArgumentException) { }
        finally { AvatarSlots.Release(); }
    }
    async Task LoadStats(string name, PlayerRow target, string mode, CancellationToken token)
    {
        try
        {
            var result = await _statsApi.FetchAsync(name, mode, token);
            if (!token.IsCancellationRequested && !_closing && _mode == mode && ByName.TryGetValue(name, out var current) && ReferenceEquals(current, target))
            { result.ApplyLeaderboard(_currentLeaderboard); target.CopyFrom(result); CollectionViewSource.GetDefaultView(Players).Refresh(); }
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
        PlayerRow result;
        try { result = await _statsApi.FetchAsync(name, mode, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (token.IsCancellationRequested || version != _searchVersion || mode != _mode || _closing) return; result?.ApplyLeaderboard(_currentLeaderboard); LastSearchResult = result;
        if (result?.State == "ok") { ModalMessage.Text = FormatStats(result); CopyButton.Visibility = Visibility.Visible; CardButton.Visibility = Visibility.Visible; }
        else ModalMessage.Text = result?.State == "unregistered" ? "未找到该玩家在当前模式的记录。" : $"查询失败：{result?.Error ?? "请检查网络后重试。"}";
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
    void Search_Click(object sender, RoutedEventArgs e) { _searchRequests.Cancel(); _searchVersion++; LastSearchResult = null; ModalTitle.Text = "查询玩家战绩"; SearchBox.Text = ""; ModalMessage.Text = "输入玩家名，最多自动重试 3 次。"; CopyButton.Visibility = Visibility.Collapsed; CardButton.Visibility = Visibility.Collapsed; ShowPanel(ModalShade); SearchBox.Focus(); }
    void OnActiveLogDetected(string path)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            LogPathBox.Text = path;
            FooterStatus.ToolTip = $"当前客户端日志: {path}";
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
        if (!string.IsNullOrEmpty(_logWatcher.ActiveLogPath))
            LogPathBox.Text = _logWatcher.ActiveLogPath;
        ShowPanel(SettingsShade);
    }
    void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Opacity = e.NewValue; }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ModalClose_Click(object sender, RoutedEventArgs e) { _searchRequests.Cancel(); _searchVersion++; HidePanel(ModalShade); }
    void SettingsClose_Click(object sender, RoutedEventArgs e) { _forceImc = ForceImcBox.IsChecked == true; _logWatcher.SetForceImc(_forceImc); ApplyMode(ModeCombo.SelectedIndex == 1 ? "counterember" : "bedwars", true); SaveConfig(); HidePanel(SettingsShade); }
    void ApplyMode(string mode, bool refresh)
    {
        bool changed = _mode != mode; _mode = mode; _searchRequests.Cancel(); _searchVersion++;
        ModeTitle.Text = mode == "counterember" ? "CounterEmber" : "起床战争";
        bool ce = mode == "counterember";
        foreach (var col in PlayersGrid.Columns)
        {
            var key = col.SortMemberPath;
            bool bwOnly = key is "threat" or "Fkdr" or "BedsBroken" or "FinalKills";
            bool ceOnly = key is "KD" or "Adr" or "HsRate";
            col.Visibility = (bwOnly && ce) || (ceOnly && !ce) ? Visibility.Collapsed : Visibility.Visible;
        }
        _sortKey = "Rating"; _sortDescending = true;
        foreach (var col in PlayersGrid.Columns) col.SortDirection = col.SortMemberPath == _sortKey ? ListSortDirection.Descending : null;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, true);
        if (changed && refresh) { var names = Players.Select(p => p.Username).ToArray(); SetRoster(names); FooterStatus.Text = "已切换模式，正在重新查询名单…"; LastSearchResult = null; }
        if (_interactiveServices) _ = RefreshLeaderboardAsync(mode);
    }
    async Task RefreshLeaderboardAsync(string mode)
    {
        try
        {
            var lb = await _statsApi.GetLeaderboardAsync(mode).ConfigureAwait(false);
            if (mode == _mode)
            {
                _currentLeaderboard = lb;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_closing) return;
                    foreach (var p in Players) p.ApplyLeaderboard(_currentLeaderboard);
                    LastSearchResult?.ApplyLeaderboard(_currentLeaderboard);
                    CollectionViewSource.GetDefaultView(Players).Refresh();
                });
            }
        }
        catch { }
    }
    void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } }
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
        CardPreview.Source = null; SaveCardButton.IsEnabled = false; CardSaveStatus.Text = "正在加载 Minecraft 官方壁纸…"; ShowPanel(CardShade);
        try
        {
            var wallpaperTask = LoadOfficialWallpaper();
            var avatarTask = player.Avatar == null ? LoadAvatar(player.Username, player) : Task.CompletedTask;
            await Task.WhenAll(wallpaperTask, avatarTask);
            if (version != _cardVersion) return;
            _cardImage = StatsCardRenderer.Render(player, await wallpaperTask, DateTime.Now);
            CardPreview.Source = _cardImage; SaveCardButton.IsEnabled = true; CardSaveStatus.Text = "保存后将自动复制图片，文件保存在「图片\\IMC.RE」文件夹。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or NotSupportedException or FormatException)
        {
            if (version == _cardVersion) CardSaveStatus.Text = "官方壁纸加载失败，请检查网络后重试。";
        }
    }
    async Task<BitmapSource> LoadOfficialWallpaper()
    {
        if (_officialWallpaper != null) return _officialWallpaper;
        const string url = "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/software/wallpapers_minecraft_ocean_monument_.zip";
        using var response = await WallpaperHttp.GetAsync(url);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 30_000_000) throw new InvalidDataException("Wallpaper archive is too large.");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 30_000_000) throw new InvalidDataException("Wallpaper archive is too large.");
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("wallpaper_minecraft_ocean_monument_1920x1080.png") ?? throw new InvalidDataException("Wallpaper not found.");
        if (entry.Length > 10_000_000) throw new InvalidDataException("Wallpaper is too large.");
        using var entryStream = entry.Open();
        using var imageStream = new MemoryStream(); entryStream.CopyTo(imageStream); imageStream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = imageStream; image.EndInit(); image.Freeze();
        _officialWallpaper = image; return image;
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
        ? $"[IMC.RE CounterEmber] {p.Username} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | Rating: {p.Rating:F2} | K/D: {p.KD:F2} | ADR: {p.Adr:F1} | 爆头率: {p.HsRate:P1} | MVP: {p.MVPs} | 击杀: {p.Kills}"
        : $"[IMC.RE 起床战争] {p.Username} | 估算 Rating: {p.RatingLabel} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | FKDR: {p.Fkdr:F2} | 拆床: {p.BedsBroken} | 终杀: {p.FinalKills} | MVP: {p.MVPs} | 击杀: {p.Kills}";
    void UpdateSummary()
    {
        EmptyPanel.Visibility = Players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

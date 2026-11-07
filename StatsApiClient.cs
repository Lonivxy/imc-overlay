using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace IMCBedwarsOverlay;

internal sealed record LeaderboardFetchResult(LeaderboardData Data, bool IsFresh);

internal sealed class StatsApiClient
{
    readonly HttpClient _http;
    readonly SemaphoreSlim _slots = new(6, 6);
    readonly SemaphoreSlim _startGate = new(1, 1);
    readonly ConcurrentDictionary<string, (PlayerRow Row, DateTime Time)> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (LeaderboardData Data, DateTime Time)> _lbCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (KitBattleLeaderboardResponse Data, DateTime Time)> _kitBattleLbCache = new(StringComparer.OrdinalIgnoreCase);
    long _lastStartTicks;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public StatsApiClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("IMC-Overlay/2.1");
    }

    public async Task<(bool Success, string Message, long ElapsedMs)> CheckConnectivityAsync(CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://api.imc.re/stats/bedwars/leaderboard");
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (res.IsSuccessStatusCode)
                return (true, $"连接成功（{sw.ElapsedMilliseconds} ms）", sw.ElapsedMilliseconds);

            using var getReq = new HttpRequestMessage(HttpMethod.Get, "https://api.imc.re/stats/bedwars/leaderboard");
            using var getRes = await _http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (getRes.IsSuccessStatusCode)
                return (true, $"连接成功（{sw.ElapsedMilliseconds} ms）", sw.ElapsedMilliseconds);

            return (false, "无法连接到 api.imc.re，请检查防火墙或网络设置", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            sw.Stop();
            return (false, "无法连接到 api.imc.re，请检查防火墙或网络设置", sw.ElapsedMilliseconds);
        }
    }

    public async Task<LeaderboardData> GetLeaderboardAsync(string mode, bool force = false, CancellationToken token = default)
        => (await GetLeaderboardWithStatusAsync(mode, force, token).ConfigureAwait(false)).Data;

    public async Task<LeaderboardFetchResult> GetLeaderboardWithStatusAsync(string mode, bool force = false, CancellationToken token = default)
    {
        if (!force && _lbCache.TryGetValue(mode, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(10))
            return new(cached.Data, false);

        try
        {
            await WaitForRequestStartAsync(token).ConfigureAwait(false);
            using var response = await _http.GetAsync("https://api.imc.re/stats/" + mode + "/leaderboard", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var res = await JsonSerializer.DeserializeAsync<LeaderboardResponse>(stream, JsonOptions, token).ConfigureAwait(false);
                if (res?.Players != null)
                {
                    var data = new LeaderboardData(res.Players, mode, DateTime.UtcNow);
                    _lbCache[mode] = (data, DateTime.UtcNow);
                    return new(data, true);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }

        if (_lbCache.TryGetValue(mode, out var old))
            return new(old.Data, false);

        return new(new LeaderboardData([], mode, DateTime.MinValue), false);
    }

    public async Task<PlayerRow> FetchAsync(string name, string mode, CancellationToken token)
    {
        if (mode == "kitbattle") return await FetchKitBattleAsync(name, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var cacheKey = mode + ":" + name;
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(3)) return cached.Row;
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                TimeSpan? retryDelay = null;
                try
                {
                    await WaitForRequestStartAsync(token).ConfigureAwait(false);

                    using var response = await _http.GetAsync("https://api.imc.re/stats/" + mode + "/players/" + Uri.EscapeDataString(name), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound) return new PlayerRow { Username = name, State = "unregistered", Error = "无记录", GameMode = mode };
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                    {
                        last = new HttpRequestException($"服务暂时不可用（HTTP {(int)response.StatusCode}）");
                        retryDelay = GetRetryAfter(response);
                    }
                    else if (!response.IsSuccessStatusCode)
                        return new PlayerRow { Username = name, State = "error", GameMode = mode, Error = $"请求失败（HTTP {(int)response.StatusCode}）" };
                    else
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        var data = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, JsonOptions, token).ConfigureAwait(false);
                        if (data?.Summary?.Metrics == null) throw new InvalidDataException("服务返回的数据格式不正确");
                        var s = data.Summary; var m = s.Metrics;
                        var row = new PlayerRow
                        {
                            Username = PlayerNameRules.PreserveRequestedIdentity(name, s.Name),
                            Uuid = s.Uuid,
                            State = "ok",
                            GameMode = mode,
                            Matches = s.Matches,
                            Wins = s.Wins,
                            MVPs = s.Mvps,
                            WinRate = s.WinRate,
                            Fkdr = m.Fkdr,
                            BedsBroken = m.BedsBroken,
                            FinalKills = m.FinalKills,
                            Kills = m.Kills,
                            AvgBeds = m.AvgBeds,
                            KD = m.Kd,
                            Rating = m.Rating,
                            Adr = m.Adr,
                            HsRate = m.HsRate,
                            Kast = m.Kast,
                            Plants = m.Plants,
                            Defuses = m.Defuses,
                            MaxRating = m.MaxRating,
                            RatingTrend = data.Trend ?? [],
                            RecentMatches = data.Recent ?? []
                        };
                        if (_lbCache.TryGetValue(mode, out var lbData)) row.ApplyLeaderboard(lbData.Data);
                        token.ThrowIfCancellationRequested();
                        _cache[cacheKey] = (row, DateTime.UtcNow);
                        return row;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException) { last = ex; }
                if (attempt < 2)
                {
                    var fallback = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 251));
                    await WaitForRetryAsync(retryDelay ?? fallback, token).ConfigureAwait(false);
                }
            }
            return new PlayerRow { Username = name, State = "error", GameMode = mode, Error = last is TaskCanceledException ? "连接超时" : last?.Message ?? "请求失败" };
        }
        finally { _slots.Release(); }
    }

    async Task<PlayerRow> FetchKitBattleAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cacheKey = "kitbattle:" + name;
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(3)) return cached.Row;
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                TimeSpan? retryDelay = null;
                try
                {
                    await WaitForRequestStartAsync(token).ConfigureAwait(false);
                    using var response = await _http.GetAsync("https://api.imc.re/kitbattle/players/" + Uri.EscapeDataString(name), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return new PlayerRow { Username = name, State = "unregistered", Error = "无记录", GameMode = "kitbattle" };
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                    {
                        last = new HttpRequestException($"服务暂时不可用（HTTP {(int)response.StatusCode}）");
                        retryDelay = GetRetryAfter(response);
                    }
                    else if (!response.IsSuccessStatusCode)
                    {
                        return new PlayerRow { Username = name, State = "error", GameMode = "kitbattle", Error = $"请求失败（HTTP {(int)response.StatusCode}）" };
                    }
                    else
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        var data = await JsonSerializer.DeserializeAsync<KitBattleProfileResponse>(stream, JsonOptions, token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("服务返回的数据格式不正确");
                        if (string.IsNullOrWhiteSpace(data.Name)) throw new InvalidDataException("服务返回的数据缺少玩家名");
                        var row = new PlayerRow
                        {
                            Username = PlayerNameRules.PreserveRequestedIdentity(name, data.Name),
                            Uuid = data.Uuid,
                            State = "ok",
                            GameMode = "kitbattle",
                            Kills = data.Totals.Kills,
                            KbExperience = data.Totals.Experience,
                            KbChallengeWins = data.Totals.ChallengeWins,
                            KbBestStreak = data.Tracked.BestStreak,
                            KbKillsRank = data.Positions.Kills?.Position,
                            KbRankTitle = data.Rank.Title,
                            Matches = data.Totals.Kills + data.Totals.Deaths,
                            KD = data.Kd ?? (data.Totals.Deaths > 0 ? (double)data.Totals.Kills / data.Totals.Deaths : data.Totals.Kills)
                        };
                        token.ThrowIfCancellationRequested();
                        _cache[cacheKey] = (row, DateTime.UtcNow);
                        return row;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException) { last = ex; }

                if (attempt < 2)
                {
                    var fallback = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 251));
                    await WaitForRetryAsync(retryDelay ?? fallback, token).ConfigureAwait(false);
                }
            }
            return new PlayerRow { Username = name, State = "error", GameMode = "kitbattle", Error = last is TaskCanceledException ? "连接超时" : last?.Message ?? "请求失败" };
        }
        finally { _slots.Release(); }
    }

    public async Task<KitBattleLeaderboardFetchResult> GetKitBattleLeaderboardAsync(
        string metric, int page, bool force = false, CancellationToken token = default)
    {
        metric = metric.ToLowerInvariant() switch { "kd" or "exp" => metric.ToLowerInvariant(), _ => "kills" };
        page = Math.Max(1, page);
        var key = $"{metric}:{page}";
        if (!force && _kitBattleLbCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(10))
            return new(cached.Data, false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            TimeSpan? retryDelay = null;
            var shouldRetry = false;
            try
            {
                await WaitForRequestStartAsync(token).ConfigureAwait(false);
                var url = $"https://api.imc.re/kitbattle/leaderboard?metric={metric}&page={page}&pageSize=50";
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var data = await JsonSerializer.DeserializeAsync<KitBattleLeaderboardResponse>(stream, JsonOptions, token).ConfigureAwait(false);
                    if (data is not null && data.Players is not null)
                    {
                        if (data.Page <= 0) data.Page = page;
                        if (data.PageSize <= 0) data.PageSize = 50;
                        data.Metric = metric;
                        _kitBattleLbCache[key] = (data, DateTime.UtcNow);
                        return new(data, true);
                    }
                    shouldRetry = true;
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                {
                    shouldRetry = true;
                    retryDelay = GetRetryAfter(response);
                }
                else break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException) { shouldRetry = true; }

            if (!shouldRetry || attempt == 2) break;
            var fallback = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 251));
            await WaitForRetryAsync(retryDelay ?? fallback, token).ConfigureAwait(false);
        }

        if (_kitBattleLbCache.TryGetValue(key, out var stale)) return new(stale.Data, false);
        return new(new KitBattleLeaderboardResponse { Metric = metric, Page = page, PageSize = 50 }, false);
    }

    async Task WaitForRequestStartAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastStartTicks);
            if (_lastStartTicks != 0 && elapsed < TimeSpan.FromMilliseconds(50))
                await Task.Delay(TimeSpan.FromMilliseconds(50) - elapsed, token).ConfigureAwait(false);
            _lastStartTicks = Stopwatch.GetTimestamp();
        }
        finally { _startGate.Release(); }
    }

    static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is DateTimeOffset date) { var remaining = date - DateTimeOffset.UtcNow; return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero; }
        return null;
    }

    static async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
    {
        while (delay > TimeSpan.Zero)
        {
            var part = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
            await Task.Delay(part, token).ConfigureAwait(false);
            delay -= part;
        }
    }
}

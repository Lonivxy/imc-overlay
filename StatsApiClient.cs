using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace IMCBedwarsOverlay;

internal sealed class StatsApiClient
{
    readonly HttpClient _http;
    readonly SemaphoreSlim _slots = new(6, 6);
    readonly SemaphoreSlim _startGate = new(1, 1);
    readonly ConcurrentDictionary<string, (PlayerRow Row, DateTime Time)> _cache = new(StringComparer.OrdinalIgnoreCase);
    long _lastStartTicks;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public StatsApiClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("IMC-BedWars-Overlay/2.0");
    }

    public async Task<PlayerRow> FetchAsync(string name, string mode, CancellationToken token)
    {
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
                    await _startGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var elapsed = Stopwatch.GetElapsedTime(_lastStartTicks);
                        if (_lastStartTicks != 0 && elapsed < TimeSpan.FromMilliseconds(50)) await Task.Delay(TimeSpan.FromMilliseconds(50) - elapsed, token).ConfigureAwait(false);
                        _lastStartTicks = Stopwatch.GetTimestamp();
                    }
                    finally { _startGate.Release(); }

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
                        var row = new PlayerRow { Username = string.IsNullOrWhiteSpace(s.Name) ? name : s.Name, State = "ok", GameMode = mode, Matches = s.Matches, Wins = s.Wins, MVPs = s.Mvps, WinRate = s.WinRate, Fkdr = m.Fkdr, BedsBroken = m.BedsBroken, FinalKills = m.FinalKills, Kills = m.Kills, AvgBeds = m.AvgBeds, KD = m.Kd, Rating = m.Rating, Adr = m.Adr, HsRate = m.HsRate };
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

    static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is DateTimeOffset date) { var remaining = date - DateTimeOffset.UtcNow; return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero; }
        return null;
    }

    static async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
    {
        // Task.Delay has an upper limit; keep long Retry-After values cancellable without retrying early.
        while (delay > TimeSpan.Zero)
        {
            var part = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
            await Task.Delay(part, token).ConfigureAwait(false);
            delay -= part;
        }
    }
}

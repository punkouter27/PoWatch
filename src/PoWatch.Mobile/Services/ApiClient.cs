using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

/// <summary>
/// The phone's calls to the PoWatch API, mirroring PoWatch.Client's PoWatchApiClient (same URLs, same
/// failure policy: a transport error, a timeout or a non-success status reads as null/false, except
/// <see cref="PostBatchAsync"/>, whose caller must tell a refusal from an outage).
/// </summary>
public sealed class ApiClient(HttpClient http, MobileConfig config)
{
    private static readonly MobileJsonContext Json = MobileJsonContext.Default;

    public static bool Online => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public Uri U(string path) => new(new Uri(config.BaseUrl + "/"), path.TrimStart('/'));

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Tz => Uri.EscapeDataString(TimeZoneInfo.Local.Id);

    // ── Auth ──
    public Task<AuthStateDto?> GetMeAsync() => ReadAsync(ct => http.GetAsync(U("auth/me"), ct), Json.AuthStateDto);

    public Task<AuthConfigDto?> GetAuthConfigAsync() => ReadAsync(ct => http.GetAsync(U("auth/config"), ct), Json.AuthConfigDto);

    /// <summary>Debug guest: the Dev/Test-only fake sign-in sets the session cookie (redirects are followed).</summary>
    public Task<bool> GuestLoginAsync() => SendAsync(ct => http.GetAsync(U("auth/login/fake?returnUrl=/"), ct));

    public Task<bool> LogoutAsync() => SendAsync(ct => http.PostAsync(U("auth/logout"), null, ct));

    public async Task<(DiagReportDto? Report, TimeSpan Elapsed)> GetDiagAsync()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        DiagReportDto? report = await ReadAsync(ct => http.GetAsync(U("api/diag"), ct), Json.DiagReportDto);
        return (report, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    // ── Sessions & ingest ──
    public Task<SessionDto?> StartSessionAsync(Guid id) =>
        ReadAsync(ct => http.PostAsJsonAsync(U("api/sessions"), new StartSessionRequestDto { SessionId = id, TimeZoneId = TimeZoneInfo.Local.Id }, Json.StartSessionRequestDto, ct), Json.SessionDto);

    public Task<SessionDto?> StopSessionAsync(Guid id) => ReadAsync(ct => http.PostAsync(U($"api/sessions/{id}/stop"), null, ct), Json.SessionDto);

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(int take = 60) =>
        await ReadAsync(ct => http.GetAsync(U($"api/sessions?take={take}"), ct), Json.ListSessionDto) ?? [];

    /// <summary>Null = refused outright (retrying won't help); throws on an outage so the outbox keeps the batch.</summary>
    public async Task<IngestBatchResultDto?> PostBatchAsync(Guid sessionId, IngestBatchDto batch, CancellationToken ct)
    {
        using HttpResponseMessage resp = await http.PostAsJsonAsync(U($"api/sessions/{sessionId}/batches"), batch, Json.IngestBatchDto, ct);
        bool retryable = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        if (!retryable && (int)resp.StatusCode is >= 400 and < 500)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(Json.IngestBatchResultDto, ct);
    }

    public Task<SnapshotUploadDto?> CreateSnapshotUploadAsync(DateOnly localDay) =>
        ReadAsync(ct => http.PostAsync(U($"api/snapshots?day={Day(localDay)}"), null, ct), Json.SnapshotUploadDto);

    public Task<bool> RaiseAlertAsync(string text) =>
        SendAsync(ct => http.PostAsJsonAsync(U("api/alerts"), new AlertDto { AtUtc = DateTimeOffset.UtcNow, Text = text }, Json.AlertDto, ct));

    // ── Journal ──
    public Task<RecapDto?> GetDayRecapAsync(DateOnly day, bool ai) =>
        ReadAsync(ct => http.GetAsync(U($"api/recaps/day/{Day(day)}?tz={Tz}&ai={(ai ? "true" : "false")}"), ct), Json.RecapDto);

    public Task<SessionStoryDto?> UpdateStoryAsync(Guid sessionId) => ReadAsync(ct => http.PostAsync(U($"api/sessions/{sessionId}/story"), null, ct), Json.SessionStoryDto);

    public Task<PeriodStoryDto?> GetPeriodStoryAsync(bool month, DateOnly day) =>
        ReadAsync(ct => http.GetAsync(U($"api/stories/{(month ? "month" : "week")}?date={Day(day)}&tz={Tz}"), ct), Json.PeriodStoryDto);

    public async Task<IReadOnlyList<MomentDto>> GetMomentsAsync(Guid sessionId) =>
        await ReadAsync(ct => http.GetAsync(U($"api/sessions/{sessionId}/moments"), ct), Json.ListMomentDto) ?? [];

    public Task<WeekDigestDto?> GetWeekDigestAsync(DateOnly day) => ReadAsync(ct => http.GetAsync(U($"api/digest/week?date={Day(day)}&tz={Tz}"), ct), Json.WeekDigestDto);

    public Task<PresenceStatsDto?> GetPresenceAsync(string range, Guid? sessionId = null, DateOnly? date = null) => StatsAsync("presence", range, sessionId, date, Json.PresenceStatsDto);

    public Task<ObjectStatsDto?> GetObjectsAsync(string range, Guid? sessionId = null, DateOnly? date = null) => StatsAsync("objects", range, sessionId, date, Json.ObjectStatsDto);

    public Task<EnvironmentStatsDto?> GetEnvironmentAsync(string range, Guid? sessionId = null, DateOnly? date = null) => StatsAsync("environment", range, sessionId, date, Json.EnvironmentStatsDto);

    private Task<T?> StatsAsync<T>(string family, string range, Guid? sessionId, DateOnly? date, JsonTypeInfo<T> type) =>
        ReadAsync(ct => http.GetAsync(U($"api/stats/{family}?range={range}&tz={Tz}{(sessionId is { } s ? $"&sessionId={s}" : string.Empty)}{(date is { } d ? $"&date={Day(d)}" : string.Empty)}"), ct), type);

    // ── Regulars ──
    public Task<List<RegularDto>?> ListRegularsAsync() => ReadAsync(ct => http.GetAsync(U("api/regulars"), ct), Json.ListRegularDto);

    public Task<ObserveRegularResultDto?> ObserveRegularAsync(string label, IReadOnlyList<float> signature) =>
        ReadAsync(ct => http.PostAsJsonAsync(U("api/regulars/observe"), new ObserveRegularRequestDto { Class = label, Signature = [.. signature] }, Json.ObserveRegularRequestDto, ct), Json.ObserveRegularResultDto);

    // ── Settings ──
    public Task<ProfileDto?> GetProfileAsync() => ReadAsync(ct => http.GetAsync(U("api/profile"), ct), Json.ProfileDto);

    public Task<ProfileDto?> UpdateProfileAsync(UpdateProfileDto update) => ReadAsync(ct => http.PutAsJsonAsync(U("api/profile"), update, Json.UpdateProfileDto, ct), Json.ProfileDto);

    public async Task<IReadOnlyList<PlaceDto>> SearchPlacesAsync(string name) =>
        await ReadAsync(ct => http.GetAsync(U($"api/places?q={Uri.EscapeDataString(name)}"), ct), Json.ListPlaceDto) ?? [];

    public Task<AiModelsDto?> GetAiModelsAsync() => ReadAsync(ct => http.GetAsync(U("api/ai/models"), ct), Json.AiModelsDto);

    public Task<AiModelsDto?> SelectAiModelAsync(string id) => ReadAsync(ct => http.PutAsJsonAsync(U("api/ai/models"), new SelectAiModelDto(id), Json.SelectAiModelDto, ct), Json.AiModelsDto);

    public Task<ShareDto?> CreateShareAsync(DateOnly day, int days) =>
        ReadAsync(ct => http.PostAsJsonAsync(U("api/shares"), new CreateShareDto { Day = day, Days = days, TimeZoneId = TimeZoneInfo.Local.Id }, Json.CreateShareDto, ct), Json.ShareDto);

    public Task<List<ShareDto>?> ListSharesAsync() => ReadAsync(ct => http.GetAsync(U("api/shares"), ct), Json.ListShareDto);

    public Task<bool> RevokeShareAsync(string token) => SendAsync(ct => http.DeleteAsync(U($"api/shares/{Uri.EscapeDataString(token)}"), ct));

    public Task<bool> DeleteMyDataAsync() => SendAsync(ct => http.DeleteAsync(U("api/data"), ct));

    public Task<HttpResponseMessage> DownloadAsync(string path) => http.GetAsync(U(path));

    public static string ExportPath => $"api/export.csv?tz={Tz}";

    public static string DayPdfPath(DateOnly day) => $"api/recaps/day/{Day(day)}.pdf?tz={Tz}";

    private static async Task<T?> ReadAsync<T>(Func<CancellationToken, Task<HttpResponseMessage>> send, JsonTypeInfo<T> type)
    {
        try
        {
            using HttpResponseMessage resp = await send(CancellationToken.None);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync(type) : default;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private static async Task<bool> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send)
    {
        try
        {
            using HttpResponseMessage resp = await send(CancellationToken.None);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

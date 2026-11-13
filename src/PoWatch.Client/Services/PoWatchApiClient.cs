using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Services;

/// <summary>
/// Every call the app makes to its API. One failure policy for all of them: a transport error, a
/// timeout, an unreadable body or a non-success status reads as null (false for a plain send), so a
/// page shows "could not load" instead of Blazor's unhandled-error banner. A caller's own cancellation
/// still throws. The one exception is <see cref="PostBatchAsync"/>, whose caller needs to tell a
/// refusal from an outage.
/// </summary>
public sealed class PoWatchApiClient(HttpClient httpClient)
{
    private static readonly PoWatchJsonContext Json = PoWatchJsonContext.Default;

    public Task<SessionDto?> StartSessionAsync(StartSessionRequestDto request, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsJsonAsync("api/sessions", request, Json.StartSessionRequestDto, ct), Json.SessionDto, cancellationToken);

    public Task<SessionDto?> StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsync($"api/sessions/{sessionId}/stop", content: null, ct), Json.SessionDto, cancellationToken);

    /// <summary>One of the caller's sessions; null when it is not theirs, does not exist, or the call failed.</summary>
    public Task<SessionDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"api/sessions/{sessionId}", ct), Json.SessionDto, cancellationToken);

    /// <summary>Empty on failure: Live and the Journal treat the list as optional.</summary>
    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(int take = 20, CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync($"api/sessions?take={take}", ct), Json.ListSessionDto, cancellationToken) ?? [];

    /// <summary>Every session that overlaps the range, oldest first — however long ago; empty on failure.</summary>
    public async Task<IReadOnlyList<SessionDto>> ListSessionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync(
            $"api/sessions?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct), Json.ListSessionDto, cancellationToken) ?? [];

    /// <summary>
    /// Posts one ingest batch. Returns null when the server refused it outright (4xx) so the caller
    /// can drop it; throws on transport or server errors, an expired sign-in, a timeout or rate
    /// limiting, so the caller keeps it for a retry.
    /// </summary>
    public async Task<IngestBatchResultDto?> PostBatchAsync(Guid sessionId, IngestBatchDto batch, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/sessions/{sessionId}/batches", batch, Json.IngestBatchDto, cancellationToken);
        var retryable = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        if (!retryable && (int)response.StatusCode is >= 400 and < 500) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(Json.IngestBatchResultDto, cancellationToken);
    }

    /// <summary>A write link for one highlight snapshot; null when there is no image storage, today's cap is reached, or the server is away.</summary>
    public Task<SnapshotUploadDto?> CreateSnapshotUploadAsync(DateOnly localDay, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsync($"api/snapshots?day={Day(localDay)}", content: null, ct), Json.SnapshotUploadDto, cancellationToken);

    /// <param name="ai">False answers at once from the template; true may wait on the server's model.</param>
    public Task<RecapDto?> GetDayRecapAsync(DateOnly day, string timeZoneId, bool ai, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"{DayRecapPath(day, timeZoneId, pdf: false)}&ai={(ai ? "true" : "false")}", ct), Json.RecapDto, cancellationToken);

    /// <summary>An answer from "Ask PoWatch", or null when the server has no model or it did not answer.</summary>
    public async Task<string?> AskAsync(string question, CancellationToken cancellationToken = default) =>
        (await ReadAsync(ct => httpClient.PostAsJsonAsync("api/ask",
            new AskRequestDto { Question = question, TimeZoneId = TimeZoneInfo.Local.Id }, Json.AskRequestDto, ct), Json.AskAnswerDto, cancellationToken))?.Answer;

    public Task<AiModelsDto?> GetAiModelsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync("api/ai/models", ct), Json.AiModelsDto, cancellationToken);

    public Task<AiModelsDto?> SelectAiModelAsync(string modelId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PutAsJsonAsync("api/ai/models", new SelectAiModelDto(modelId), Json.SelectAiModelDto, ct), Json.AiModelsDto, cancellationToken);

    /// <summary>A watch rule drafted from a sentence, or null when there is no model or the sentence is not a rule.</summary>
    public async Task<RuleDraftDto?> DraftRuleAsync(string text, CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.PostAsJsonAsync("api/ask/rule", new RuleDraftRequestDto { Text = text }, Json.RuleDraftRequestDto, ct), Json.RuleDraftDto, cancellationToken);

    /// <summary>Relays a fired watch rule to every open tab; best effort (offline, the sensing tab already showed it).</summary>
    public Task RaiseAlertAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(ct => httpClient.PostAsJsonAsync("api/alerts", new AlertDto { Text = text }, Json.AlertDto, ct), cancellationToken);

    /// <summary>Every stat family for the query in one JSON file.</summary>
    public static string AllStatsUrl(StatsQuery query) => StatsUrl("all", query);

    public static string ExportUrl(string timeZoneId) => $"api/export.csv?tz={Uri.EscapeDataString(timeZoneId)}";

    public Task<bool> DeleteMyDataAsync(CancellationToken cancellationToken = default) =>
        SendAsync(ct => httpClient.DeleteAsync("api/data", ct), cancellationToken);

    /// <summary>Relative link to a day's recap PDF; the BFF cookie rides along on a plain download.</summary>
    public static string DayRecapPdfUrl(DateOnly day, string timeZoneId) => DayRecapPath(day, timeZoneId, pdf: true);

    public static string SessionRecapPdfUrl(Guid sessionId) => $"api/recaps/session/{sessionId}.pdf";

    private static string DayRecapPath(DateOnly day, string timeZoneId, bool pdf) =>
        $"api/recaps/day/{Day(day)}{(pdf ? ".pdf" : string.Empty)}?tz={Uri.EscapeDataString(timeZoneId)}";

    /// <summary>
    /// Writes the session's chapters that are due (and, once it has ended, its story) and returns them;
    /// null on failure. Can take a while when a model writes the story.
    /// </summary>
    public Task<SessionStoryDto?> UpdateStoryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsync($"api/sessions/{sessionId}/story", content: null, ct), Json.SessionStoryDto, cancellationToken);

    /// <summary>The story of the week (Monday to Sunday) or month holding <paramref name="day"/>; null on failure.</summary>
    public Task<PeriodStoryDto?> GetPeriodStoryAsync(bool month, DateOnly day, string timeZoneId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"api/stories/{(month ? "month" : "week")}?date={Day(day)}&tz={Uri.EscapeDataString(timeZoneId)}", ct), Json.PeriodStoryDto, cancellationToken);

    public async Task<IReadOnlyList<MomentDto>> GetMomentsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync($"api/sessions/{sessionId}/moments", ct), Json.ListMomentDto, cancellationToken) ?? [];

    /// <summary>Null when the list could not be read, so the page can tell "none yet" from "server away".</summary>
    public async Task<IReadOnlyList<RegularDto>?> ListRegularsAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync("api/regulars", ct), Json.ListRegularDto, cancellationToken);

    public Task<ObserveRegularResultDto?> ObserveRegularAsync(ObserveRegularRequestDto request, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsJsonAsync("api/regulars/observe", request, Json.ObserveRegularRequestDto, ct), Json.ObserveRegularResultDto, cancellationToken);

    public async Task<RegularDto?> RenameRegularAsync(string regularId, string? name, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, $"api/regulars/{Uri.EscapeDataString(regularId)}")
        {
            Content = JsonContent.Create(new RenameRegularRequestDto { Name = name }, Json.RenameRegularRequestDto)
        };
        return await ReadAsync(ct => httpClient.SendAsync(message, ct), Json.RegularDto, cancellationToken);
    }

    public Task<RegularDto?> MergeRegularsAsync(string primaryId, string duplicateId, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsJsonAsync("api/regulars/merge",
            new MergeRegularsRequestDto { PrimaryId = primaryId, DuplicateId = duplicateId }, Json.MergeRegularsRequestDto, ct), Json.RegularDto, cancellationToken);

    public Task<PresenceStatsDto?> GetPresenceAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("presence", query, Json.PresenceStatsDto, cancellationToken);

    public Task<SpaceStatsDto?> GetSpaceAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("space", query, Json.SpaceStatsDto, cancellationToken);

    public Task<ObjectStatsDto?> GetObjectsAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("objects", query, Json.ObjectStatsDto, cancellationToken);

    public Task<PatternStatsDto?> GetPatternsAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("patterns", query, Json.PatternStatsDto, cancellationToken);

    public Task<EnvironmentStatsDto?> GetEnvironmentAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("environment", query, Json.EnvironmentStatsDto, cancellationToken);

    public Task<PipelineStatsDto?> GetPipelineAsync(StatsQuery query, CancellationToken cancellationToken = default) =>
        GetStatsAsync("pipeline", query, Json.PipelineStatsDto, cancellationToken);

    /// <summary>A day minute by minute, for the Journal's replay.</summary>
    public Task<ReplayDayDto?> GetReplayAsync(DateOnly day, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"api/stats/replay?date={Day(day)}&tz={Uri.EscapeDataString(TimeZoneInfo.Local.Id)}", ct), Json.ReplayDayDto, cancellationToken);

    private static string StatsUrl(string family, StatsQuery query) =>
        $"api/stats/{family}?range={Uri.EscapeDataString(query.Range)}&tz={Uri.EscapeDataString(query.TimeZoneId)}"
        + (query.SessionId is { } id ? $"&sessionId={id}" : string.Empty)
        + (query.Date is { } date ? $"&date={Day(date)}" : string.Empty);

    private Task<T?> GetStatsAsync<T>(string family, StatsQuery query, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        ReadAsync(ct => httpClient.GetAsync(StatsUrl(family, query), ct), type, cancellationToken);

    public Task<ProfileDto?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync("api/profile", ct), Json.ProfileDto, cancellationToken);

    public Task<ProfileDto?> UpdateProfileAsync(UpdateProfileDto update, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PutAsJsonAsync("api/profile", update, Json.UpdateProfileDto, ct), Json.ProfileDto, cancellationToken);

    public async Task<IReadOnlyList<PlaceDto>> SearchPlacesAsync(string name, CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync($"api/places?q={Uri.EscapeDataString(name)}", ct), Json.ListPlaceDto, cancellationToken) ?? [];

    public Task<WeatherYearDto?> GetWeatherYearAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"api/weather/year?tz={Uri.EscapeDataString(TimeZoneInfo.Local.Id)}", ct), Json.WeatherYearDto, cancellationToken);

    /// <summary>The Monday-to-Sunday week holding <paramref name="day"/>, against the week before.</summary>
    public Task<WeekDigestDto?> GetWeekDigestAsync(DateOnly day, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync($"api/digest/week?date={Day(day)}&tz={Uri.EscapeDataString(TimeZoneInfo.Local.Id)}", ct), Json.WeekDigestDto, cancellationToken);

    public Task<ShareDto?> CreateShareAsync(DateOnly day, int days, CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.PostAsJsonAsync("api/shares",
            new CreateShareDto { Day = day, Days = days, TimeZoneId = TimeZoneInfo.Local.Id }, Json.CreateShareDto, ct), Json.ShareDto, cancellationToken);

    public async Task<IReadOnlyList<ShareDto>?> ListSharesAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(ct => httpClient.GetAsync("api/shares", ct), Json.ListShareDto, cancellationToken);

    public Task<bool> RevokeShareAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(ct => httpClient.DeleteAsync($"api/shares/{Uri.EscapeDataString(token)}", ct), cancellationToken);

    public Task<DiagnosticsSnapshotDto?> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync("api/diagnostics/status", ct), Json.DiagnosticsSnapshotDto, cancellationToken);

    /// <summary>
    /// Reads the same <c>/health</c> document the App Service probe and the CI deploy gate read.
    /// HttpClient does not send an <c>Accept: text/html</c> header, so this always resolves to the
    /// JSON endpoint rather than the Health page's own HTML shell.
    /// </summary>
    public Task<HealthReportDto?> GetHealthAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync("health", ct), Json.HealthReportDto, cancellationToken);

    public Task<AuthConfigDto?> GetAuthConfigAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ct => httpClient.GetAsync("auth/config", ct), Json.AuthConfigDto, cancellationToken);

    public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync(ct => httpClient.PostAsync("auth/logout", content: null, ct), cancellationToken);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Sends and reads the body; null on any failure that is not the caller cancelling.</summary>
    private static async Task<T?> ReadAsync<T>(Func<CancellationToken, Task<HttpResponseMessage>> send, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(type, cancellationToken) : default;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return default;
        }
    }

    /// <summary>Sends without reading a body; true on a success status.</summary>
    private static async Task<bool> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return false;
        }
    }

    // A TaskCanceledException the caller did not ask for is HttpClient's timeout: a failure, not a cancel.
    private static bool IsFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or NotSupportedException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}

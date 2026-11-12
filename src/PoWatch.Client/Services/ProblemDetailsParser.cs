using System.Net.Http.Json;
using System.Text.Json;

namespace PoWatch.Client.Services;

public static class ProblemDetailsParser
{
    public static async Task<string> ParseAsync(HttpResponseMessage response, string fallback, CancellationToken ct = default)
    {
        try
        {
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("application/json" or "application/problem+json"))
                return fallback;

            JsonElement root = await response.Content.ReadFromJsonAsync(PoWatchJsonContext.Default.JsonElement, ct);
            if (root.TryGetProperty("detail", out JsonElement detail) && !string.IsNullOrWhiteSpace(detail.GetString()))
                return detail.GetString()!;
            if (root.TryGetProperty("title", out JsonElement title) && !string.IsNullOrWhiteSpace(title.GetString()))
                return title.GetString()!;
        }
        catch
        {
        }

        return fallback;
    }
}

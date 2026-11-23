using System.Text.Json;
using System.Text.Json.Serialization;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

/// <summary>A watch rule: alert when this class (or "*" for anything) enters between these hours. Kept per phone, as the web keeps it per browser.</summary>
public sealed record WatchRule(string Class, int FromHour, int ToHour)
{
    public const string Any = "*";

    public bool Matches(string label, DateTimeOffset localNow)
    {
        if (Class != Any && !string.Equals(Class, label, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int hour = localNow.Hour;
        return FromHour == ToHour || (FromHour < ToHour ? hour >= FromHour && hour < ToHour : hour >= FromHour || hour < ToHour);
    }

    public override string ToString() =>
        $"{(Class == Any ? "anything" : Class)} · {(FromHour == ToHour ? "any time" : $"{FromHour:00}:00–{ToHour:00}:00")}";
}

/// <summary>Source-generated JSON with the web client's options (camelCase, case-insensitive, numbers as strings allowed).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AuthStateDto))]
[JsonSerializable(typeof(AuthConfigDto))]
[JsonSerializable(typeof(StartSessionRequestDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(IngestBatchDto))]
[JsonSerializable(typeof(IngestBatchResultDto))]
[JsonSerializable(typeof(SnapshotUploadDto))]
[JsonSerializable(typeof(RecapDto))]
[JsonSerializable(typeof(SessionStoryDto))]
[JsonSerializable(typeof(PeriodStoryDto))]
[JsonSerializable(typeof(List<MomentDto>))]
[JsonSerializable(typeof(List<RegularDto>))]
[JsonSerializable(typeof(RegularDto))]
[JsonSerializable(typeof(ObserveRegularRequestDto))]
[JsonSerializable(typeof(ObserveRegularResultDto))]
[JsonSerializable(typeof(AlertDto))]
[JsonSerializable(typeof(AiModelsDto))]
[JsonSerializable(typeof(SelectAiModelDto))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(UpdateProfileDto))]
[JsonSerializable(typeof(List<PlaceDto>))]
[JsonSerializable(typeof(CreateShareDto))]
[JsonSerializable(typeof(ShareDto))]
[JsonSerializable(typeof(List<ShareDto>))]
[JsonSerializable(typeof(WeekDigestDto))]
[JsonSerializable(typeof(PresenceStatsDto))]
[JsonSerializable(typeof(ObjectStatsDto))]
[JsonSerializable(typeof(EnvironmentStatsDto))]
[JsonSerializable(typeof(DiagReportDto))]
[JsonSerializable(typeof(List<WatchRule>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class MobileJsonContext : JsonSerializerContext;

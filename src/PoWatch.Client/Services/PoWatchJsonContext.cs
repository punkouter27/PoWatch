using System.Text.Json.Serialization;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Services;

/// <summary>
/// Source-generated JSON metadata for every type crossing the BFF boundary, so the WASM
/// client serializes without reflection and passes the trim analyzer.
/// Options mirror <see cref="System.Text.Json.JsonSerializerDefaults.Web"/> to match the API.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(DiagnosticsSnapshotDto))]
[JsonSerializable(typeof(AuthStateDto))]
[JsonSerializable(typeof(AuthConfigDto))]
[JsonSerializable(typeof(HealthReportDto))]
[JsonSerializable(typeof(StartSessionRequestDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(SessionStoryDto))]
[JsonSerializable(typeof(IngestBatchDto))]
[JsonSerializable(typeof(IngestBatchResultDto))]
[JsonSerializable(typeof(PixelSamplePayload))]
[JsonSerializable(typeof(List<OutboxEntryPayload>))]
[JsonSerializable(typeof(PresenceStatsDto))]
[JsonSerializable(typeof(SpaceStatsDto))]
[JsonSerializable(typeof(ObjectStatsDto))]
[JsonSerializable(typeof(PatternStatsDto))]
[JsonSerializable(typeof(EnvironmentStatsDto))]
[JsonSerializable(typeof(PipelineStatsDto))]
[JsonSerializable(typeof(StatsChangedDto))]
[JsonSerializable(typeof(SnapshotUploadDto))]
[JsonSerializable(typeof(RecapDto))]
[JsonSerializable(typeof(RegularDto))]
[JsonSerializable(typeof(List<RegularDto>))]
[JsonSerializable(typeof(ObserveRegularRequestDto))]
[JsonSerializable(typeof(ObserveRegularResultDto))]
[JsonSerializable(typeof(RenameRegularRequestDto))]
[JsonSerializable(typeof(MergeRegularsRequestDto))]
[JsonSerializable(typeof(List<MomentDto>))]
[JsonSerializable(typeof(FxFramePayload))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(AskRequestDto))]
[JsonSerializable(typeof(AskAnswerDto))]
[JsonSerializable(typeof(AiModelsDto))]
[JsonSerializable(typeof(SelectAiModelDto))]
[JsonSerializable(typeof(InferenceStatusPayload))]
[JsonSerializable(typeof(RuleDraftRequestDto))]
[JsonSerializable(typeof(RuleDraftDto))]
[JsonSerializable(typeof(AlertDto))]
[JsonSerializable(typeof(List<WatchRule>))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(UpdateProfileDto))]
[JsonSerializable(typeof(List<PlaceDto>))]
[JsonSerializable(typeof(WeatherYearDto))]
[JsonSerializable(typeof(WeekDigestDto))]
[JsonSerializable(typeof(CreateShareDto))]
[JsonSerializable(typeof(ShareDto))]
[JsonSerializable(typeof(List<ShareDto>))]
[JsonSerializable(typeof(ReplayDayDto))]
internal sealed partial class PoWatchJsonContext : JsonSerializerContext;

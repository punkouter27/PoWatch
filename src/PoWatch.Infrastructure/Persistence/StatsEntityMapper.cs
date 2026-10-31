using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Azure.Data.Tables;
using PoWatch.Domain.Models;

namespace PoWatch.Infrastructure.Persistence;

public static class SensingKeys
{
    /// <summary>Time-ordered and unique per session: two sessions can tick in the same instant.</summary>
    public static string TickRowKey(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return $"{tick.StartUtc.UtcTicks:D19}-{tick.SessionId:N}";
    }
}

/// <summary>
/// Maps stat-cam models to and from Table Storage entities. Scalars become columns; grids are
/// packed as float bytes (576 bytes for 16×9) and small maps as JSON strings.
/// </summary>
internal static class StatsEntityMapper
{
    /// <summary>Table keys may not contain / \ # ? — escape the user id so any identity provider's id is safe.</summary>
    public static string UserKey(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return Uri.EscapeDataString(userId);
    }

    public static string DayPartition(string userId, DateOnly localDay) =>
        $"{UserKey(userId)}|{localDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static string Ticks(DateTimeOffset instant) =>
        instant.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);

    public static TableEntity ToEntity(string partitionKey, Tick tick) => new(partitionKey, SensingKeys.TickRowKey(tick))
    {
        ["SessionId"] = tick.SessionId,
        ["StartUtc"] = tick.StartUtc,
        ["DurationSeconds"] = tick.DurationSeconds,
        ["PixelSamples"] = tick.PixelSamples,
        ["DetectorSamples"] = tick.DetectorSamples,
        ["MotionMean"] = tick.MotionMean,
        ["MotionMax"] = tick.MotionMax,
        ["LuminanceMean"] = tick.LuminanceMean,
        ["SoundMean"] = tick.SoundMean,
        ["SoundMax"] = tick.SoundMax,
        ["Palette"] = JsonSerializer.Serialize(tick.Palette),
        ["MotionGrid"] = Pack(tick.MotionGrid.ToArray()),
        ["PresenceGrid"] = Pack(tick.PresenceGrid.ToArray()),
        ["Classes"] = JsonSerializer.Serialize(tick.Classes),
        ["ActiveTrackIds"] = JsonSerializer.Serialize(tick.ActiveTrackIds)
    };

    public static Tick ToTick(TableEntity e) => new()
    {
        SessionId = e.GetGuid("SessionId")!.Value,
        StartUtc = e.GetDateTimeOffset("StartUtc")!.Value,
        DurationSeconds = e.GetDouble("DurationSeconds") ?? Tick.MaxDurationSeconds,
        PixelSamples = e.GetInt32("PixelSamples") ?? 0,
        DetectorSamples = e.GetInt32("DetectorSamples") ?? 0,
        MotionMean = e.GetDouble("MotionMean") ?? 0,
        MotionMax = e.GetDouble("MotionMax") ?? 0,
        LuminanceMean = e.GetDouble("LuminanceMean") ?? 0,
        SoundMean = e.GetDouble("SoundMean") ?? 0,
        SoundMax = e.GetDouble("SoundMax") ?? 0,
        Palette = Json<List<int>>(e, "Palette") ?? [],
        MotionGrid = Unpack(e.GetBinary("MotionGrid")),
        PresenceGrid = Unpack(e.GetBinary("PresenceGrid")),
        Classes = Json<Dictionary<string, ClassCount>>(e, "Classes") ?? [],
        ActiveTrackIds = Json<List<string>>(e, "ActiveTrackIds") ?? []
    };

    public static TableEntity ToEntity(string partitionKey, SceneEvent sceneEvent)
    {
        var entity = new TableEntity(partitionKey, $"{Ticks(sceneEvent.AtUtc)}-{sceneEvent.StableId():N}")
        {
            ["SessionId"] = sceneEvent.SessionId,
            ["AtUtc"] = sceneEvent.AtUtc,
            ["Kind"] = (int)sceneEvent.Kind,
            ["Edge"] = (int)sceneEvent.Edge
        };
        if (sceneEvent.TrackId is not null) entity["TrackId"] = sceneEvent.TrackId;
        if (sceneEvent.Class is not null) entity["Class"] = sceneEvent.Class;
        if (sceneEvent.Text is not null) entity["Text"] = sceneEvent.Text;
        if (sceneEvent.Score is { } score) entity["Score"] = score;
        if (sceneEvent.DwellSeconds is { } dwell) entity["DwellSeconds"] = dwell;
        if (sceneEvent.ImagePath is not null) entity["ImagePath"] = sceneEvent.ImagePath;
        if (sceneEvent.RegularId is not null) entity["RegularId"] = sceneEvent.RegularId;
        return entity;
    }

    public static SceneEvent ToSceneEvent(TableEntity e) => new()
    {
        SessionId = e.GetGuid("SessionId")!.Value,
        AtUtc = e.GetDateTimeOffset("AtUtc")!.Value,
        Kind = (SceneEventKind)(e.GetInt32("Kind") ?? 0),
        Edge = (FrameEdge)(e.GetInt32("Edge") ?? 0),
        TrackId = e.GetString("TrackId"),
        Class = e.GetString("Class"),
        Text = e.GetString("Text"),
        Score = e.GetDouble("Score"),
        DwellSeconds = e.GetDouble("DwellSeconds"),
        ImagePath = e.GetString("ImagePath"),
        RegularId = e.GetString("RegularId")
    };

    public static TableEntity ToEntity(Session session)
    {
        var entity = new TableEntity(UserKey(session.UserId), session.Id.ToString("N"))
        {
            ["UserId"] = session.UserId,
            ["StartedUtc"] = session.StartedUtc,
            ["LastActivityUtc"] = session.LastActivityUtc,
            ["TimeZoneId"] = session.TimeZoneId
        };
        if (session.EndedUtc is { } ended) entity["EndedUtc"] = ended;
        return entity;
    }

    public static Session ToSession(TableEntity e)
    {
        var startedUtc = e.GetDateTimeOffset("StartedUtc")!.Value;
        return new Session
        {
            Id = Guid.ParseExact(e.RowKey, "N"),
            UserId = e.GetString("UserId"),
            StartedUtc = startedUtc,
            EndedUtc = e.GetDateTimeOffset("EndedUtc"),
            TimeZoneId = e.GetString("TimeZoneId"),
            LastActivityUtc = e.GetDateTimeOffset("LastActivityUtc") ?? startedUtc
        };
    }

    public static byte[] Pack(ReadOnlySpan<float> values) => MemoryMarshal.AsBytes(values).ToArray();

    public static float[] Unpack(byte[]? data) =>
        data is null ? [] : MemoryMarshal.Cast<byte, float>(data).ToArray();

    public static T? Json<T>(TableEntity e, string column) =>
        e.GetString(column) is { Length: > 0 } json ? JsonSerializer.Deserialize<T>(json) : default;
}

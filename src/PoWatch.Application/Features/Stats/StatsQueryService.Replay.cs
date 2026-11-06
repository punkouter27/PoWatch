using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Sessions;

namespace PoWatch.Application.Features.Stats;

/// <summary>The raw record and the minute-by-minute replay of a short window.</summary>
public sealed partial class StatsQueryService
{
    /// <summary>
    /// The raw record behind a short window (session, today or one day): every 10 s tick and every scene
    /// event, as stored. Null for longer windows, where the rollups are the record worth reading.
    /// </summary>
    public async Task<(List<Tick> Ticks, List<SceneEvent> Events)?> RawAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        if (!window.IsShort) return null;

        var ticks = new List<Tick>();
        foreach (var day in DaysOf(window))
            ticks.AddRange((await sensingLog.GetTicksAsync(userId, day, cancellationToken)).Where(t => window.Contains(t.StartUtc)));

        return (ticks, await EventsAsync(userId, window, cancellationToken));
    }

    /// <summary>
    /// A day minute by minute for the History replay: every observed minute with its motion grid,
    /// and the captions, moments, entrances and exits in between.
    /// </summary>
    public async Task<ReplayDayDto> ReplayAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var minutesRead = rollups.GetRangeAsync(userId, RollupGrain.Minute, window.FromUtc, window.ToUtc, cancellationToken);
        var eventsRead = EventsAsync(userId, window, cancellationToken);
        var regularsRead = regulars.ListAsync(userId, cancellationToken);
        await Task.WhenAll(minutesRead, eventsRead, regularsRead);
        var names = (await regularsRead).ToDictionary(r => r.Id, r => r.DisplayName);

        var minutes = (await minutesRead).Where(m => m.Ticks > 0).OrderBy(m => m.BucketStartUtc).ToList();
        var grids = new byte[minutes.Count * SpatialGrid.Cells];
        for (var i = 0; i < minutes.Count; i++)
            SpatialGrid.Quantize(minutes[i].Grid.Span, grids.AsSpan(i * SpatialGrid.Cells, SpatialGrid.Cells));

        return new ReplayDayDto
        {
            Window = window.ToDto(),
            Minutes = minutes.Select(m => new ReplayMinuteDto
            {
                AtUtc = m.BucketStartUtc,
                Occupancy = m.OccupancyFraction,
                Peak = m.PeakConcurrency,
                Motion = m.Motion.Mean,
                Luminance = m.Luminance.Mean
            }).ToList(),
            Grids = Convert.ToBase64String(grids),
            Events = ReplayEvents(await eventsRead, names)
        };
    }

    /// <summary>
    /// The events as replay lines, in time order. Recognition lands a moment after a track comes in, so its
    /// entry is usually posted without a regular: it takes the one the same track was recognised as later.
    /// </summary>
    private static List<ReplayEventDto> ReplayEvents(IReadOnlyList<SceneEvent> events, Dictionary<string, string> names)
    {
        var regularOfTrack = events
            .Where(e => e.TrackId is not null && e.RegularId is not null)
            .GroupBy(e => (e.SessionId, e.TrackId))
            .ToDictionary(g => g.Key, g => g.First().RegularId!);
        return events.OrderBy(e => e.AtUtc)
            .Select(e => ReplayEvent(e, e.RegularId ?? (e.TrackId is null ? null : regularOfTrack.GetValueOrDefault((e.SessionId, e.TrackId))), names))
            .OfType<ReplayEventDto>()
            .ToList();
    }

    private static ReplayEventDto? ReplayEvent(SceneEvent e, string? regularId, Dictionary<string, string> names)
    {
        var who = regularId is { } id ? names.GetValueOrDefault(id) : null;
        return e.Kind switch
        {
            SceneEventKind.Caption when e.Text is not null => new() { AtUtc = e.AtUtc, Kind = "caption", Text = e.Text },
            SceneEventKind.Speech when e.Text is not null => new() { AtUtc = e.AtUtc, Kind = "speech", Text = e.Text, Who = who },
            SceneEventKind.Sound when e.Text is not null => new() { AtUtc = e.AtUtc, Kind = "sound", Text = e.Text },
            SceneEventKind.Notable => new() { AtUtc = e.AtUtc, Kind = "moment", Text = e.Text },
            SceneEventKind.TrackEnter when e.Class is { } enterClass && EntityClasses.IsPresence(enterClass) => new() { AtUtc = e.AtUtc, Kind = "enter", Class = e.Class, Who = who },
            SceneEventKind.TrackExit when e.Class is { } exitClass && EntityClasses.IsPresence(exitClass) => new() { AtUtc = e.AtUtc, Kind = "exit", Class = e.Class, Who = who },
            _ => null
        };
    }
}

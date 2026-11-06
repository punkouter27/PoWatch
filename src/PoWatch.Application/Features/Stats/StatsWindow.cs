using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Sessions;

namespace PoWatch.Application.Features.Stats;

public enum StatsRange
{
    Session,
    Today,
    Week,
    Month,
    All,
    Day
}

/// <summary>A resolved stats window: the instants it covers and the coarsest rollup grain that answers it.</summary>
public sealed record StatsWindow(StatsRange Range, DateTimeOffset FromUtc, DateTimeOffset ToUtc, RollupGrain Grain, TimeZoneInfo Zone)
{
    public TimeSpan GrainLength => Grain switch
    {
        RollupGrain.Minute => TimeSpan.FromMinutes(1),
        RollupGrain.Hour => TimeSpan.FromHours(1),
        _ => TimeSpan.FromDays(1)
    };

    /// <summary>Session, today or one day: short enough that the raw ticks and events are worth reading.</summary>
    public bool IsShort => Range is StatsRange.Today or StatsRange.Session or StatsRange.Day;

    public bool Contains(DateTimeOffset instant) => instant >= FromUtc && instant < ToUtc;

    public StatsWindowDto ToDto() => new()
    {
        Range = StatsQueryService.RangeName(Range),
        FromUtc = FromUtc,
        ToUtc = ToUtc,
        Grain = Grain.ToString(),
        TimeZoneId = Zone.Id
    };
}

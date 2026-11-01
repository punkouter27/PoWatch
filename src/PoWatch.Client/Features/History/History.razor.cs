using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Features.History;

/// <summary>The History page's state and behaviour; the markup is in History.razor.</summary>
public sealed partial class History
{
    private const string DayFormat = "yyyy-MM-dd";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The Sunday the 53-week calendar starts on; cell index = weekday × 53 + week.</summary>
    private static DateOnly CalendarStart => Today.AddDays(-(52 * 7) - (int)Today.DayOfWeek);

    private DateOnly _day = Today;
    private DateOnly? _shown;
    private int _tab;
    private const string YearViewKey = "powatch:year-view";
    private bool _year3d;
    private IReadOnlyList<double> _skyValues = [];
    private IReadOnlyList<bool> _skyWet = [];
    private IReadOnlyList<string> _skyLabels = [];
    private bool _loaded;
    private bool _rewriting;
    private PresenceStatsDto? _presence;
    private RecapDto? _recap;
    private EnvironmentStatsDto? _environment;
    private PatternStatsDto? _patterns;
    private IReadOnlyList<SessionDto> _sessions = [];
    private List<double> _calendarCells = [];
    private WeatherYearDto? _weather;
    private Dictionary<DateOnly, WeatherDayDto> _weatherByDay = [];
    private WeekDigestDto? _week;
    private DateOnly? _weekShown;

    /// <summary>The day in the address, yyyy-MM-dd; today when absent, unreadable or in the future.</summary>
    [Parameter] public string? Day { get; set; }

    /// <summary>?tab=replay&amp;at={unix ms}: a caption search result, opened on its minute in the replay.</summary>
    [SupplyParameterFromQuery(Name = "tab")] public string? Tab { get; set; }
    [SupplyParameterFromQuery(Name = "at")] public long? At { get; set; }

    private long? _replayAt;
    private (string?, long?) _appliedQuery;

    protected override async Task OnInitializedAsync()
    {
        Prefs.Changed += OnPrefsChanged;
        var weather = Api.GetWeatherYearAsync();
        _patterns = await Api.GetPatternsAsync(StatsQuery.For("today"));
        BuildCalendar();
        _weather = await weather;
        _weatherByDay = _weather?.Days.ToDictionary(d => d.Date) ?? [];
        BuildSkyline();
        _year3d = await JS.TryInvokeAsync<string>("localStorage.getItem", YearViewKey) == "3d";
    }

    private WeatherDayDto? WeatherOn(DateOnly day) => _weatherByDay.GetValueOrDefault(day);

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>"+6 pts on the week before", or "no week before" when there is nothing to compare with.</summary>
    private string Change(double now, double before)
    {
        if (_week is not { LastWeek.SecondsObserved: > 0 }) return "no week before";
        var points = Math.Round((now - before) * 100);
        return points == 0 ? "same as the week before" : $"{(points > 0 ? "+" : "−")}{Math.Abs(points)} pts on the week before";
    }

    private void OnPrefsChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Prefs.Changed -= OnPrefsChanged;

    protected override async Task OnParametersSetAsync()
    {
        if (Day is null)
        {
            _day = Today;
        }
        else if (!DateOnly.TryParseExact(Day, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > Today)
        {
            Navigation.NavigateTo(DayPath(Today), replace: true);
            return;
        }
        else
        {
            _day = day;
        }
        // Once per distinct link, so choosing another tab afterwards is not undone by a re-render.
        if (Tab == "replay" && (Tab, At) != _appliedQuery) (_tab, _replayAt, _appliedQuery) = (1, At, (Tab, At));
        if (_day == _shown) return;
        _shown = _day;
        await LoadDayAsync();
    }

    private void Move(int days) => Go(_day.AddDays(days) > Today ? Today : _day.AddDays(days));

    // Every day, today included, has its own address (/days opens on today).
    private void Go(DateOnly day) => Navigation.NavigateTo(DayPath(day));

    private static string DayPath(DateOnly day) => StoryLinks.Day(day);

    private static DateOnly? DayFor(int index)
    {
        var day = CalendarStart.AddDays(((index % 53) * 7) + (index / 53));
        return day > Today ? null : day;
    }

    private static int CellFor(DateOnly day)
    {
        var offset = day.DayNumber - CalendarStart.DayNumber;
        return offset < 0 || offset >= 53 * 7 ? -1 : ((int)day.DayOfWeek * 53) + (offset / 7);
    }

    private async Task LoadDayAsync()
    {
        var day = _day;
        _loaded = false;
        if (_weekShown != WeekStart(day)) (_week, _weekShown) = (null, WeekStart(day));
        var week = _week is null ? Api.GetWeekDigestAsync(day) : Task.FromResult<WeekDigestDto?>(_week);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
        var query = StatsQuery.ForDay(day);
        var presence = Api.GetPresenceAsync(query);
        var environment = Api.GetEnvironmentAsync(query);
        var sessions = Api.ListSessionsBetweenAsync(start, start.AddDays(1));
        // The template recap answers at once; a model's rewording replaces it when (if) it arrives.
        var recap = Api.GetDayRecapAsync(day, TimeZoneInfo.Local.Id, ai: false);
        await Task.WhenAll(presence, environment, sessions, recap, week);
        if (day != _day) return; // A newer ◀/▶ press owns the panels now.

        (_presence, _environment, _recap, _sessions, _week) = (presence.Result, environment.Result, recap.Result, sessions.Result, week.Result);
        _loaded = true;
        StateHasChanged();

        await RewordRecapAsync(day);
    }

    /// <summary>
    /// Asks the server's model for its version of the day's recap; where the server has none, the
    /// browser's built-in model gets the same prompt. Either answer must keep to the template's numbers.
    /// </summary>
    private async Task RewordRecapAsync(DateOnly day)
    {
        if (_recap is not { } template || _presence is not { SecondsObserved: > 0 }) return;
        _rewriting = true;
        StateHasChanged();

        var reworded = await Api.GetDayRecapAsync(day, TimeZoneInfo.Local.Id, ai: true);
        if (reworded is null || reworded.Source == template.Source)
        {
            var prompt = RecapPrompt.User(template);
            var text = await JS.TryInvokeAsync<string>("powatchLocalAi.rewrite", RecapPrompt.System + " Reply with the summary paragraph only.", prompt);
            reworded = !string.IsNullOrWhiteSpace(text) && RecapPrompt.KeepsToFacts(text, prompt)
                ? RecapPrompt.Rewritten(template, text, "this browser's model")
                : null;
        }

        _rewriting = false;
        if (day == _day && reworded is not null) _recap = reworded;
        StateHasChanged();
    }

    private string CellLabel(int index, double value) => DayFor(index) is { } d
        ? $"{d:ddd dd MMM} · {(value > 0 ? value.ToString("P0", CultureInfo.CurrentCulture) : "no data")}{(WeatherOn(d) is { } w ? $" · {w.Summary}" : string.Empty)}"
        : string.Empty;

    /// <summary>The calendar as the skyline wants it: −1 marks days still to come, so they get no tower.</summary>
    private void BuildSkyline()
    {
        var cells = Enumerable.Range(0, _calendarCells.Count).ToList();
        _skyValues = cells.Select(i => DayFor(i) is null ? -1 : _calendarCells[i]).ToList();
        _skyWet = cells.Select(i => DayFor(i) is { } d && WeatherOn(d) is { Wet: true }).ToList();
        _skyLabels = cells.Select(i => CellLabel(i, _calendarCells[i])).ToList();
    }

    private async Task SetYearViewAsync(bool threeD)
    {
        _year3d = threeD;
        await JS.TryInvokeVoidAsync("localStorage.setItem", YearViewKey, threeD ? "3d" : "2d");
    }

    private void BuildCalendar()
    {
        var cells = new double[53 * 7];
        foreach (var day in _patterns?.Calendar ?? [])
        {
            if (CellFor(day.Date) is var index and >= 0) cells[index] = Math.Max(day.Occupancy, 0.0001);
        }
        _calendarCells = [.. cells];
    }
}

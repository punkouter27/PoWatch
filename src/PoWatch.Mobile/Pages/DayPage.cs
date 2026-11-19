using System.Globalization;
using PoWatch.Mobile.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Pages;

/// <summary>One day: the recap (numbers, highlights, moments with snapshots), presence, objects, regulars, light — plus share and PDF.</summary>
public sealed class DayPage : ContentPage, IQueryAttributable
{
    private readonly ApiClient _api;
    private readonly MobileConfig _config;
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private bool _ai;

    public DayPage(ApiClient api, MobileConfig config)
    {
        _api = api;
        _config = config;
        Title = "Day";
        Content = new ScrollView { Content = _body };
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("date", out object? raw) && DateOnly.TryParseExact(raw?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            _day = d;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _body.Clear();
        Title = _day.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
        _body.Add(new ActivityIndicator { IsRunning = true });
        Task<RecapDto?> recapTask = _api.GetDayRecapAsync(_day, _ai);
        Task<PresenceStatsDto?> presenceTask = _api.GetPresenceAsync("day", date: _day);
        Task<ObjectStatsDto?> objectsTask = _api.GetObjectsAsync("day", date: _day);
        Task<EnvironmentStatsDto?> environmentTask = _api.GetEnvironmentAsync("day", date: _day);
        await Task.WhenAll(recapTask, presenceTask, objectsTask, environmentTask);
        _body.Clear();

        if (await recapTask is { } recap)
        {
            VerticalStackLayout box = Ui.Stack(6, Ui.H1(recap.Title), Ui.Muted(recap.Subtitle), Ui.Text(recap.Summary));
            foreach (string highlight in recap.Highlights)
            {
                box.Add(Ui.Text($"• {highlight}"));
            }

            Grid numbers = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 4 };
            for (int i = 0; i < recap.Numbers.Count; i++)
            {
                numbers.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                numbers.Add(Ui.Muted(recap.Numbers[i].Label, 14), 0, i);
                numbers.Add(Ui.Text(recap.Numbers[i].Value, 14), 1, i);
            }

            box.Add(numbers);
            box.Add(Ui.Field($"AI wording ({recap.Source})", Ui.Toggle(_ai, async on => { _ai = on; await LoadAsync(); })));
            _body.Add(Ui.Card(box));
            AddMoments(_body, recap.Moments, _api);
        }
        else
        {
            _body.Add(Ui.Muted("No recap for this day."));
        }

        if (await presenceTask is { } p)
        {
            _body.Add(Ui.Card(Ui.Stack(4,
                Ui.H2("Presence"),
                Ui.Text($"Occupied {p.Occupancy:P0} of {TimeSpan.FromSeconds(p.SecondsObserved).TotalHours:F1} h · {p.Visits} visits ({p.VisitsPerHour:F1}/h) · up to {p.PeakConcurrency} at once"),
                Ui.Muted($"Typical stay {Dur(p.DwellP50Seconds)} · long stays {Dur(p.DwellP90Seconds)} · longest empty {Dur(p.LongestEmptySeconds)}{(p.BusiestStartUtc is { } b ? $" · busiest {b.LocalDateTime:t}" : string.Empty)}"))));
        }

        if (await objectsTask is { } o && o.Classes.Count > 0)
        {
            VerticalStackLayout box = Ui.Stack(4, Ui.H2("What was seen"));
            foreach (ClassStatDto c in o.Classes.OrderByDescending(c => c.Share).Take(8))
            {
                box.Add(Ui.Field(c.Name, Ui.Text($"{c.Share:P0} of the time · up to {c.Peak}", 14)));
            }

            foreach (RegularStatDto r in o.Regulars.Take(5))
            {
                box.Add(Ui.Muted($"{r.DisplayName} ({r.Class}): {r.Visits} visits, {Dur(r.DwellSeconds)}"));
            }

            _body.Add(Ui.Card(box));
        }

        if (await environmentTask is { } env)
        {
            string switches = string.Join(", ", env.Switches.Select(s => $"{(s.On ? "on" : "off")} {s.AtUtc.LocalDateTime:t}"));
            _body.Add(Ui.Card(Ui.Stack(4,
                Ui.H2("Light & words"),
                Ui.Muted(switches.Length > 0 ? $"Lights: {switches}" : "No light switches noticed."),
                Ui.Muted(env.WeirdestCaption is { } w ? $"Weirdest caption: \"{w}\"" : $"{env.CaptionCount} captions"))));
        }

        _body.Add(Ui.Row(
            Ui.Secondary("Share link", ShareDayAsync),
            Ui.Secondary("PDF", () => DownloadAndShareAsync(ApiClient.DayPdfPath(_day), "application/pdf", $"powatch-{_day:yyyy-MM-dd}.pdf"))));
    }

    internal static void AddMoments(VerticalStackLayout body, IReadOnlyList<MomentDto> moments, ApiClient api)
    {
        if (moments.Count == 0)
        {
            return;
        }

        body.Add(Ui.H2("Moments"));
        foreach (MomentDto m in moments)
        {
            VerticalStackLayout card = Ui.Stack(4, Ui.Text(m.Text), Ui.Muted($"{m.AtUtc.LocalDateTime:t} · {m.Score:F2}"));
            if (m.ImageUrl is { Length: > 0 } url)
            {
                card.Insert(0, new Image { Source = ImageSource.FromUri(url.StartsWith("http", StringComparison.Ordinal) ? new Uri(url) : api.U(url)), HeightRequest = 180, Aspect = Aspect.AspectFill });
            }

            body.Add(Ui.Card(card));
        }
    }

    private static string Dur(double? seconds) => seconds is not { } s ? "—" : s < 90 ? $"{s:F0} s" : s < 5400 ? $"{s / 60:F0} min" : $"{s / 3600:F1} h";

    private async Task ShareDayAsync()
    {
        if (await _api.CreateShareAsync(_day, 7) is not { } share)
        {
            await Ui.Toast("Couldn't create a share link.");
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest { Title = "PoWatch day", Uri = _api.U(share.Path).ToString(), Text = $"My room on {_day:MMM d} (link expires {share.ExpiresUtc.LocalDateTime:MMM d})" });
    }

    internal async Task DownloadAndShareAsync(string path, string type, string name)
    {
        try
        {
            using HttpResponseMessage resp = await _api.DownloadAsync(path);
            if (!resp.IsSuccessStatusCode)
            {
                await Ui.Toast(resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? "The server can't make PDFs right now." : "Download failed.");
                return;
            }

            string file = Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
            await Share.Default.RequestAsync(new ShareFileRequest(name, new ShareFile(file, type)));
        }
        catch (HttpRequestException)
        {
            await Ui.Toast($"Can't reach {_config.BaseUrl}.");
        }
    }
}

/// <summary>One session: its story (chapters), moments, presence and objects.</summary>
public sealed class SessionPage(ApiClient api) : ContentPage, IQueryAttributable
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private Guid _id;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out object? raw) && Guid.TryParse(raw?.ToString(), out Guid id))
        {
            _id = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = SessionDto.Tag(_id);
        Content = new ScrollView { Content = _body };
        _body.Clear();
        _body.Add(new ActivityIndicator { IsRunning = true });
        Task<SessionStoryDto?> storyTask = api.UpdateStoryAsync(_id);
        Task<IReadOnlyList<MomentDto>> momentsTask = api.GetMomentsAsync(_id);
        Task<PresenceStatsDto?> presenceTask = api.GetPresenceAsync("session", _id);
        Task<ObjectStatsDto?> objectsTask = api.GetObjectsAsync("session", _id);
        await Task.WhenAll(storyTask, momentsTask, presenceTask, objectsTask);
        _body.Clear();
        _body.Add(Ui.H1($"Session {SessionDto.Tag(_id)}"));

        if (await storyTask is { } story)
        {
            VerticalStackLayout box = Ui.Stack(6, Ui.H2("Story"));
            if (story.Story is { Length: > 0 } text)
            {
                box.Add(Ui.Text(text));
            }

            foreach (StoryChapterDto chapter in story.Chapters)
            {
                box.Add(Ui.Muted($"{chapter.FromUtc.LocalDateTime:t}–{chapter.ToUtc.LocalDateTime:t}"));
                box.Add(Ui.Text(chapter.Text));
            }

            _body.Add(Ui.Card(box));
        }

        if (await presenceTask is { } p)
        {
            _body.Add(Ui.Card(Ui.Stack(4, Ui.H2("Presence"), Ui.Text($"Occupied {p.Occupancy:P0} · {p.Visits} visits · up to {p.PeakConcurrency} at once · {TimeSpan.FromSeconds(p.SecondsObserved).TotalMinutes:F0} min observed"))));
        }

        if (await objectsTask is { } o && o.Classes.Count > 0)
        {
            _body.Add(Ui.Card(Ui.Stack(4, Ui.H2("Seen"), Ui.Text(string.Join(" · ", o.Classes.OrderByDescending(c => c.Share).Take(8).Select(c => $"{c.Name} {c.Share:P0}"))))));
        }

        DayPage.AddMoments(_body, await momentsTask, api);
    }
}

using PoWatch.Mobile.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Pages;

/// <summary>Journal: this week in a paragraph, Ask, and every observed day with its sessions.</summary>
public sealed class JournalPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };

    public JournalPage(ApiClient api)
    {
        _api = api;
        Title = "Journal";
        RefreshView refresh = new() { Content = new ScrollView { Content = _body } };
        refresh.Command = new Command(async () => { await LoadAsync(); refresh.IsRefreshing = false; });
        Content = refresh;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _body.Clear();
        _body.Add(Ui.H1("Journal"));
        if (!ApiClient.Online)
        {
            _body.Add(Ui.Colored("You're offline — the journal lives on the server. Observing still works and sends later.", Ui.Warn));
            return;
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        Task<WeekDigestDto?> digestTask = _api.GetWeekDigestAsync(today);
        Task<IReadOnlyList<SessionDto>> sessionsTask = _api.ListSessionsAsync(100);

        if (await digestTask is { } digest)
        {
            WeekNumbersDto w = digest.ThisWeek;
            _body.Add(Ui.Card(Ui.Stack(4,
                Ui.H2(digest.Headline ?? "This week"),
                Ui.Text(digest.Summary),
                Ui.Muted($"{w.DaysObserved} days · {TimeSpan.FromSeconds(w.SecondsObserved).TotalHours:F1} h observed · {w.Visits} visits · occupied {w.Occupancy:P0}{(digest.NewRegulars.Count > 0 ? $" · new: {string.Join(", ", digest.NewRegulars)}" : string.Empty)}"))));
        }

        Entry question = new() { Placeholder = "Ask about your room (e.g. when is it busiest?)", MaxLength = AskRequestDto.MaxLength };
        Label answer = Ui.Text(null);
        _body.Add(Ui.Card(Ui.Stack(6, Ui.H2("Ask"), question, Ui.Secondary("Ask", async () =>
        {
            if (!string.IsNullOrWhiteSpace(question.Text))
            {
                answer.Text = "Thinking…";
                answer.Text = await _api.AskAsync(question.Text.Trim()) ?? "No answer — the AI may be off on this server.";
            }
        }), answer)));

        IReadOnlyList<SessionDto> sessions = await sessionsTask;
        if (sessions.Count == 0)
        {
            _body.Add(Ui.Muted("Nothing observed yet. Start on the Live tab."));
            return;
        }

        foreach (IGrouping<DateOnly, SessionDto> day in sessions.GroupBy(s => DateOnly.FromDateTime(s.StartedUtc.LocalDateTime)).OrderByDescending(g => g.Key))
        {
            double hours = day.Sum(s => s.DurationSeconds) / 3600;
            VerticalStackLayout card = Ui.Stack(4,
                new Label { Text = day.Key == today ? "Today" : day.Key.ToString("dddd, MMM d", System.Globalization.CultureInfo.CurrentCulture), FontAttributes = FontAttributes.Bold, FontSize = 16 },
                Ui.Muted($"{day.Count()} session(s) · {hours:F1} h"));
            foreach (SessionDto s in day.OrderBy(s => s.StartedUtc))
            {
                Label row = new() { Text = $"{SessionDto.Tag(s.Id)} · {s.StartedUtc.LocalDateTime:t}{(s.IsRunning ? " · running" : $" – {s.EndedUtc?.LocalDateTime:t}")}", TextColor = Ui.Accent, Padding = new Thickness(0, 4) };
                TapGestureRecognizer tap = new();
                tap.Tapped += async (_, _) => await Shell.Current.GoToAsync($"session?id={s.Id}");
                row.GestureRecognizers.Add(tap);
                card.Add(row);
            }

            DateOnly date = day.Key;
            _body.Add(Ui.Card(card, () => Shell.Current.GoToAsync($"day?date={date:yyyy-MM-dd}")));
        }
    }
}

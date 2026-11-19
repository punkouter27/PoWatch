using PoWatch.Mobile.Services;
using PoWatch.Mobile.Services.Sensing;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Pages;

/// <summary>Settings: account, profile, regulars, watch rules, shares, AI, data, server.</summary>
public sealed class SettingsPage : ContentPage
{
    private static readonly string[] RuleClasses = [WatchRule.Any, "person", "cat", "dog", "bird", "car", "bicycle"];

    private readonly ApiClient _api;
    private readonly Session _session;
    private readonly MobileConfig _config;
    private readonly EntraAuthService _entra;
    private readonly CookieJar _cookies;
    private readonly SensingEngine _engine;
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = new Thickness(16, 8, 16, 32) };

    public SettingsPage(ApiClient api, Session session, MobileConfig config, EntraAuthService entra, CookieJar cookies, SensingEngine engine)
    {
        _api = api;
        _session = session;
        _config = config;
        _entra = entra;
        _cookies = cookies;
        _engine = engine;
        Title = "Settings";
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        _body.Clear();
        _body.Add(Ui.H1("Settings"));
        bool signedIn = await _session.RefreshAsync();

        VerticalStackLayout account = Ui.Stack(6, Ui.H2("Account"));
        if (signedIn)
        {
            account.Add(Ui.Text(_session.Me?.Email ?? _session.Me?.Name ?? "Signed in"));
            account.Add(Ui.Secondary("Sign out", SignOutAsync));
        }
        else
        {
            account.Add(Ui.Primary("Sign in", () => Shell.Current.GoToAsync("login")));
        }

        _body.Add(Ui.Card(account));
        if (signedIn && ApiClient.Online)
        {
            Task<ProfileDto?> profile = _api.GetProfileAsync();
            Task<List<RegularDto>?> regulars = _api.ListRegularsAsync();
            Task<List<ShareDto>?> shares = _api.ListSharesAsync();
            Task<AiModelsDto?> models = _api.GetAiModelsAsync();
            await Task.WhenAll(profile, regulars, shares, models);
            if (await profile is { } p)
            {
                _body.Add(Ui.Card(Profile(p)));
            }

            _body.Add(Ui.Card(Regulars(await regulars ?? [])));
            _body.Add(Ui.Card(Shares(await shares ?? [])));
            if (await models is { Models.Count: > 0 } ai)
            {
                Picker picker = new() { Title = "Recap/Ask model", ItemsSource = ai.Models.Select(m => $"{m.Provider} · {m.Model}").ToList(), SelectedIndex = ai.Models.ToList().FindIndex(m => m.Id == ai.SelectedId) };
                picker.SelectedIndexChanged += async (_, _) => { if (picker.SelectedIndex >= 0) { await _api.SelectAiModelAsync(ai.Models[picker.SelectedIndex].Id); } };
                _body.Add(Ui.Card(Ui.Stack(6, Ui.H2("AI"), Ui.Field("Model", picker))));
            }
        }

        _body.Add(Ui.Card(Rules()));
        if (signedIn)
        {
            _body.Add(Ui.Card(Data()));
        }

        _body.Add(Ui.Card(Ui.Stack(6, Ui.H2("More"), Ui.Secondary("System status", () => Shell.Current.GoToAsync("diag")))));
        _body.Add(Ui.Card(Server()));
        _body.Add(Ui.Muted($"PoWatch for {DeviceInfo.Current.Platform} · v{AppInfo.Current.VersionString} · model {(ModelStore.IsDownloaded ? "downloaded" : "not downloaded")}"));
    }

    private View Profile(ProfileDto p)
    {
        Picker retention = new() { Title = "Keep raw data", ItemsSource = p.RetentionChoices.Select(d => $"{d} days").Prepend("forever").ToList() };
        retention.SelectedIndex = p.RetentionDays is { } days ? p.RetentionChoices.IndexOf(days) + 1 : 0;
        Switch digest = new() { IsToggled = p.DigestEmail, IsEnabled = p.DigestEmailAvailable };
        Entry place = new() { Placeholder = p.Place?.Name ?? "Town or city (for sunrise and weather)" };
        VerticalStackLayout hits = new();
        PlaceDto? chosen = p.Place;
        place.Completed += async (_, _) =>
        {
            hits.Clear();
            foreach (PlaceDto hit in (await _api.SearchPlacesAsync(place.Text ?? string.Empty)).Take(5))
            {
                Label row = new() { Text = $"{hit.Name}{(hit.Detail is { } d ? $", {d}" : string.Empty)}", TextColor = Ui.Accent, Padding = new Thickness(0, 4) };
                TapGestureRecognizer tap = new();
                tap.Tapped += (_, _) => { chosen = hit; place.Text = row.Text; hits.Clear(); };
                row.GestureRecognizers.Add(tap);
                hits.Add(row);
            }
        };

        return Ui.Stack(6,
            Ui.H2("Profile"),
            Ui.Field("Keep raw data", retention),
            Ui.Field(p.DigestEmailAvailable ? $"Weekly email to {p.Email}" : "Weekly email (not available)", digest),
            Ui.Text("Place"), place, hits,
            Ui.Primary("Save profile", async () =>
            {
                int? keep = retention.SelectedIndex > 0 ? p.RetentionChoices[retention.SelectedIndex - 1] : null;
                ProfileDto? saved = await _api.UpdateProfileAsync(new UpdateProfileDto { RetentionDays = keep, Place = chosen, DigestEmail = digest.IsToggled, TimeZoneId = TimeZoneInfo.Local.Id });
                await Ui.Toast(saved is null ? "That didn't save." : "Saved (time zone set to this phone's).");
            }));
    }

    private View Regulars(List<RegularDto> regulars)
    {
        VerticalStackLayout box = Ui.Stack(6, Ui.H2("Regulars"), Ui.Muted("People and pets PoWatch recognises by colours only — no faces."));
        if (regulars.Count == 0)
        {
            box.Add(Ui.Muted("None yet."));
        }

        foreach (RegularDto r in regulars.OrderByDescending(r => r.Visits))
        {
            Entry name = new() { Text = r.IsNamed ? r.DisplayName : null, Placeholder = r.DisplayName, MaxLength = RenameRegularRequestDto.MaxLength };
            Picker merge = new() { Title = "Same as…", ItemsSource = regulars.Where(o => o.Id != r.Id && o.Class == r.Class).Select(o => o.DisplayName).ToList() };
            box.Add(Ui.Stack(4,
                new Label { Text = $"{r.DisplayName} · {r.Class}", FontAttributes = FontAttributes.Bold },
                Ui.Muted($"{r.Visits} visits · last seen {r.LastSeenUtc.LocalDateTime:MMM d, t}"),
                name,
                Ui.Row(
                    Ui.Secondary("Rename", async () => { await _api.RenameRegularAsync(r.Id, string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim()); await RenderAsync(); }),
                    merge,
                    Ui.Secondary("Merge", async () =>
                    {
                        if (merge.SelectedItem is string dup && regulars.FirstOrDefault(o => o.DisplayName == dup && o.Id != r.Id) is { } other)
                        {
                            await _api.MergeRegularsAsync(r.Id, other.Id);
                            await RenderAsync();
                        }
                    }))));
        }

        return box;
    }

    private View Rules()
    {
        List<WatchRule> rules = SensingEngine.Rules;
        VerticalStackLayout box = Ui.Stack(6, Ui.H2("Watch rules (this phone)"), Ui.Muted("A notification when something matching enters the frame. Up to 8."));
        foreach (WatchRule rule in rules)
        {
            box.Add(Ui.Field(rule.ToString(), Ui.Danger("Remove", async () => { SensingEngine.Rules = [.. rules.Where(r => r != rule)]; await RenderAsync(); })));
        }

        if (rules.Count < 8)
        {
            Picker what = new() { Title = "What", ItemsSource = RuleClasses.Select(c => c == WatchRule.Any ? "anything" : c).ToList(), SelectedIndex = 1 };
            Picker from = new() { Title = "From", ItemsSource = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList(), SelectedIndex = 0 };
            Picker to = new() { Title = "To", ItemsSource = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList(), SelectedIndex = 0 };
            box.Add(Ui.Row(what, from, to));
            box.Add(Ui.Secondary("Add rule (same from/to = any time)", async () =>
            {
                SensingEngine.Rules = [.. rules, new WatchRule(RuleClasses[Math.Max(0, what.SelectedIndex)], Math.Max(0, from.SelectedIndex), Math.Max(0, to.SelectedIndex))];
                await RenderAsync();
            }));
        }

        return box;
    }

    private View Shares(List<ShareDto> shares)
    {
        VerticalStackLayout box = Ui.Stack(6, Ui.H2("Share links"));
        if (shares.Count == 0)
        {
            box.Add(Ui.Muted("No live links. Share a day from its Journal page."));
        }

        foreach (ShareDto share in shares)
        {
            box.Add(Ui.Field($"{share.Day:MMM d} · expires {share.ExpiresUtc.LocalDateTime:MMM d}", Ui.Danger("Revoke", async () => { await _api.RevokeShareAsync(share.Token); await RenderAsync(); })));
        }

        return box;
    }

    private View Data()
    {
        Entry confirm = new() { Placeholder = "Type DELETE to confirm" };
        return Ui.Stack(6,
            Ui.H2("Your data"),
            Ui.Secondary("Export CSV", async () =>
            {
                using HttpResponseMessage resp = await _api.DownloadAsync(ApiClient.ExportPath);
                if (!resp.IsSuccessStatusCode)
                {
                    await Ui.Toast("Export failed.");
                    return;
                }

                string file = Path.Combine(FileSystem.CacheDirectory, $"powatch-{DateTime.Now:yyyyMMdd}.csv");
                await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
                await Share.Default.RequestAsync(new ShareFileRequest("PoWatch export", new ShareFile(file, "text/csv")));
            }),
            confirm,
            Ui.Danger("Delete all my data", async () =>
            {
                if (confirm.Text != "DELETE")
                {
                    await Ui.Toast("Type DELETE in capitals, exactly.");
                    return;
                }

                await Ui.Toast(await _api.DeleteMyDataAsync() ? "Everything is deleted." : "Delete failed — try again.");
            }));
    }

    private View Server()
    {
        Entry baseUrl = new() { Text = _config.BaseUrl, Keyboard = Keyboard.Url };
        Entry clientId = new() { Text = _config.EntraClientId, Placeholder = "Entra app client id (GUID)" };
        Entry tenant = new() { Text = _config.EntraTenantId, Placeholder = "common" };
        return Ui.Stack(6,
            Ui.H2("Server"),
            Ui.Muted("Debug builds reach the PC's API over USB (scripts/mobile-dev.ps1). Microsoft sign-in needs the redirect URI " + MobileConfig.RedirectUri + " on the app registration."),
            Ui.Text("API address"), baseUrl,
            Ui.Text("Entra client id"), clientId,
            Ui.Text("Tenant"), tenant,
            Ui.Secondary("Save", async () =>
            {
                bool moved = baseUrl.Text?.TrimEnd('/') != _config.BaseUrl;
                _config.BaseUrl = baseUrl.Text ?? string.Empty;
                _config.EntraClientId = clientId.Text ?? string.Empty;
                _config.EntraTenantId = tenant.Text ?? string.Empty;
                if (moved)
                {
                    await SignOutAsync();
                }

                await Ui.Toast("Saved.");
            }));
    }

    private async Task SignOutAsync()
    {
        if (_engine.IsRunning)
        {
            await _engine.StopAsync();
        }

        await _api.LogoutAsync();
        await _entra.SignOutAsync();
        _cookies.Clear();
        _session.Clear();
        await Shell.Current.GoToAsync("login");
    }
}

/// <summary>System status (the web /diag page): stores and services with their state.</summary>
public sealed class DiagPage(ApiClient api, MobileConfig config) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "System status";
        VerticalStackLayout body = Ui.Stack(10, Ui.H1("System status"), Ui.Muted(config.BaseUrl));
        Content = new ScrollView { Content = body, Padding = 16 };
        (DiagReportDto? report, TimeSpan elapsed) = await api.GetDiagAsync();
        if (report is null)
        {
            body.Add(Ui.Colored("Can't reach the API.", Ui.Bad));
            return;
        }

        body.Add(Ui.Colored($"{report.Status} · {report.Environment} · v{report.Version} · answered in {elapsed.TotalMilliseconds:F0} ms", Badge(report.Status), 16));
        foreach ((string group, IReadOnlyList<DiagEntryDto> entries) in new[] { ("Data", report.Stores), ("Services", report.Services) })
        {
            body.Add(Ui.H2(group));
            foreach (DiagEntryDto e in entries)
            {
                body.Add(Ui.Card(Ui.Stack(3,
                    Ui.Field(e.Name, Ui.Colored(e.Status, Badge(e.Status), 14)),
                    Ui.Muted($"{e.Purpose}{(e.LatencyMs is { } ms ? $" · {ms:F0} ms" : string.Empty)}"),
                    Ui.Muted(e.Problem))));
            }
        }
    }

    private static Color Badge(string status) => status.ToLowerInvariant() switch
    {
        "healthy" => Ui.Good,
        "degraded" => Ui.Warn,
        _ => Ui.Bad,
    };
}

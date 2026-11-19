using System.Globalization;
using Microsoft.AspNetCore.Components;
using PoWatch.Client.Services;

namespace PoWatch.Client.Layout.Components;

/// <summary>A stat family's panels: loads whenever the range changes and renders nothing stale.</summary>
public abstract class StatsTabBase<T> : ComponentBase where T : class
{
    private StatsQuery? _loaded;
    private bool _dirty;

    [Inject] protected PoWatchApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public StatsQuery Query { get; set; } = default!;

    protected T? Data { get; private set; }

    protected bool Loading { get; private set; }

    protected abstract Task<T?> LoadAsync(StatsQuery query);

    protected override async Task OnParametersSetAsync()
    {
        if (Query == _loaded) return;
        _loaded = Query;
        Loading = true;
        _dirty = true;
        Data = Query.IsEmpty ? null : await LoadAsync(Query);
        Loading = false;
        _dirty = true;
    }

    /// <summary>Only when a load starts or lands: the host page re-renders several times a second.</summary>
    protected override bool ShouldRender()
    {
        var dirty = _dirty;
        _dirty = false;
        return dirty;
    }

    protected static string Percent(double value) => DisplayText.Percent(value);

    protected static string Number(double value, string format = "0.##") => DisplayText.Number(value, format);

    protected static string Duration(double seconds) => DisplayText.Duration(seconds);

    protected static string Local(DateTimeOffset? instant, string format = "ddd HH:mm") =>
        instant?.ToLocalTime().ToString(format, CultureInfo.CurrentCulture) ?? "—";
}

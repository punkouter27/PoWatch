using PoWatch.Domain.Models;

namespace PoWatch.Application.Contracts;

/// <summary>One profile per user; background jobs walk them all.</summary>
public interface IProfileStore
{
    Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken);

    Task UpsertAsync(UserProfile profile, CancellationToken cancellationToken);

    /// <summary>Every saved profile. Only users who changed a setting have one, so the set stays small.</summary>
    IAsyncEnumerable<UserProfile> ListAsync(CancellationToken cancellationToken);

    /// <summary>The user's Live panel arrangement; null until they change it.</summary>
    Task<LiveLayout?> GetLayoutAsync(string userId, CancellationToken cancellationToken);

    Task SaveLayoutAsync(string userId, LiveLayout layout, CancellationToken cancellationToken);
}

/// <summary>Shared recap links, found by token for anyone holding the link and by owner for revoking.</summary>
public interface IShareStore
{
    Task AddAsync(RecapShare share, CancellationToken cancellationToken);

    /// <summary>The share behind a token whoever asks, expired or not; null when there is none.</summary>
    Task<RecapShare?> GetAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecapShare>> ListAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Revokes one of the caller's own shares; false when the token is not theirs.</summary>
    Task<bool> DeleteAsync(string userId, string token, CancellationToken cancellationToken);
}

/// <summary>Daily weather and place search (Open-Meteo in production).</summary>
public interface IWeatherSource
{
    /// <summary>Each local day in [from, to] at the place, as far as the source has it; days it lacks are left out.</summary>
    Task<IReadOnlyList<DailyWeather>> GetDailyAsync(Place place, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Places matching a name, best first; empty for a blank or unknown name.</summary>
    Task<IReadOnlyList<(Place Place, string? Detail)>> SearchAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Sends the weekly digest by email, where the server has a way to.</summary>
public interface IDigestSender
{
    bool IsEnabled { get; }

    Task SendAsync(string to, string subject, string html, string text, CancellationToken cancellationToken);
}

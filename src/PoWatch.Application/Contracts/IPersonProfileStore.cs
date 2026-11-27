using PoWatch.Domain.Models;

namespace PoWatch.Application.Contracts;

/// <summary>Persistent anonymous face-matched person profiles per user (Azure Table Storage).</summary>
public interface IPersonProfileStore
{
    Task<IReadOnlyList<PersonProfile>> ListAsync(string userId, CancellationToken cancellationToken);
    Task UpsertAsync(PersonProfile profile, CancellationToken cancellationToken);
}

namespace PoWatch.Domain.Models;

/// <summary>How a user arranges Live's panels: panel ids in grid order, and the ids they hid.</summary>
public sealed record LiveLayout(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden);

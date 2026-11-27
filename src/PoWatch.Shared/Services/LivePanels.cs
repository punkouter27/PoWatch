namespace PoWatch.Shared.Services;

/// <summary>
/// The nine panels on Live and the rules for a user's arrangement of them: the order they fill the
/// three-by-three grid in, and which are hidden. Unknown ids are dropped and missing ones come back in
/// their default place, so a layout saved before a panel was added or renamed still works.
/// </summary>
public static class LivePanels
{
    public const string Camera = "camera";

    /// <summary>Every panel in its default order: row by row, with the camera in the middle cell.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        ("metrics", "Session"),
        ("inframe", "In frame"),
        ("faces", "Faces"),
        ("space", "Space"),
        (Camera, "Camera"),
        ("narration", "Narration"),
        ("environment", "Room insights"),
        ("regulars", "Regulars"),
        ("activity", "Activity"),
    ];

    public static IReadOnlyList<string> DefaultOrder { get; } = [.. All.Select(p => p.Id)];

    public static bool IsKnown(string? id) => id is not null && DefaultOrder.Contains(id);

    public static string Title(string id) => All.FirstOrDefault(p => p.Id == id).Title ?? id;

    /// <summary>
    /// Known ids once each in the given order; any panel the list leaves out goes back in at its default
    /// position, so a new panel appears where it was designed to be.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? order)
    {
        var result = (order ?? []).Where(IsKnown).Distinct().ToList();
        for (var i = 0; i < DefaultOrder.Count; i++)
        {
            if (!result.Contains(DefaultOrder[i])) result.Insert(Math.Min(i, result.Count), DefaultOrder[i]);
        }
        return result;
    }

    /// <summary>Known ids once each, in default order.</summary>
    public static List<string> CleanHidden(IEnumerable<string>? hidden)
    {
        var set = (hidden ?? []).ToHashSet();
        return [.. DefaultOrder.Where(set.Contains)];
    }

    /// <summary>
    /// Insert and shift: <paramref name="id"/> takes <paramref name="target"/>'s place and the panels in
    /// between move along by one. Dropping a panel on itself, or naming an unknown one, changes nothing.
    /// </summary>
    public static List<string> Move(IReadOnlyList<string> order, string id, string target)
    {
        var result = Normalize(order);
        var to = result.IndexOf(target);
        if (id == target || !result.Contains(id) || to < 0) return result;
        result.Remove(id);
        result.Insert(to, id);
        return result;
    }

    /// <summary>Moves a panel one place earlier (negative) or later (positive) in the order, for keyboard and touch.</summary>
    public static List<string> Step(IReadOnlyList<string> order, string id, int by)
    {
        var result = Normalize(order);
        var from = result.IndexOf(id);
        if (from < 0) return result;
        var to = Math.Clamp(from + by, 0, result.Count - 1);
        return to == from ? result : Move(result, id, result[to]);
    }
}

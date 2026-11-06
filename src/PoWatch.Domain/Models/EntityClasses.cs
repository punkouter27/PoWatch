namespace PoWatch.Domain.Models;

/// <summary>Detector class groups that stats care about.</summary>
public static class EntityClasses
{
    private static readonly HashSet<string> Presence = new(StringComparer.OrdinalIgnoreCase)
    {
        "person", "cat", "dog", "bird", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"
    };

    /// <summary>People and animals — the classes that make a scene "occupied".</summary>
    public static bool IsPresence(string className) => Presence.Contains(className);
}

namespace PoWatch.Shared.Services;

/// <summary>
/// Turns raw 24-bit RGB colours and 64-bin colour histograms into human-friendly colour names and hex
/// swatches, used by Room insights (palette) and Regulars (anonymous appearance chips).
/// </summary>
public static class ColorNamer
{
    /// <summary>Human-friendly name for a 24-bit RGB colour (e.g. "Warm grey", "Soft navy", "Charcoal").</summary>
    public static string Describe(int rgb)
    {
        var r = ((rgb >> 16) & 0xFF) / 255.0;
        var g = ((rgb >> 8) & 0xFF) / 255.0;
        var b = (rgb & 0xFF) / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var d = max - min;
        var s = d == 0 ? 0 : d / (1 - Math.Abs((2 * l) - 1));

        if (l < 0.14) return "Near black";
        if (l > 0.90 && s < 0.25) return "Off white";
        if (s < 0.14)
        {
            return l switch
            {
                < 0.30 => "Charcoal",
                < 0.55 => r >= b + 0.02 ? "Warm grey" : b >= r + 0.02 ? "Cool grey" : "Mid grey",
                < 0.78 => "Light grey",
                _ => "Silver"
            };
        }

        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;

        var hue = h switch
        {
            < 18 or >= 342 => "red",
            < 42 => l < 0.45 ? "brown" : "orange",
            < 66 => l < 0.45 ? "olive" : "yellow",
            < 150 => "green",
            < 195 => "teal",
            < 248 => "blue",
            < 292 => "purple",
            _ => "pink"
        };

        var tone = l switch
        {
            < 0.30 => "Deep",
            > 0.72 => "Pale",
            _ when s < 0.38 => "Muted",
            _ => "Rich"
        };

        return $"{tone} {hue}";
    }

    /// <summary>
    /// Dominant CSS hex colour from a 64-bin 4×4×4 RGB histogram (the regular's appearance signature).
    /// Null when the signature is empty or has a different shape.
    /// </summary>
    public static string? HexFromSignature(IReadOnlyList<float>? signature)
    {
        if (signature is not { Count: 64 }) return null;
        var best = -1;
        var bestVal = 0f;
        for (var i = 0; i < 64; i++)
        {
            if (signature[i] > bestVal)
            {
                bestVal = signature[i];
                best = i;
            }
        }
        if (best < 0 || bestVal <= 0) return null;
        var r = (((best >> 4) & 3) * 64) + 32;
        var g = (((best >> 2) & 3) * 64) + 32;
        var b = ((best & 3) * 64) + 32;
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    /// <summary>Human-readable zone name for a 16×9 grid cell index, e.g. "Middle left" or "Bottom far right".</summary>
    public static string ZoneName(int index, int columns = 16, int rows = 9)
    {
        if (index < 0 || index >= columns * rows) return "—";
        var col = index % columns;
        var row = index / columns;
        // Five bands across and three down, in words: "Bottom far left", "Top centre", "Centre".
        var vertical = row < rows / 3 ? "Top" : row >= (rows * 2) / 3 ? "Bottom" : "Middle";
        var horizontal = Math.Clamp(col * 5 / columns, 0, 4) switch
        {
            0 => "far left",
            1 => "left",
            2 => "centre",
            3 => "right",
            _ => "far right",
        };
        return vertical == "Middle" && horizontal == "centre" ? "Centre" : $"{vertical} {horizontal}";
    }

    /// <summary>The spreadsheet-style cell code ("I4") for a tooltip beside <see cref="ZoneName"/>.</summary>
    public static string ZoneCode(int index, int columns = 16) =>
        index < 0 ? "—" : $"{(char)('A' + (index % columns))}{(index / columns) + 1}";
}

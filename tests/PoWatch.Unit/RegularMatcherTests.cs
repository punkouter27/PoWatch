using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Unit;

public sealed class RegularMatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    /// <summary>A histogram concentrated in a few bins: a "red jacket" or a "blue jacket".</summary>
    private static float[] Look(params int[] bins)
    {
        var histogram = new float[RegularMatcher.SignatureBins];
        foreach (var bin in bins) histogram[bin] = 1f / bins.Length;
        return histogram;
    }

    private static Regular Person(string id, int number, float[] look) => new()
    {
        Id = id,
        UserId = "u",
        Class = "person",
        Number = number,
        Signature = look,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
        Sightings = 1
    };

    [Fact]
    public void Regulars_match_by_look_within_their_class_and_stay_anonymous()
    {
        var red = Look(48, 52, 56);
        var blue = Look(3, 7, 11);
        var known = new[] { Person("r1", 1, red), Person("r2", 2, blue), Person("r3", 3, red) with { Class = "dog", Number = 1 } };

        // The same red look with a little noise → Person 1; the dog with the same colours is another class.
        var redAgain = Look(48, 52, 56);
        Assert.Equal("r1", RegularMatcher.BestMatch(known, "person", redAgain.Select(v => v * 0.9f + 0.001f).ToArray())?.Id);
        Assert.Equal("r3", RegularMatcher.BestMatch(known, "dog", red)?.Id);
        Assert.Null(RegularMatcher.BestMatch(known, "person", Look(20, 24, 28)));
        Assert.Null(RegularMatcher.BestMatch(known, "cat", red));

        Assert.Equal(1, RegularMatcher.Similarity(red, red), 6);
        Assert.Equal(0, RegularMatcher.Similarity(red, blue), 6);

        // Never named: every regular reads by kind and number.
        Assert.Equal("Person 1", known[0].DisplayName);
        Assert.Equal("Person 2", known[1].DisplayName);
        Assert.Equal("Dog 1", known[2].DisplayName);

        // Blending moves the look slowly toward new sightings.
        var blended = RegularMatcher.Blend(red, sightings: 3, blue);
        Assert.Equal(0.75f * (1f / 3), blended[48], 5);
        Assert.Equal(0.25f * (1f / 3), blended[3], 5);
    }
}

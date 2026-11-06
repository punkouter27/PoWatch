using PoWatch.Application.Features.Shares;
using PoWatch.Domain.Models;

namespace PoWatch.Unit;

public sealed class ShareAndReplayTests
{
    [Fact]
    public void Share_tokens_are_22_url_safe_characters_and_nothing_else_passes_for_one()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => ShareService.NewToken()).ToList();
        Assert.All(tokens, t => Assert.True(ShareService.IsToken(t), t));
        Assert.Equal(50, tokens.Distinct().Count());

        Assert.False(ShareService.IsToken(null));
        Assert.False(ShareService.IsToken("short"));
        Assert.False(ShareService.IsToken("../../../etc/passwd/xxx"));
        Assert.False(ShareService.IsToken("aaaaaaaaaaaaaaaaaaaaa'"));
    }

    [Fact]
    public void A_replay_grid_is_shaded_relative_to_its_busiest_cell()
    {
        var grid = new float[SpatialGrid.Cells];
        grid[0] = 0.02f;
        grid[5] = 0.04f;
        var bytes = new byte[SpatialGrid.Cells];

        SpatialGrid.Quantize(grid, bytes);
        Assert.Equal(255, bytes[5]);
        Assert.Equal(128, bytes[0]);
        Assert.Equal(0, bytes[1]);

        // A minute without a grid (or without motion) is all zeros, not a division by zero.
        bytes[3] = 9;
        SpatialGrid.Quantize(ReadOnlySpan<float>.Empty, bytes);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }
}

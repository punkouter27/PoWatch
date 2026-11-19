using PoWatch.Shared.Services;

namespace PoWatch.Mobile.Services.Sensing;

/// <summary>One camera frame as tightly packed RGBA bytes, upright (already rotated to the scene).</summary>
public sealed record RgbaFrame(byte[] Pixels, int Width, int Height, DateTimeOffset AtUtc);

/// <summary>
/// The web's L0 pixel layer (js/sensing/pixel-layer.js), ported: the frame is reduced to 64×36 — 4×4
/// pixels per cell of the 16×9 grid the server aggregates — then motion (share of pixels whose RGB moved
/// by more than 28 since the last sample), the per-cell motion grid, Rec.709 luminance and the five most
/// common colours binned 4 bits per channel. Same numbers as a browser watching the same room.
/// </summary>
public sealed class PixelLayer
{
    private const int Cols = 16, Rows = 9, Cell = 4, Width = Cols * Cell, Height = Rows * Cell, Delta = 28;
    private byte[]? _previous;

    public void Reset() => _previous = null;

    public PixelSample Sample(RgbaFrame frame)
    {
        byte[] small = Downscale(frame);
        int[] changedPerCell = new int[Cols * Rows];
        Dictionary<int, int> bins = [];
        int changed = 0;
        double lumaSum = 0;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                int r = small[i], g = small[i + 1], b = small[i + 2];
                lumaSum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                int bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                bins[bin] = bins.GetValueOrDefault(bin) + 1;
                if (_previous is not null)
                {
                    int d = Math.Abs(r - _previous[i]) + Math.Abs(g - _previous[i + 1]) + Math.Abs(b - _previous[i + 2]);
                    if (d / 3.0 > Delta)
                    {
                        changed++;
                        changedPerCell[(y / Cell * Cols) + (x / Cell)]++;
                    }
                }
            }
        }

        bool first = _previous is null;
        _previous = small;
        const int total = Width * Height, perCell = Cell * Cell;
        return new PixelSample(
            frame.AtUtc,
            first ? 0 : changed / (double)total,
            lumaSum / total / 255,
            [.. changedPerCell.Select(n => first ? 0f : n / (float)perCell)],
            [.. bins.OrderByDescending(kv => kv.Value).Take(5).Select(kv => BinCentre(kv.Key))]);
    }

    private static int BinCentre(int bin)
    {
        static int Channel(int nibble) => (nibble << 4) | 0x8;
        return (Channel((bin >> 8) & 0xF) << 16) | (Channel((bin >> 4) & 0xF) << 8) | Channel(bin & 0xF);
    }

    // Box-averages each 64×36 pixel from its block of the source, as a canvas drawImage downscale does.
    private static byte[] Downscale(RgbaFrame frame)
    {
        byte[] small = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int sy0 = y * frame.Height / Height, sy1 = Math.Max(sy0 + 1, (y + 1) * frame.Height / Height);
            for (int x = 0; x < Width; x++)
            {
                int sx0 = x * frame.Width / Width, sx1 = Math.Max(sx0 + 1, (x + 1) * frame.Width / Width);
                long r = 0, g = 0, b = 0;
                int n = 0;
                // Every second source pixel each way: plenty for an average, a quarter of the work.
                for (int sy = sy0; sy < sy1; sy += 2)
                {
                    for (int sx = sx0; sx < sx1; sx += 2)
                    {
                        int s = (sy * frame.Width + sx) * 4;
                        r += frame.Pixels[s];
                        g += frame.Pixels[s + 1];
                        b += frame.Pixels[s + 2];
                        n++;
                    }
                }

                int o = (y * Width + x) * 4;
                small[o] = (byte)(r / n);
                small[o + 1] = (byte)(g / n);
                small[o + 2] = (byte)(b / n);
                small[o + 3] = 255;
            }
        }

        return small;
    }
}

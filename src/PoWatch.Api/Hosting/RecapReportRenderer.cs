using System.Globalization;
using PoWatch.Shared.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PoWatch.Api.Hosting;

/// <summary>
/// Renders a <see cref="RecapDto"/> with QuestPDF (Community licence): a one-to-two page PDF, or the
/// 1200×630 image card a shared recap shows when its link is pasted somewhere.
/// </summary>
public static class RecapReportRenderer
{
    /// <summary>
    /// The PDF engine cannot run on this machine. QuestPDF renders through a native Skia library and
    /// ships no <c>win-arm64</c> build; callers turn this into an explained 503 instead of a bare 500.
    /// </summary>
    public sealed class RendererUnavailableException(string message, Exception inner) : InvalidOperationException(message, inner);

    public static byte[] Render(RecapDto recap, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(recap);
        return Guarded(() => RenderCore(recap, zone));
    }

    /// <summary>The share card: title, the opening of the paragraph and four headline numbers, as a PNG.</summary>
    public static byte[] RenderCard(RecapDto recap)
    {
        ArgumentNullException.ThrowIfNull(recap);
        return Guarded(() => RenderCardCore(recap));
    }

    private static byte[] Guarded(Func<byte[]> render)
    {
        try
        {
            return render();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new RendererUnavailableException(
                "The PDF engine could not start on this machine. QuestPDF needs a native library that is not available for "
                + $"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}. The recap itself is fine — read it in the app.",
                ex);
        }
    }

    private static byte[] RenderCardCore(RecapDto recap)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        const string Amber = "#f5a524", Ink = "#e6e9ee", Muted = "#9aa4b2";
        var summary = recap.Summary.Length <= 240 ? recap.Summary : recap.Summary[..recap.Summary.LastIndexOf(' ', 237)] + "…";

        return Document.Create(container => container.Page(page =>
        {
            page.Size(1200, 630, Unit.Point);
            page.Margin(56);
            page.PageColor("#0d1014");
            page.DefaultTextStyle(ts => ts.FontColor(Ink));
            page.Content().Column(col =>
            {
                col.Item().Text("POWATCH · DAY RECAP").FontSize(20).Bold().FontColor(Amber).LetterSpacing(0.08f);
                col.Item().PaddingTop(10).Text(recap.Title).FontSize(48).Bold();
                col.Item().PaddingTop(18).Height(200).Text(summary).FontSize(26).LineHeight(1.35f).FontColor("#c9ced6");
                col.Item().PaddingTop(20).LineHorizontal(2).LineColor(Amber);
                col.Item().PaddingTop(20).Row(row =>
                {
                    foreach (var number in recap.Numbers.Take(4))
                    {
                        row.RelativeItem().Column(cell =>
                        {
                            cell.Item().Text(number.Label.ToUpperInvariant()).FontSize(16).FontColor(Muted).LetterSpacing(0.06f);
                            cell.Item().Text(number.Value).FontSize(38).Bold().FontColor(Amber);
                        });
                    }
                });
            });
        })).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 72 }).First();
    }

    private static byte[] RenderCore(RecapDto recap, TimeZoneInfo zone)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        string Local(DateTimeOffset at, string format) => TimeZoneInfo.ConvertTime(at, zone).ToString(format, CultureInfo.InvariantCulture);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(ts => ts.FontSize(10));

            page.Header().Column(col =>
            {
                col.Item().Text("PoWatch — Recap").FontSize(9).FontColor(Colors.Orange.Darken2).Bold();
                col.Item().Text(recap.Title).FontSize(20).Bold();
                col.Item().Text($"{recap.Subtitle}  ·  {Local(recap.FromUtc, "yyyy-MM-dd HH:mm")} – {Local(recap.ToUtc, "HH:mm")} ({zone.Id})")
                    .FontSize(9).FontColor(Colors.Grey.Darken2);
                col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Orange.Lighten2);
            });

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(10);
                col.Item().Text(recap.Summary).FontSize(12).LineHeight(1.4f);

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                    foreach (var number in recap.Numbers)
                    {
                        table.Cell().Padding(4).Column(cell =>
                        {
                            cell.Item().Text(number.Label.ToUpperInvariant()).FontSize(7).FontColor(Colors.Grey.Darken1);
                            cell.Item().Text(number.Value).FontSize(14).Bold();
                        });
                    }
                });

                if (recap.Highlights.Count > 0)
                {
                    col.Item().Text("Highlights").FontSize(12).Bold();
                    foreach (var highlight in recap.Highlights)
                        col.Item().Row(row => { row.ConstantItem(12).Text("•"); row.RelativeItem().Text(highlight); });
                }

                if (recap.Moments.Count > 0)
                {
                    col.Item().Text("Moments").FontSize(12).Bold();
                    foreach (var moment in recap.Moments)
                        col.Item().Row(row =>
                        {
                            row.ConstantItem(60).Text(Local(moment.AtUtc, "HH:mm:ss")).FontColor(Colors.Grey.Darken2);
                            row.RelativeItem().Text(moment.Text);
                        });
                }
            });

            page.Footer().AlignRight().Text(text =>
            {
                text.Span($"Written by: {recap.Source} · page ").FontSize(8).FontColor(Colors.Grey.Medium);
                text.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
            });
        })).GeneratePdf();
    }
}

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Shares;

/// <summary>
/// The public page behind a shared recap link: plain server-rendered HTML (link previews do not run
/// the app), every value HTML-encoded, no scripts, and kept out of search engines.
/// </summary>
internal static class SharePage
{
    /// <summary>No scripts, no frames, no outside requests: the page can show text and its own card, nothing else.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static string Render(RecapDto recap, Uri cardUrl, DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(recap);
        ArgumentNullException.ThrowIfNull(cardUrl);
        var e = HtmlEncoder.Default;
        var description = recap.Summary.Length <= 200 ? recap.Summary : recap.Summary[..197] + "…";

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="robots" content="noindex, nofollow" />
            <title>{{e.Encode(recap.Title)}} · PoWatch</title>
            <meta property="og:type" content="article" />
            <meta property="og:title" content="{{e.Encode(recap.Title)}} · PoWatch" />
            <meta property="og:description" content="{{e.Encode(description)}}" />
            <meta property="og:image" content="{{e.Encode(cardUrl.ToString())}}" />
            <meta property="og:image:width" content="1200" />
            <meta property="og:image:height" content="630" />
            <meta name="twitter:card" content="summary_large_image" />
            <style>
              :root { color-scheme: dark; }
              body { margin: 0; padding: 32px 16px; background: #0d1014; color: #e6e9ee; font: 16px/1.55 system-ui, sans-serif; }
              main { max-width: 720px; margin: 0 auto; }
              .kicker { color: #f5a524; font: 700 12px/1 ui-monospace, monospace; letter-spacing: .1em; }
              h1 { font-size: 28px; margin: 8px 0 2px; }
              .sub { color: #9aa4b2; margin: 0 0 20px; }
              .numbers { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin: 24px 0; padding: 0; list-style: none; }
              .numbers li { border: 1px solid #262d38; border-radius: 6px; padding: 10px 12px; background: #151a21; }
              .numbers span { display: block; color: #9aa4b2; font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
              .numbers b { color: #f5a524; font-size: 22px; }
              footer { margin-top: 32px; color: #6b7480; font-size: 13px; }
            </style>
            </head>
            <body>
            <main>
            <p class="kicker">POWATCH · SHARED DAY RECAP</p>
            <h1>{{e.Encode(recap.Title)}}</h1>
            <p class="sub">{{e.Encode(recap.Subtitle)}}</p>
            <p>{{e.Encode(recap.Summary)}}</p>
            """);

        if (recap.Numbers.Count > 0)
        {
            html.Append("<ul class=\"numbers\">");
            foreach (var number in recap.Numbers)
                html.Append(CultureInfo.InvariantCulture, $"<li><span>{e.Encode(number.Label)}</span><b>{e.Encode(number.Value)}</b></li>");
            html.Append("</ul>");
        }
        if (recap.Highlights.Count > 0)
        {
            html.Append("<ul>");
            foreach (var highlight in recap.Highlights)
                html.Append(CultureInfo.InvariantCulture, $"<li>{e.Encode(highlight)}</li>");
            html.Append("</ul>");
        }

        html.Append(CultureInfo.InvariantCulture, $$"""
            <footer>Shared from PoWatch, a stat-heavy webcam. Numbers only: no camera images leave the owner's browser. This link stops working {{expiresUtc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}}.</footer>
            </main>
            </body>
            </html>
            """);
        return html.ToString();
    }

    public static string NotFound() => """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><meta name="robots" content="noindex" />
        <title>Link not found · PoWatch</title>
        <style>body { margin: 0; padding: 48px 16px; background: #0d1014; color: #e6e9ee; font: 16px/1.5 system-ui, sans-serif; text-align: center; }</style></head>
        <body><p>This shared recap does not exist, has expired, or was taken down by its owner.</p></body></html>
        """;
}

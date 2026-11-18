using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PoWatch.Api.Hosting;

/// <summary>
/// Development only: binds <c>PoWatch:Ports:Http</c>/<c>Https</c> on both loopback families and, when a
/// port is held by another app, steps to the next free one and says so. Everywhere else the host's
/// own binding (ASPNETCORE_URLS, set by App Service) stands untouched, and so does an explicit
/// <c>--urls</c> / <c>ASPNETCORE_URLS</c> in Development: Kestrel's own Listen calls would override it.
/// </summary>
public static class PortNegotiation
{
    private const int Attempts = 16;

    public static void Configure(KestrelServerOptions options, IConfiguration configuration, IHostEnvironment env, Serilog.ILogger logger)
    {
        if (!env.IsDevelopment()
            || configuration.GetSection("Kestrel:Endpoints").Exists()
            || !string.IsNullOrWhiteSpace(configuration["urls"])) return;

        var busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(l => IPAddress.IsLoopback(l.Address)).Select(l => l.Port).ToHashSet();

        var http = Listen(options, configuration.GetValue("PoWatch:Ports:Http", 5000), busy, logger, https: false);
        var https = Listen(options, configuration.GetValue("PoWatch:Ports:Https", 5001), busy, logger, https: true);
        logger.Information("Kestrel listening on http://localhost:{HttpPort} and https://localhost:{HttpsPort}", http, https);
    }

    private static int Listen(KestrelServerOptions options, int requested, HashSet<int> busy, Serilog.ILogger logger, bool https)
    {
        var port = Enumerable.Range(requested, Attempts).Cast<int?>().FirstOrDefault(p => !busy.Contains(p!.Value))
            ?? throw new IOException($"No free port in [{requested}..{requested + Attempts - 1}].");
        if (port != requested) logger.Warning("Port {Requested} is in use; using {Actual} instead", requested, port);
        busy.Add(port);

        // Both families: Windows resolves "localhost" to ::1 first, so an IPv4-only bind looks dead.
        options.Listen(IPAddress.Loopback, port, Use);
        if (Socket.OSSupportsIPv6) options.Listen(IPAddress.IPv6Loopback, port, Use);
        return port;

        void Use(ListenOptions listen)
        {
            if (https) listen.UseHttps();
        }
    }
}

using System.Net.Sockets;

namespace PoWatch.Infrastructure.Runtime;

/// <summary>
/// An HTTP handler whose TCP connect gives up after a few seconds and tries again. From App Service,
/// about one in three new connections to Open-Meteo never finishes its handshake (seen 2026-11-23),
/// while a fresh attempt answers in under a second. Only the connect is retried, before any request
/// bytes are sent, so no request can run twice.
/// </summary>
public static class RetryingConnectHandler
{
    public static SocketsHttpHandler Create(int attempts = 3, TimeSpan? perAttempt = null)
    {
        var timeout = perAttempt ?? TimeSpan.FromSeconds(2.5);
        return new SocketsHttpHandler
        {
            // Pooled connections are recycled so DNS changes still land.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectCallback = async (context, cancellationToken) =>
            {
                for (var attempt = 1; ; attempt++)
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(timeout);
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, limit.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (attempt < attempts && !cancellationToken.IsCancellationRequested
                                               && ex is OperationCanceledException or SocketException)
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            },
        };
    }
}

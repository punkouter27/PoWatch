using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace PoWatch.Application;

/// <summary>
/// Runs one AI job down the request's model chain (the chosen model, then the next configured one):
/// each attempt gets its own timeout, a failure or timeout moves on, and the model that answered is
/// logged. Whatever checks the caller makes on the answer (<c>RecapPrompt.KeepsToFacts</c>) apply to
/// every attempt alike, since they run on the returned value.
/// </summary>
public static class AiFallback
{
    /// <summary>The first answer, or the last failure rethrown when every model failed.</summary>
    /// <exception cref="InvalidOperationException">The chain is empty.</exception>
    public static async Task<T> FirstAnswerAsync<T>(
        this IEnumerable<IChatClient> chain,
        string job,
        TimeSpan timeout,
        Func<IChatClient, CancellationToken, Task<T>> attempt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (var chat in chain)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                var answer = await attempt(chat, limit.Token);
                logger.LogInformation("AI {Job} answered by {Model}{Fallback}.", job, ModelName(chat), failure is null ? string.Empty : " (fallback)");
                return answer;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeouts, transport and provider errors (the OpenAI SDK throws ClientResultException,
                // not HttpRequestException) and a reply that is not the asked-for JSON.
                logger.LogWarning(ex, "AI {Job} failed on {Model}.", job, ModelName(chat));
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }
        failure?.Throw();
        throw new InvalidOperationException("No AI model is selected.");
    }

    /// <summary>The model behind a client, for logs: its default model id, else its type.</summary>
    public static string ModelName(IChatClient chat) =>
        chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? chat.GetType().Name;
}

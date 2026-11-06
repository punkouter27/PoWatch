using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PoWatch.Application.Features.Ask;

namespace PoWatch.Unit;

public sealed class AssistServiceTests
{
    /// <summary>A model that always says the same thing.</summary>
    private sealed class Canned(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A model whose provider is down.</summary>
    private sealed class Down : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("provider unavailable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static AssistService With(string reply) => new([new Canned(reply)], NullLogger<AssistService>.Instance);

    [Fact]
    public async Task A_model_drafts_only_rules_and_headlines_that_survive_the_checks()
    {
        var ct = CancellationToken.None;

        // A rule must name something the detector can see, with real hours.
        var dog = await With("""{"class":"Dog","fromHour":22,"toHour":6}""").DraftRuleAsync("a dog at night", ct);
        Assert.Equal(("dog", 22, 6), (dog!.Class, dog.FromHour, dog.ToHour));
        Assert.Null(await With("""{"class":"burglar","fromHour":22,"toHour":6}""").DraftRuleAsync("a burglar at night", ct));
        Assert.Null(await With("""{"class":"cat","fromHour":25,"toHour":6}""").DraftRuleAsync("a cat at 25", ct));
        Assert.Null(await With("not json").DraftRuleAsync("a cat", ct));

        // A headline may only use the summary's numbers.
        const string summary = "The room was occupied 62% of the time, with 251 visits.";
        Assert.Equal("251 visits and a busy room", await With("""{"headline":"251 visits and a busy room"}""").HeadlineAsync(summary, ct));
        Assert.Null(await With("""{"headline":"Three hundred visits!"}""").HeadlineAsync(summary, ct));

        // No model: nothing, and no error.
        Assert.Null(await new AssistService([], NullLogger<AssistService>.Instance).HeadlineAsync(summary, ct));
    }

    [Fact]
    public async Task A_failing_model_hands_over_to_the_next_once_and_its_answer_is_checked_too()
    {
        var ct = CancellationToken.None;
        const string summary = "The room was occupied 62% of the time, with 251 visits.";
        var down = new Down();

        Assert.Equal("251 visits and a busy room", await new AssistService(
            [down, new Canned("""{"headline":"251 visits and a busy room"}""")], NullLogger<AssistService>.Instance).HeadlineAsync(summary, ct));
        Assert.Equal(1, down.Calls);
        Assert.Null(await new AssistService(
            [down, new Canned("""{"headline":"Nine hundred visits!"}""")], NullLogger<AssistService>.Instance).HeadlineAsync(summary, ct));
        Assert.Null(await new AssistService([down, down], NullLogger<AssistService>.Instance).HeadlineAsync(summary, ct));
        Assert.Equal(4, down.Calls);
    }
}

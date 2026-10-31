using Microsoft.Extensions.AI;
using PoWatch.Shared.Models;

namespace PoWatch.Application.Contracts;

/// <summary>The configured server models and their shared, model-isolated chat pipelines.</summary>
public interface IAiModelRegistry
{
    /// <summary>Template, Auto (when any model is configured) and each configured model, in offer order.</summary>
    IReadOnlyList<AiModelDto> Models { get; }

    string DefaultId { get; }

    IChatClient? GetClient(string id);

    /// <summary>
    /// The clients to try for a selection, in order: the chosen model (Auto's pick for Auto), then the
    /// next configured one. Lazy, so the fallback is resolved only after the first one failed; empty
    /// for Template.
    /// </summary>
    IEnumerable<IChatClient> Chain(string id);

    /// <summary><see cref="Models"/> with Auto's label naming the model it resolves to right now.</summary>
    IReadOnlyList<AiModelDto> Describe();
}

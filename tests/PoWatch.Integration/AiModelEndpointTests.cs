using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoWatch.Shared.Models;

namespace PoWatch.Integration;

public sealed class AiModelEndpointTests(AzuriteWebApplicationFactory factory) : IClassFixture<AzuriteWebApplicationFactory>
{
    [Fact]
    public async Task Model_selection_is_validated_persisted_and_isolated_between_browsers()
    {
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        browser.DefaultRequestHeaders.Add("X-Fake-User", "ai-selection");
        otherBrowser.DefaultRequestHeaders.Add("X-Fake-User", "ai-selection");
        var initial = await browser.GetFromJsonAsync<AiModelsDto>("/api/ai/models");
        Assert.NotNull(initial);
        Assert.Contains(initial.Models, m => m.Id == "Template");
        Assert.Contains(initial.Models, m => m.Id == "Ollama");

        var saved = await browser.PutAsJsonAsync("/api/ai/models", new SelectAiModelDto("Ollama"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains(saved.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("httponly", StringComparison.OrdinalIgnoreCase) && value.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Ollama", (await browser.GetFromJsonAsync<AiModelsDto>("/api/ai/models"))!.SelectedId);
        Assert.Equal(initial.SelectedId, (await otherBrowser.GetFromJsonAsync<AiModelsDto>("/api/ai/models"))!.SelectedId);

        var invalid = await browser.PutAsJsonAsync("/api/ai/models", new SelectAiModelDto("not-configured"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Ollama", (await browser.GetFromJsonAsync<AiModelsDto>("/api/ai/models"))!.SelectedId);

        var auto = await (await browser.PutAsJsonAsync("/api/ai/models", new SelectAiModelDto("Auto"))).Content.ReadFromJsonAsync<AiModelsDto>();
        Assert.Equal("Auto", auto!.SelectedId);
        Assert.NotEqual("Ollama, else cloud", auto.Models.Single(m => m.Id == "Auto").Model);

        await browser.PutAsJsonAsync("/api/ai/models", new SelectAiModelDto("Template"));
        Assert.Equal("Template", (await browser.GetFromJsonAsync<AiModelsDto>("/api/ai/models"))!.SelectedId);
        var noAi = await browser.PostAsJsonAsync("/api/ask", new AskRequestDto { Question = "What happened?", TimeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noAi.StatusCode);
    }
}

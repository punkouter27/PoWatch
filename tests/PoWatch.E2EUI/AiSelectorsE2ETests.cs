using Microsoft.Playwright;

namespace PoWatch.E2EUI;

[Collection(nameof(PlaywrightCollection))]
public sealed class AiSelectorsE2ETests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task Model_choices_on_Live_persist_switch_during_a_session_and_fit_on_a_phone()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser, user: $"ai-selectors-{Guid.NewGuid():N}");
        // The model choices are a strip on Live.
        await page.GetByTestId("nav-live").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-caption-model")).ToBeHiddenAsync();
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        var captions = page.GetByTestId("ai-caption-model");
        var chat = page.GetByTestId("ai-chat-model");
        await Assertions.Expect(chat).ToBeEnabledAsync();
        await captions.SelectOptionAsync("qwen2.5vl:7b");
        await chat.SelectOptionAsync("Ollama");
        await Assertions.Expect(chat).ToBeEnabledAsync();
        await page.GetByTestId("ai-hearing").SelectOptionAsync("off");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-hearing")).ToHaveValueAsync("off", new() { Timeout = 60_000 });
        await page.GetByTestId("ai-hearing").SelectOptionAsync("on");
        await Assertions.Expect(captions).ToHaveValueAsync("qwen2.5vl:7b", new() { Timeout = 60_000 });
        await Assertions.Expect(chat).ToHaveValueAsync("Ollama");

        page.ThrowIfErrors();
        await page.RouteAsync("**/api/ai/models", async route =>
        {
            if (route.Request.Method == "PUT") await route.FulfillAsync(new() { Status = 503 });
            else await route.ContinueAsync();
        });
        await chat.SelectOptionAsync("Template");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Could not switch server AI");
        await Assertions.Expect(chat).ToHaveValueAsync("Ollama");
        await page.UnrouteAsync("**/api/ai/models");
        // Clear the intentional 503 fault-injection error verified immediately above.
        page.ClearErrors();

        // Exercise the real session scheduler without downloading GPU models or requesting a camera.
        await page.EvaluateAsync("""
            () => {
                window.captionCalls = [];
                powatchInference.startPreview = async () => 'OK';
                powatchInference.captureAndInfer = async (...args) => {
                    captionCalls.push(args[3]);
                    return { isAvailable: false, status: 'test' };
                };
                powatchInference.getInferenceDiagnostics = async () => ({
                    modelKey: captionCalls.at(-1), loadState: 'ready', device: 'ollama', loadError: null
                });
                powatchDetector.load = async () => ({ modelId: 'test', device: 'wasm' });
                powatchDetector.start = () => {};
                const detector = { name: 'detector', model: 'onnx-community/rfdetr_nano-ONNX', device: 'webgpu', dtype: 'fp16' };
                window.detectorLoad = { ...detector, phase: 'downloading', loaded: 50 * 1048576, total: 100 * 1048576, at: Date.now() };
                window.detectorReady = () => { detectorLoad = { ...detector, phase: 'ready', loadMs: 4200, at: Date.now() }; };
                window.detectorOnCpu = () => { detectorLoad = { ...detector, device: 'wasm', dtype: 'q8', phase: 'ready', reason: 'this browser offers no WebGPU', at: Date.now() }; };
                const hearing = { name: 'hearing', model: 'speech', device: 'wasm', dtype: 'q8', phase: 'ready', at: Date.now() };
                powatchModels.snapshot = () => [detectorLoad, hearing];
            }
            """);
        await page.GetByTestId("start-camera").ClickAsync();
        // The detector's load shows as it goes: a download with its bytes, then where it runs.
        var detectorStatus = page.GetByTestId("ai-detector-status");
        await Assertions.Expect(detectorStatus).ToContainTextAsync("RF-DETR NANO · LOADING 50% · 50/100 MB");
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-config-toggle")).ToContainTextAsync("LOADING 50%");
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        await page.EvaluateAsync("() => detectorReady()");
        await Assertions.Expect(detectorStatus).ToContainTextAsync("READY · WEBGPU FP16");
        await Assertions.Expect(detectorStatus).ToHaveAttributeAsync("data-phase", "ready");
        // Hearing runs on the CPU by design; a GPU model that lands there says so once, with why.
        await Assertions.Expect(page.GetByTestId("ai-hearing-status")).ToContainTextAsync("WORDS · SOUNDS · READY · CPU Q8");
        await page.EvaluateAsync("() => detectorOnCpu()");
        await Assertions.Expect(page.GetByTestId("model-cpu-notice")).ToContainTextAsync("object detector is running on the CPU, not the GPU, because this browser offers no WebGPU");
        await page.WaitForFunctionAsync("() => captionCalls.includes('qwen2.5vl:7b')");
        await Assertions.Expect(page.GetByTestId("ai-caption-status")).ToContainTextAsync("OLLAMA");
        await captions.SelectOptionAsync("minicpm-v");
        await page.WaitForFunctionAsync("() => captionCalls.at(-1) === 'minicpm-v'");
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("OBSERVING");
        await captions.SelectOptionAsync("off");
        await Assertions.Expect(page.GetByTestId("ai-caption-status")).ToHaveTextAsync("OFF");
        await captions.SelectOptionAsync("qwen3-vl:8b-instruct");
        await page.WaitForFunctionAsync("() => captionCalls.at(-1) === 'qwen3-vl:8b-instruct'");
        await page.GetByTestId("header-stop").ClickAsync();

        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/");
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        await Assertions.Expect(captions).ToHaveValueAsync("qwen3-vl:8b-instruct", new() { Timeout = 60_000 });

        await page.SetViewportSizeAsync(375, 812);
        var ai = await page.GetByTestId("ai-models").BoundingBoxAsync();
        Assert.NotNull(ai);
        Assert.True(ai.X >= 0 && ai.X + ai.Width <= 375);
        Assert.True((await captions.BoundingBoxAsync())!.Height >= 44);
        await page.AssertNoBlazorErrorAsync();
    }
}

using Microsoft.Playwright;

namespace PoWatch.E2EUI;

[Collection(nameof(PlaywrightCollection))]
public sealed class SensoryE2ETests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task Demo_flow_renders_and_releases_without_leaving_a_canvas_or_animation_running()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        await page.GetByTestId("demo-effects").ClickAsync();
        await page.WaitForFunctionAsync("""
            () => ['webgpu', 'canvas'].includes(document.querySelector('.demo-flow')?.dataset.renderer)
                && [...document.querySelectorAll('.demo-flow')].some(c => !c.hidden && c.width > 300)
            """);
        var gpuAvailable = await page.EvaluateAsync<bool>("async () => !!(await navigator.gpu?.requestAdapter())");
        if (gpuAvailable)
            await Assertions.Expect(page.Locator(".demo-flow")).ToHaveAttributeAsync("data-renderer", "webgpu");
        await page.GetByTestId("demo-effects").ClickAsync();
        await Assertions.Expect(page.Locator(".demo-flow")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".demo-flow")).ToHaveAttributeAsync("data-renderer", "off");

        await page.GetByTestId("demo-effects").ClickAsync();
        await page.GetByTestId("nav-journal").ClickAsync();
        await page.ExpectSectionAsync("JOURNAL");
        await Assertions.Expect(page.Locator(".demo-flow")).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Reduced_motion_uses_a_static_canvas_and_drawers_close_without_trapping_focus()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.GetByTestId("demo-effects").ClickAsync();
        await page.WaitForFunctionAsync("""
            () => document.querySelector('.demo-flow')?.dataset.renderer === 'canvas'
                && [...document.querySelectorAll('.demo-flow')].some(c => !c.hidden && c.width > 300)
            """);
        var image = await page.Locator(".demo-flow:not([hidden])").EvaluateAsync<string>("c => c.toDataURL()");
        await page.WaitForTimeoutAsync(250);
        Assert.Equal(image, await page.Locator(".demo-flow:not([hidden])").EvaluateAsync<string>("c => c.toDataURL()"));

        await page.GetByTestId("settings-menu").ClickAsync();
        await Assertions.Expect(page.Locator(".term-drawer:not([hidden])")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".term-drawer")).ToBeHiddenAsync();
        await page.GetByTestId("tray-toggle").ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".term-drawer:not([hidden])")).ToHaveCountAsync(0);
        await page.GetByTestId("settings-menu").ClickAsync();
        await Assertions.Expect(page.Locator(".term-drawer:not([hidden])")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".term-drawer:not([hidden])")).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Heat_shader_settles_without_a_permanent_render_loop_and_resumes_for_new_data()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        var supported = await page.EvaluateAsync<bool>("""
            () => {
                const canvas = document.createElement('canvas');
                canvas.id = 'sensory-heat';
                canvas.style.cssText = 'position:fixed;top:80px;left:20px;width:320px;height:180px;z-index:90';
                document.body.append(canvas);
                const gl = canvas.getContext('webgl2');
                if (!gl) { canvas.remove(); return false; }
                window.__heatDraws = 0;
                const draw = gl.drawArrays.bind(gl);
                gl.drawArrays = (...args) => { window.__heatDraws++; draw(...args); };
                const values = Array.from({length:144}, (_,i) => i === 70 ? 1 : 0);
                window.powatchHeat.draw(canvas, values, 16, 9);
                return true;
            }
            """);
        Assert.True(supported, "Chromium must provide WebGL2 to validate the heat shader.");
        await page.WaitForTimeoutAsync(1800);
        var before = await page.EvaluateAsync<int>("() => window.__heatDraws");
        Assert.True(before > 0);
        await page.WaitForTimeoutAsync(200);
        Assert.Equal(before, await page.EvaluateAsync<int>("() => window.__heatDraws"));
        await page.EvaluateAsync("""
            () => window.powatchHeat.draw(document.getElementById('sensory-heat'),
                Array.from({length:144}, (_,i) => i === 90 ? 1 : 0), 16, 9)
            """);
        await page.WaitForFunctionAsync("before => window.__heatDraws > before", before);
        await page.EvaluateAsync("""
            () => { const canvas = document.getElementById('sensory-heat'); window.powatchHeat.release(canvas); canvas.remove(); }
            """);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Procedural_worklet_registers_and_cue_tails_finish()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        await page.EvaluateAsync("""
            () => {
                window.__workletNodes = 0;
                const Worklet = window.AudioWorkletNode;
                window.AudioWorkletNode = class extends Worklet {
                    constructor(...args) { super(...args); window.__workletNodes++; }
                };
                window.powatchCues.play('regular', -0.5, 0.6, 0.4, { seed: 'test', room: true });
            }
            """);
        await page.WaitForTimeoutAsync(400);
        await page.EvaluateAsync("() => window.powatchCues.play('start', 0.5, 0.2, 0.5, 'test2')");
        await page.WaitForFunctionAsync("() => window.__workletNodes > 0 && window.powatchCues.isPlaying()");
        await page.WaitForFunctionAsync("() => !window.powatchCues.isPlaying()");
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Seeking_back_to_a_notable_minute_reuses_the_same_particle_and_audio_seed()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        var seeds = await page.EvaluateAsync<string[]>("""
            () => {
                const root = document.createElement('div');
                root.style.cssText = 'position:fixed;top:80px;left:20px;width:320px;z-index:90';
                root.innerHTML = '<canvas data-replay="track"></canvas><input type="range" data-replay="scrub"><canvas data-replay="heat"></canvas>';
                document.body.append(root);
                const calls = [];
                const sound = window.powatchCues.play, burst = window.powatchFx.burst;
                window.powatchCues.play = (_,pan,depth,energy,options) => calls.push(options.seed);
                window.powatchFx.burst = (_,seed) => calls.push(seed);
                const from = '2026-01-01T10:00:00Z', minute = '2026-01-01T10:01:00Z';
                window.powatchReplay.mount(root, JSON.stringify({
                    window: { fromUtc: from, toUtc: '2026-01-01T10:02:00Z' },
                    minutes: [{atUtc:from,occupancy:0,peak:0,motion:0}, {atUtc:minute,occupancy:1,peak:1,motion:1}],
                    grids: btoa(String.fromCharCode(...new Uint8Array(288))),
                    events: [{kind:'moment',text:'Arrival',atUtc:minute}]
                }), '2026-01-01');
                const scrub = root.querySelector('input');
                for (const value of ['1','0','1']) { scrub.value = value; scrub.dispatchEvent(new Event('input')); }
                window.powatchReplay.unmount(root);
                root.remove();
                window.powatchCues.play = sound;
                window.powatchFx.burst = burst;
                return calls;
            }
            """);
        Assert.Equal(4, seeds.Length);
        Assert.All(seeds, seed => Assert.Equal(seeds[0], seed));
        await page.AssertNoBlazorErrorAsync();
    }
}

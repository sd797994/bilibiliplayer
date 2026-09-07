#if DEBUG && WINDOWS
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BiliBiliPlayer.Models;
using BiliBiliPlayer.Views;

namespace BiliBiliPlayer.Diagnostics;

// Opt-in integration test of the real MAUI page lifecycle and WebView2 media pipeline.
// Never compiled into Release, never reads or writes login/session data.
internal static class NativePlaybackSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static bool IsRequested => Environment.GetEnvironmentVariable("BILI_PLAYBACK_SMOKE") == "1";
    private static string LogPath => Environment.GetEnvironmentVariable("BILI_PLAYBACK_SMOKE_LOG")
        ?? Path.Combine(AppContext.BaseDirectory, "playback-smoke.jsonl");

    private static void Log(object value) => File.AppendAllText(LogPath,
        JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, value }) + Environment.NewLine);

    private static T? Field<T>(PlayerPage page, string name) =>
        (T?)typeof(PlayerPage).GetField(name, PrivateInstance)!.GetValue(page);

    private static Task Call(PlayerPage page, string name, params object?[] args) =>
        (Task)typeof(PlayerPage).GetMethod(name, PrivateInstance)!.Invoke(page, args)!;

    private static Task<string> Script(PlayerPage page, string script) =>
        (Task<string>)typeof(PlayerPage).GetMethod("ExecutePlayerScriptAsync", PrivateInstance)!
            .Invoke(page, [script])!;

    private static PlayerPage CreatePage(string bvid) => new(new VideoItem
    {
        Bvid = bvid,
        Title = $"Native playback smoke · {bvid}"
    });

    private static async Task<JsonElement> WaitForPlayback(PlayerPage page, string label, int expectedPage, int timeoutSeconds)
    {
        var timer = Stopwatch.StartNew();
        string previousStage = string.Empty;
        while (timer.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var ready = Field<bool>(page, "_playerReady");
            var resolving = Field<bool>(page, "_isResolving");
            var manifest = Field<PlaybackManifest>(page, "_playbackManifest");
            var native = Field<Microsoft.UI.Xaml.Controls.WebView2>(page, "_nativePlayerWebView");
            if (native?.CoreWebView2 is { } core) core.IsMuted = true;
            var loading = page.FindByName<Label>("LoadingLabel")?.Text;
            var stage = JsonSerializer.Serialize(new
            {
                label, ready, resolving,
                resolverCreated = Field<WebView>(page, "_resolverWebView") is not null,
                cid = manifest?.Cid, page = manifest?.PageNumber, loading
            });
            if (stage != previousStage) { Log(new { stage }); previousStage = stage; }
            if (ready && manifest is not null && !resolving)
            {
                var raw = await Script(page, """
                    JSON.stringify((() => {
                        const video = document.getElementById('video');
                        const quality = video.getVideoPlaybackQuality?.();
                        return {
                            ...window.biliLocalPlayer.state(),
                            readyState: video.readyState,
                            width: video.videoWidth,
                            frames: quality?.totalVideoFrames || video.webkitDecodedFrameCount || 0,
                            partsDisabled: document.getElementById('partToggle').disabled,
                            selectedParts: document.querySelectorAll('#partList .selected').length
                        };
                    })())
                    """).WaitAsync(TimeSpan.FromSeconds(6));
                var json = JsonSerializer.Deserialize<string>(raw) ?? raw;
                using var document = JsonDocument.Parse(json);
                var state = document.RootElement;
                if (state.GetProperty("page").GetInt32() == expectedPage &&
                    state.GetProperty("cid").GetInt64() == manifest.Cid &&
                    state.GetProperty("time").GetDouble() > 4 &&
                    state.GetProperty("width").GetInt32() > 0 &&
                    state.GetProperty("frames").GetInt32() > 0 &&
                    !state.GetProperty("paused").GetBoolean())
                {
                    Log(new { pass = label, state });
                    return state.Clone();
                }
            }
            if (!resolving && page.FindByName<Button>("RetryButton")?.IsVisible == true)
                throw new InvalidOperationException($"{label}: {loading}");
            await Task.Delay(500);
        }
        throw new TimeoutException($"{label}: playback did not start within {timeoutSeconds}s. Last stage: {previousStage}");
    }

    internal static async Task RunAsync(MainPage owner)
    {
        var passed = false;
        try
        {
            Log(new { start = "native MAUI/WebView2 playback", process = Environment.ProcessId });
            var reproduceOnly = Environment.GetEnvironmentVariable("BILI_PLAYBACK_SMOKE_REPRO") == "1";
            var single = CreatePage("BV1wr8S6WEX7");
            await owner.Navigation.PushModalAsync(single, animated: false);
            var state = await WaitForPlayback(single, "single P startup", 1, reproduceOnly ? 25 : 90);
            if (!state.GetProperty("partsDisabled").GetBoolean()) throw new Exception("Single P button must be disabled");
            await Call(single, "CloseAsync").WaitAsync(TimeSpan.FromSeconds(10));

            var multi = CreatePage("BV11Xh36sEuY");
            await owner.Navigation.PushModalAsync(multi, animated: false);
            state = await WaitForPlayback(multi, "multi P startup", 1, 90);
            if (state.GetProperty("partsDisabled").GetBoolean()) throw new Exception("Multi P button must be enabled");
            var firstCid = state.GetProperty("cid").GetInt64();
            // Exercise the real HTML -> WebMessageReceived -> native resolver route.
            await Script(multi, "document.getElementById('partToggle').click(); document.querySelectorAll('#partList button')[1].click(); 'ok'");
            state = await WaitForPlayback(multi, "switch to P2", 2, 90);
            if (state.GetProperty("cid").GetInt64() == firstCid) throw new Exception("P2 reused P1 cid");
            var secondCid = state.GetProperty("cid").GetInt64();
            await Call(multi, "ResolveAndPlayAsync", 64, true, false, null);
            state = await WaitForPlayback(multi, "quality switch stays on P2", 2, 45);
            if (state.GetProperty("cid").GetInt64() != secondCid) throw new Exception("Quality switch changed cid");
            await Call(multi, "CloseAsync").WaitAsync(TimeSpan.FromSeconds(10));

            var resumed = CreatePage("BV11Xh36sEuY");
            await owner.Navigation.PushModalAsync(resumed, animated: false);
            state = await WaitForPlayback(resumed, "reopen resumes P2", 2, 90);
            if (state.GetProperty("cid").GetInt64() != secondCid) throw new Exception("Reopen changed cid");
            await Call(resumed, "CloseAsync").WaitAsync(TimeSpan.FromSeconds(10));
            passed = true;
            Log(new { result = "PASS" });
        }
        catch (Exception error)
        {
            Log(new { result = "FAIL", error = error.ToString() });
        }
        finally
        {
            Environment.Exit(passed ? 0 : 1);
        }
    }
}
#endif

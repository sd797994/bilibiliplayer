using BiliBiliPlayer.Services;

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{message}: expected {expected}, got {actual}");
}

const string bvid = "BV_PARTS";
Equal(1, PlaybackProgressStore.GetPageNumber(bvid), "Default P");
PlaybackProgressStore.SavePosition(bvid, 11, 1, 120, 1558, false);
Equal(0d, PlaybackProgressStore.GetPosition(bvid, 22), "P2 starts at zero");
PlaybackProgressStore.SavePosition(bvid, 22, 2, 240, 1606, false);
Equal(120d, PlaybackProgressStore.GetPosition(bvid, 11), "Preserve P1");
Equal(240d, PlaybackProgressStore.GetPosition(bvid, 22), "Preserve P2");
Equal(2, PlaybackProgressStore.GetPageNumber(bvid), "Reopen last P");
Equal(0d, PlaybackProgressStore.GetPosition("BV_OTHER", 22), "Isolate different BVs");

PlaybackProgressStore.SavePosition(bvid, 22, 2, 0, 0, false);
Equal(240d, PlaybackProgressStore.GetPosition(bvid, 22), "Closing during load keeps checkpoint");
PlaybackProgressStore.SavePosition(bvid, 22, 2, double.NaN, 1606, false);
Equal(240d, PlaybackProgressStore.GetPosition(bvid, 22), "Invalid time keeps checkpoint");
PlaybackProgressStore.SavePosition(bvid, 0, 9, 10, 100, false);
Equal(2, PlaybackProgressStore.GetPageNumber(bvid), "Unresolved cid cannot change remembered P");

PlaybackProgressStore.SavePosition(bvid, 22, 2, 1600, 1606, false);
Equal(0d, PlaybackProgressStore.GetPosition(bvid, 22), "Near end clears only P2");
Equal(120d, PlaybackProgressStore.GetPosition(bvid, 11), "Completed P2 does not clear P1");
PlaybackProgressStore.SavePosition(bvid, 11, 1, 2, 1558, false);
Equal(0d, PlaybackProgressStore.GetPosition(bvid, 11), "Restart clears P1 checkpoint");
PlaybackProgressStore.SavePosition(bvid, 11, 1, 120, 1558, false);
PlaybackProgressStore.SavePosition(bvid, 11, 1, 120, 1558, true);
Equal(0d, PlaybackProgressStore.GetPosition(bvid, 11), "Ended clears checkpoint");
Console.WriteLine("PASS playback progress: per-cid resume, last P, loading/invalid state, completion and restart");

var scriptCalls = 0;
var neverCompletes = new TaskCompletionSource<string>();
Task<string> UnattachedWebViewScript()
{
    scriptCalls++;
    return neverCompletes.Task;
}
Equal(string.Empty, await PlayerScriptExecutor.ExecuteAsync(false, UnattachedWebViewScript), "Skip unattached WebView");
Equal(0, scriptCalls, "Never enqueue a script before readiness");
Equal("ok", await PlayerScriptExecutor.ExecuteAsync(true, () => Task.FromResult("ok")), "Execute ready player command");
Equal(string.Empty, await PlayerScriptExecutor.ExecuteAsync(true, UnattachedWebViewScript,
    timeout: TimeSpan.FromMilliseconds(20)), "Bound a hung script");
Equal(string.Empty, await PlayerScriptExecutor.ExecuteAsync(true,
    () => throw new InvalidOperationException("WebView disposed")), "Closing WebView remains safe");
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
var callsBeforeCancellation = scriptCalls;
Equal(string.Empty, await PlayerScriptExecutor.ExecuteAsync(true, UnattachedWebViewScript,
    cancellation.Token), "Cancelled page does not queue work");
Equal(callsBeforeCancellation, scriptCalls, "No script queued after cancellation");
using var pendingCancellation = new CancellationTokenSource();
var pending = PlayerScriptExecutor.ExecuteAsync(true, UnattachedWebViewScript, pendingCancellation.Token);
pendingCancellation.Cancel();
Equal(string.Empty, await pending.WaitAsync(TimeSpan.FromSeconds(1)), "Closing cancels a pending command");
Console.WriteLine("PASS player startup: no script before readiness, ready execution, timeout, errors and cancellation");

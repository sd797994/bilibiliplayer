namespace BiliBiliPlayer.Services;

/// <summary>
/// Optional player commands must not queue against an unattached WebView or wait forever.
/// In particular, MAUI cannot complete EvaluateJavaScriptAsync invoked before its handler exists.
/// </summary>
public static class PlayerScriptExecutor
{
    public static async Task<string> ExecuteAsync(
        bool isReady,
        Func<Task<string>> execute,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (!isReady || cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }

        try
        {
            return await execute().WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken)
                ?? string.Empty;
        }
        catch
        {
            // Callers that require a result (e.g. loading media) detect the empty response
            // and show Retry. Optional controls and closing the page remain responsive.
            return string.Empty;
        }
    }
}

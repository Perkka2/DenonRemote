using System.Collections.Concurrent;

namespace DenonRemote.Services;

/// <summary>
/// Keeps the app from talking over the receiver.
///
/// The HTTP server inside these units is small and gives up easily. A sweep of a
/// hundred ordinary GETs, one after another as fast as they will go, produced two
/// timeouts and then twenty-five refused connections in a row: it had stopped
/// accepting anything at all. Nothing was wrong with the requests - the settings
/// after that point simply looked, from the app, like settings the receiver did
/// not have.
///
/// So: one request at a time per receiver, a breath between them, and a pause and
/// retry when it does drop the connection, because it comes back on its own.
/// </summary>
public static class ReceiverGate
{
    /// <summary>Long enough that the receiver keeps up; short enough not to feel slow.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(120);

    /// <summary>It needs a good deal longer than the gap once it has actually fallen over.</summary>
    private static readonly TimeSpan Recovery = TimeSpan.FromMilliseconds(750);

    private const int Attempts = 3;

    private sealed class Lane
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public DateTimeOffset Last;
    }

    private static readonly ConcurrentDictionary<string, Lane> Lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one call against a receiver, in its turn, retrying while
    /// <paramref name="transient"/> says the answer was the receiver buckling rather
    /// than an answer.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        string host, Func<Task<T>> call, Func<T, bool> transient, CancellationToken ct)
    {
        var lane = Lanes.GetOrAdd(host, _ => new Lane());
        await lane.Turn.WaitAsync(ct);
        try
        {
            var result = default(T)!;

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var since = DateTimeOffset.UtcNow - lane.Last;
                var wait = attempt == 1 ? Gap - since : Recovery * attempt;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                result = await call();
                lane.Last = DateTimeOffset.UtcNow;

                if (!transient(result)) return result;
            }

            return result;
        }
        finally
        {
            lane.Turn.Release();
        }
    }
}

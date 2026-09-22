using System.Collections.Concurrent;

namespace DenonRemote.Services;

/// <summary>How hard a caller is allowed to lean on the receiver.</summary>
public enum ReceiverPace
{
    /// <summary>A person is waiting for this - a menu opening, a setting changing.</summary>
    Interactive,

    /// <summary>A sweep or a crawl. Nobody is watching each request, so take it slowly.</summary>
    Bulk,
}

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
/// So: one request at a time per receiver, a breath between them, a longer breath
/// for bulk work nobody is waiting on, and - the part that matters most - once a
/// receiver has dropped something, a slow lane for the next half minute rather
/// than straight back to full speed. It does not recover between one request and
/// the next; it recovers over about that long.
/// </summary>
public static class ReceiverGate
{
    /// <summary>Between requests a person is waiting for.</summary>
    private static readonly TimeSpan InteractiveGap = TimeSpan.FromMilliseconds(250);

    /// <summary>Between requests in a sweep or a crawl.</summary>
    private static readonly TimeSpan BulkGap = TimeSpan.FromMilliseconds(500);

    /// <summary>Between any two requests once the receiver has shown strain.</summary>
    private static readonly TimeSpan StrainedGap = TimeSpan.FromMilliseconds(1_500);

    /// <summary>
    /// How long one dropped request keeps the lane slow. The failure was never a
    /// single refusal - it was twenty-five in a row - so easing off for one request
    /// and resuming would walk straight back into it.
    /// </summary>
    private static readonly TimeSpan StrainedFor = TimeSpan.FromSeconds(30);

    /// <summary>The first pause after a drop; each further attempt doubles it.</summary>
    private static readonly TimeSpan FirstRecovery = TimeSpan.FromSeconds(2);

    private const int Attempts = 4;

    private sealed class Lane
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public DateTimeOffset Last;
        public DateTimeOffset Strained;

        /// <summary>
        /// The endpoints on this receiver that have ever given a real answer.
        ///
        /// Per port, not per host: the pacing is shared across the whole box, because
        /// one small server is behind all of it, but "nothing is listening" is a fact
        /// about a port. Sharing that too meant a successful read on 10443 made every
        /// refusal on port 80 - which simply has no server on some firmware - look
        /// like a receiver falling over, at fourteen seconds of backoff apiece.
        /// </summary>
        public readonly HashSet<string> Answered = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, Lane> Lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How the gate waits. Only the self-test replaces it, so that it can watch the
    /// real pacing decisions go by instead of sitting through fourteen seconds of
    /// backoff - and so the thing being checked is this code rather than a second
    /// copy of the rules written out in the test.
    /// </summary>
    internal static Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    /// <summary>
    /// Runs one call against a receiver, in its turn, retrying while
    /// <paramref name="transient"/> says the answer was the receiver buckling rather
    /// than an answer.
    /// </summary>
    /// <param name="endpoint">
    /// The receiver, as host or host:port. The port decides whether a refusal means
    /// "nothing is listening"; the host decides which queue the request waits in.
    /// </param>
    public static async Task<T> RunAsync<T>(
        string endpoint, Func<Task<T>> call, Func<T, bool> transient, CancellationToken ct,
        ReceiverPace pace = ReceiverPace.Interactive)
    {
        var colon = endpoint.LastIndexOf(':');
        var host = colon > 0 ? endpoint[..colon] : endpoint;
        var lane = Lanes.GetOrAdd(host, _ => new Lane());
        await lane.Turn.WaitAsync(ct);
        try
        {
            var result = default(T)!;

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var wait = attempt == 1
                    ? Gap(lane, pace) - (DateTimeOffset.UtcNow - lane.Last)
                    // 2s, 4s, 8s.
                    : FirstRecovery * (1 << (attempt - 2));

                if (wait > TimeSpan.Zero) await Sleep(wait, ct);

                result = await call();
                lane.Last = DateTimeOffset.UtcNow;

                if (!transient(result))
                {
                    lock (lane.Answered) lane.Answered.Add(endpoint);
                    return result;
                }

                // An endpoint that has never answered is not a receiver under strain -
                // it is a port with nothing behind it. The pre-HEOS API is simply
                // absent on some firmware, and backing off fourteen seconds per
                // request to establish that turned a sweep into a five-minute wait.
                bool known;
                lock (lane.Answered) known = lane.Answered.Contains(endpoint);
                if (!known) return result;

                // It dropped this one. Everything on this receiver goes slowly for a
                // while, not just the retries of this request.
                lane.Strained = lane.Last;
            }

            return result;
        }
        finally
        {
            lane.Turn.Release();
        }
    }

    private static TimeSpan Gap(Lane lane, ReceiverPace pace) =>
        DateTimeOffset.UtcNow - lane.Strained < StrainedFor ? StrainedGap
        : pace == ReceiverPace.Bulk ? BulkGap
        : InteractiveGap;

    internal static int MaxAttempts => Attempts;
}

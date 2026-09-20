using System;
using System.Threading;
using System.Threading.Tasks;
using PlayFlow.Nakama.Fleet.Protocol;

namespace PlayFlow.Nakama.Fleet.Client
{
    public sealed class AssignmentException : Exception
    {
        public string State { get; }
        public AssignmentException(string state) : base("Fleet assignment ended with state: " + state) { State = state; }
    }

    /// <summary>
    /// SDK-independent assignment poller. The delegate uses the existing authenticated Nakama client/session.
    /// Notifications can trigger FetchAsync sooner; the server remains the authority for allocation ownership.
    /// </summary>
    public sealed class AssignmentClient
    {
        private readonly Func<CancellationToken, Task<string>> _fetchJson;
        private readonly Func<long> _clock;
        private readonly string _expectedBuildHash;

        public AssignmentClient(Func<CancellationToken, Task<string>> fetchJson, string expectedBuildHash,
            Func<long> clock = null)
        {
            _fetchJson = fetchJson ?? throw new ArgumentNullException(nameof(fetchJson));
            if (string.IsNullOrWhiteSpace(expectedBuildHash)) throw new ArgumentException("Build hash is required.");
            _expectedBuildHash = expectedBuildHash;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public async Task<Assignment> FetchAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await AwaitWithCancellation(_fetchJson(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var assignment = FleetProtocol.Deserialize<Assignment>(json, 16384);
            if (assignment.SchemaVersion != FleetProtocol.SchemaVersion || assignment.Revision < 0 ||
                string.IsNullOrEmpty(assignment.AllocationId) || string.IsNullOrEmpty(assignment.State))
                throw new FormatException("Invalid fleet assignment envelope.");
            return assignment;
        }

        public async Task<Assignment> WaitUntilAssignedAsync(TimeSpan timeout, TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            var interval = pollInterval ?? TimeSpan.FromSeconds(1);
            if (interval < TimeSpan.FromMilliseconds(200) || interval > TimeSpan.FromSeconds(10))
                throw new ArgumentOutOfRangeException(nameof(pollInterval));
            var tracker = new AssignmentTracker();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    while (true)
                    {
                        var assignment = await FetchAsync(deadline.Token).ConfigureAwait(false);
                        if (tracker.TryAccept(assignment))
                        {
                            if (IsTerminal(assignment.State)) throw new AssignmentException(assignment.State);
                            if (IsAssignedState(assignment.State))
                            {
                                ValidateForConnection(assignment, _expectedBuildHash, _clock());
                                return assignment;
                            }
                        }
                        await Task.Delay(interval, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for a fleet assignment. Query or cancel the allocation before retrying matchmaking.");
                }
            }
        }

        // A client validates public routing data, never the HMAC. Admission keys belong only on the server.
        public static void ValidateForConnection(Assignment assignment, string expectedBuildHash, long now)
        {
            if (assignment == null || assignment.SchemaVersion != FleetProtocol.SchemaVersion ||
                !IsAssignedState(assignment.State) || string.IsNullOrEmpty(assignment.AllocationId) ||
                string.IsNullOrEmpty(assignment.WorkerId) || string.IsNullOrEmpty(assignment.BootId) ||
                string.IsNullOrEmpty(assignment.RoomId) || assignment.Seat < 0 || assignment.Seat > 255 ||
                assignment.Endpoint == null || string.IsNullOrWhiteSpace(assignment.Endpoint.Host) ||
                assignment.Endpoint.Port < 1 || assignment.Endpoint.Port > 65535 ||
                string.IsNullOrEmpty(assignment.Endpoint.Transport) ||
                string.IsNullOrEmpty(assignment.AdmissionToken) || assignment.AdmissionToken.Length > 8192 ||
                assignment.ExpiresAt <= now || assignment.ReconnectSeconds < 0)
                throw new FormatException("Assignment does not contain valid connection information.");
            if (!string.Equals(assignment.BuildHash, expectedBuildHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The assignment requires a different game build.");
        }

        public static bool IsAssignedState(string state) => state == "assigned" || state == "active";
        public static bool IsTerminal(string state) => state == "failed" || state == "cancelled" ||
            state == "expired" || state == "completed";

        private static async Task<string> AwaitWithCancellation(Task<string> request, CancellationToken token)
        {
            if (request == null) throw new InvalidOperationException("Assignment fetch delegate returned no task.");
            if (!token.CanBeCanceled || request.IsCompleted) return await request.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(request, cancelled.Task).ConfigureAwait(false) == request)
                    return await request.ConfigureAwait(false);
                // Some SDK versions cannot abort the HTTP call. Observe a late fault without blocking cancellation.
                _ = request.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new OperationCanceledException(token);
            }
        }
    }

    /// <summary>One search/allocation at a time. Create a new tracker when the user starts a new search.</summary>
    public sealed class AssignmentTracker
    {
        public Assignment Current { get; private set; }

        public bool TryAccept(Assignment assignment)
        {
            if (assignment == null) throw new ArgumentNullException(nameof(assignment));
            if (Current != null)
            {
                if (!string.Equals(Current.AllocationId, assignment.AllocationId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Allocation changed during the same search. Resolve the previous allocation first.");
                if (assignment.Revision <= Current.Revision) return false;
            }
            Current = assignment;
            return true;
        }
    }
}

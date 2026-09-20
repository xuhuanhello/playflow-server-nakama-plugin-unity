using PlayFlow.Nakama.Fleet.Protocol;

namespace PlayFlow.Nakama.Fleet.Server
{
    /// <summary>
    /// All calls occur on Unity's main thread. Implement using preloaded room resources; return
    /// success only once the requested state is real. Never block this thread for network/disk IO.
    /// </summary>
    public interface IFleetServerHost
    {
        // True only after the transport is listening, room resources are ready, and result storage is available.
        bool IsReady { get; }
        RoomSnapshot[] CaptureRooms();
        FleetMetrics CaptureMetrics();

        // Release retained "closed" observations only after this ACK. The request is the exact accepted snapshot.
        // Keep a room in closing while result/audit records still lack durable ownership outside this process.
        void OnHeartbeatAcknowledged(HeartbeatRequest request);

        // Idempotent by room/allocation/epoch. Refuse an existing room owned by another allocation or epoch.
        HostCommandResult PrepareRoom(FleetCommand command);

        // Must fence allocation+epoch so a late cancel cannot remove a replacement room. Duplicate is success.
        // Return success only after the room is gone and results/replay data have durable ownership.
        HostCommandResult CancelRoom(FleetCommand command);

        // Sticky for the lifetime of this process. Stop new rooms/rounds; active games may finish.
        // This signals the start of drain, not that the process is safe to stop.
        HostCommandResult BeginDrain();

        // Check local room ownership, epoch, roster[seat], reservation, expiry, reconnect/replacement policy.
        // This method only checks; attach the connection immediately after TryAuthorizeAdmission succeeds.
        bool CanAdmit(AdmissionClaims claims, out string error);
    }

    public readonly struct HostCommandResult
    {
        public bool Success { get; }
        public string Error { get; }
        private HostCommandResult(bool success, string error) { Success = success; Error = error; }
        public static HostCommandResult Ok() => new HostCommandResult(true, null);
        // Use short stable error codes, never exceptions, tokens, user-provided strings or secrets.
        public static HostCommandResult Failed(string code) => new HostCommandResult(false, code);
    }
}

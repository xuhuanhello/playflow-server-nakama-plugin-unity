using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading;
using PlayFlow.Nakama.Fleet.Core;
using PlayFlow.Nakama.Fleet.Protocol;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayFlow.Nakama.Fleet.Server
{
    [DisallowMultipleComponent]
    public sealed class FleetServerAgent : MonoBehaviour
    {
        [Tooltip("Component implementing IFleetServerHost. Credentials are read from the server environment.")]
        [SerializeField] private MonoBehaviour hostComponent = null;
        [Tooltip("Leave off in client scenes. StartAgent can also be called explicitly by the server bootstrap.")]
        [SerializeField] private bool startAutomatically = false;
        [Tooltip("Allows HTTP only for a loopback URL in local development.")]
        [SerializeField] private bool allowInsecureLoopback = false;

        private FleetAgentOptions _options;
        private IFleetServerHost _host;
        private AdmissionTokenVerifier _verifier;
        private AdmissionReplayGuard _replayGuard;
        private CommandLedger _ledger;
        private RoomCancellationGuard _cancelledRooms;
        private UnityWebRequest _inFlight;
        private Coroutine _loop;
        private string _agentToken;
        private long _sequence;
        private long _revision = -1;
        private int _mainThreadId;
        private int _heartbeatInterval = 2;
        private double _lastSuccess = double.NegativeInfinity;
        private bool _running;
        private bool _everStarted;
        private bool _faulted;
        private bool _drainDelivered;
        private double _lastWarning = double.NegativeInfinity;
        private readonly System.Random _jitter = new System.Random();

        public string WorkerId => _options?.WorkerId;
        public string BootId { get; private set; }
        public bool IsDraining { get; private set; }
        public bool IsControlHealthy => _running && !_faulted && _agentToken != null &&
            Time.realtimeSinceStartupAsDouble - _lastSuccess <= _options.ControlStaleAfterSeconds;
        public long ControlRevision => _revision;

        private void Start()
        {
            if (!startAutomatically) return;
            if (!(hostComponent is IFleetServerHost host))
            {
                Debug.LogError("Fleet agent requires an IFleetServerHost adapter.", this);
                return;
            }
            try { StartAgent(host, FleetAgentOptions.FromEnvironment(allowInsecureLoopback)); }
            catch (Exception)
            {
                // Configuration errors must not accidentally expose environment secrets.
                Debug.LogError("Fleet agent configuration is invalid. Check required server environment variables and limits.", this);
            }
        }

        public void StartAgent(IFleetServerHost host, FleetAgentOptions options)
        {
            if (host == null || options == null) throw new ArgumentNullException(host == null ? nameof(host) : nameof(options));
            if (_everStarted) throw new InvalidOperationException("An agent cannot be restarted in the same component. Restart the worker process.");
            if (!isActiveAndEnabled) throw new InvalidOperationException("The fleet agent component must be enabled.");
            options.Validate();
            options = options.Snapshot();
            _options = options;
            _host = host;
            BootId = Guid.NewGuid().ToString("D");
            _verifier = new AdmissionTokenVerifier(options.AdmissionKey, options.WorkerId, BootId,
                maximumLifetimeSeconds: options.MaximumAdmissionLifetimeSeconds);
            _replayGuard = new AdmissionReplayGuard(options.NonceCapacity);
            _ledger = new CommandLedger(options.CommandLedgerCapacity);
            _cancelledRooms = new RoomCancellationGuard(options.CancellationFenceCapacity,
                Math.Max(options.MaximumPreparationLifetimeSeconds, options.MaximumAdmissionLifetimeSeconds));
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _running = true;
            _everStarted = true;
            _loop = StartCoroutine(Run());
        }

        // Call once per transport handshake, then bind the authenticated claims to that connection.
        // A duplicate network handshake should reuse its already-bound identity, not consume the nonce twice.
        public bool TryAuthorizeAdmission(string token, out AdmissionClaims claims, out string error)
        {
            claims = null;
            error = "agent_unavailable";
            if (!_running || _faulted || Thread.CurrentThread.ManagedThreadId != _mainThreadId) return false;
            if (!IsControlHealthy) { error = "control_unavailable"; return false; }
            if (!_verifier.TryValidate(token, out var parsed, out error)) return false;
            if (_cancelledRooms.IsCancelled(parsed.RoomId, parsed.AllocationId, parsed.Epoch))
            {
                error = "room_cancelled";
                return false;
            }
            try
            {
                if (!_host.IsReady || !_host.CanAdmit(parsed, out error))
                {
                    error = SafeError(error, "room_unavailable");
                    return false;
                }
            }
            catch (Exception) { error = "host_unavailable"; return false; }
            if (!_replayGuard.TryConsume(parsed, out error)) return false;
            claims = parsed;
            error = null;
            return true;
        }

        // Stops control IO only. It does not terminate the host, delete rooms, or claim a successful drain.
        public void StopAgent()
        {
            _running = false;
            _inFlight?.Abort();
            if (_loop != null) StopCoroutine(_loop);
            _inFlight?.Dispose();
            _inFlight = null;
            _loop = null;
            _agentToken = null;
            _verifier?.Dispose();
        }

        private void OnDisable() { StopAgent(); }
        private void OnDestroy() { StopAgent(); }

        private IEnumerator Run()
        {
            var backoff = 1;
            var bootstrapBody = FleetProtocol.Serialize(new BootstrapRequest
            {
                WorkerId = _options.WorkerId, BootstrapToken = _options.BootstrapToken,
                BootId = BootId, BuildHash = _options.BuildHash
            });
            while (_running && _agentToken == null && !_faulted)
            {
                HttpResult result = null;
                var bootstrapStartedAt = Time.realtimeSinceStartupAsDouble;
                yield return Post(FleetProtocol.BootstrapPath, bootstrapBody, null, value => result = value);
                if (!_running) yield break;
                if (result != null && result.Success)
                {
                    BootstrapResponse response = null;
                    try { response = FleetProtocol.Deserialize<BootstrapResponse>(result.Body); }
                    catch (Exception) { Fault("invalid_bootstrap_response"); }
                    if (response != null)
                    {
                        if (string.IsNullOrEmpty(response.AgentToken) || response.AgentToken.Length > 4096 ||
                            response.HeartbeatIntervalSeconds < 1 || response.HeartbeatIntervalSeconds > 30 ||
                            response.AgentToken.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                            Fault("invalid_bootstrap_response");
                        else
                        {
                            _agentToken = response.AgentToken;
                            _heartbeatInterval = response.HeartbeatIntervalSeconds;
                            _lastSuccess = bootstrapStartedAt;
                            break;
                        }
                    }
                }
                else if (result != null && IsPermanent(result.Status)) { Fault("bootstrap_rejected"); }
                if (_faulted) yield break;
                Warn("Fleet bootstrap retry pending.");
                yield return RetryDelay(backoff);
                backoff = Math.Min(backoff * 2, 30);
            }

            HeartbeatRequest pending = null;
            string pendingBody = null;
            double pendingStartedAt = double.NegativeInfinity;
            backoff = 1;
            while (_running && !_faulted)
            {
                if (pending == null)
                {
                    if (!TryCaptureHeartbeat(out pending, out pendingBody))
                    {
                        Warn("Fleet host snapshot is unavailable; new admissions will stop if control becomes stale.");
                        yield return RetryDelay(backoff);
                        backoff = Math.Min(backoff * 2, 30);
                        continue;
                    }
                    pendingStartedAt = Time.realtimeSinceStartupAsDouble;
                }
                HttpResult result = null;
                // Replay the exact same sequence and body until durably acknowledged.
                yield return Post(FleetProtocol.HeartbeatPath, pendingBody, _agentToken, value => result = value);
                if (!_running) yield break;
                if (result != null && result.Success)
                {
                    HeartbeatResponse response = null;
                    try { response = FleetProtocol.Deserialize<HeartbeatResponse>(result.Body); }
                    catch (Exception) { Fault("invalid_heartbeat_response"); }
                    if (_faulted) yield break;
                    if (response == null || response.Commands == null || response.Commands.Length > _options.MaximumCommandBatch ||
                        response.Revision < 0 || response.Revision < _revision)
                    {
                        Fault("invalid_heartbeat_revision_or_batch");
                        yield break;
                    }
                    _ledger.Acknowledge(pending.CommandResults);
                    try { _host.OnHeartbeatAcknowledged(pending); }
                    catch (Exception)
                    {
                        Fault("host_heartbeat_ack_failed");
                    }
                    if (_faulted) yield break;
                    _revision = response.Revision;
                    // The controller does not refresh heartbeat age for a duplicate sequence.
                    // A delayed/lost response must not extend admission freshness on retry.
                    _lastSuccess = pendingStartedAt;
                    if (response.Draining) IsDraining = true; // Never clear a previously observed drain.
                    if (IsDraining && !_drainDelivered) DeliverDrain();
                    foreach (var command in response.Commands)
                    {
                        ProcessCommand(command);
                        if (_faulted) yield break;
                    }
                    pending = null;
                    pendingBody = null;
                    backoff = 1;
                    yield return new WaitForSecondsRealtime(_heartbeatInterval);
                }
                else
                {
                    if (result != null && IsPermanent(result.Status))
                    {
                        Fault("heartbeat_rejected");
                        yield break;
                    }
                    Warn("Fleet control connection retry pending; existing games remain with their host.");
                    yield return RetryDelay(backoff);
                    backoff = Math.Min(backoff * 2, 30);
                }
            }
        }

        private bool TryCaptureHeartbeat(out HeartbeatRequest heartbeat, out string body)
        {
            heartbeat = null;
            body = null;
            try
            {
                var rooms = _host.CaptureRooms();
                var metrics = _host.CaptureMetrics();
                if (rooms == null || rooms.Length > _options.MaximumSnapshotRooms || metrics == null ||
                    rooms.Any(room => room == null || !AdmissionTokenVerifier.ValidIdentity(room.RoomId) ||
                        string.IsNullOrEmpty(room.State) || room.State.Length > 64 || room.UserIds == null ||
                        room.UserIds.Length > 256 || room.UserIds.Any(user => !AdmissionTokenVerifier.ValidIdentity(user))) ||
                    rooms.Select(room => room.RoomId).Distinct(StringComparer.Ordinal).Count() != rooms.Length ||
                    rooms.SelectMany(room => room.UserIds).Distinct(StringComparer.Ordinal).Count() != rooms.Sum(room => room.UserIds.Length) ||
                    metrics.SimulationPending < 0 || metrics.SimulationActive < 0 || metrics.MemoryBytes < 0 ||
                    metrics.AuditPending < 0 || metrics.AuditActive < 0 || metrics.PendingResults < 0 ||
                    !ValidMetric(metrics.SimulationOldestSeconds) || !ValidMetric(metrics.FrameP99Ms))
                    return false;
                heartbeat = new HeartbeatRequest
                {
                    WorkerId = _options.WorkerId, BootId = BootId, Sequence = checked(_sequence + 1),
                    Ready = _host.IsReady && !IsDraining && _ledger.HasCapacity(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    Rooms = rooms.Select(room => new RoomSnapshot
                    {
                        RoomId = room.RoomId, State = room.State, UserIds = (string[])room.UserIds.Clone()
                    }).ToArray(),
                    Metrics = metrics.Snapshot(),
                    CommandResults = _ledger.PendingResults(_options.MaximumCommandBatch)
                };
                body = FleetProtocol.Serialize(heartbeat);
                if (Encoding.UTF8.GetByteCount(body) > _options.MaximumResponseBytes)
                {
                    heartbeat = null;
                    body = null;
                    return false;
                }
                _sequence = heartbeat.Sequence;
                return true;
            }
            catch (Exception)
            {
                heartbeat = null;
                body = null;
                return false;
            }
        }

        private void ProcessCommand(FleetCommand command)
        {
            if (command == null || !AdmissionTokenVerifier.ValidIdentity(command.CommandId))
            {
                Fault("invalid_command_identity");
                return;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var receipt = _ledger.TryBegin(command, now);
            if (receipt == CommandReceipt.Collision) { Fault("command_identity_collision"); return; }
            if (receipt == CommandReceipt.Duplicate) return;
            if (receipt == CommandReceipt.Full)
            {
                // The controller retains this unacknowledged command and redelivers it.
                Warn("Fleet command ledger is full; pending commands await a later heartbeat.");
                return;
            }
            var result = HostCommandResult.Failed("unsupported_command");
            try
            {
                switch (command.Type)
                {
                    case FleetProtocol.PrepareRoom:
                        if (!ValidRoomCommand(command) || command.UserIds == null || command.UserIds.Length < 1 ||
                            command.UserIds.Length > 256 || command.UserIds.Any(user => !AdmissionTokenVerifier.ValidIdentity(user)) ||
                            command.UserIds.Distinct(StringComparer.Ordinal).Count() != command.UserIds.Length)
                            result = HostCommandResult.Failed("invalid_room_command");
                        else if (command.ExpiresAt <= now) result = HostCommandResult.Failed("command_expired");
                        else if (command.ExpiresAt > now + _options.MaximumPreparationLifetimeSeconds)
                            result = HostCommandResult.Failed("prepare_lifetime_exceeded");
                        else if (_cancelledRooms.IsCancelled(command.RoomId, command.AllocationId, command.Epoch))
                            result = HostCommandResult.Failed("room_cancelled");
                        else if (IsDraining || !_host.IsReady) result = HostCommandResult.Failed("host_not_admitting");
                        else result = _host.PrepareRoom(command);
                        break;
                    case FleetProtocol.CancelRoom:
                        if (!ValidRoomCommand(command)) result = HostCommandResult.Failed("invalid_room_command");
                        else if (!_cancelledRooms.TryRecord(command, out var fenceError)) result = HostCommandResult.Failed(fenceError);
                        else result = _host.CancelRoom(command);
                        break;
                    case FleetProtocol.Drain:
                        IsDraining = true;
                        result = DeliverDrain();
                        break;
                }
            }
            catch (Exception) { result = HostCommandResult.Failed("host_exception"); }
            _ledger.Complete(command.CommandId, result.Success, SafeError(result.Error, "host_rejected"));
        }

        private HostCommandResult DeliverDrain()
        {
            if (_drainDelivered) return HostCommandResult.Ok();
            try
            {
                var result = _host.BeginDrain();
                if (result.Success) _drainDelivered = true;
                return result;
            }
            catch (Exception) { return HostCommandResult.Failed("host_exception"); }
        }

        private IEnumerator Post(string route, string body, string bearer, Action<HttpResult> completed)
        {
            using (var download = new BoundedDownloadHandler(_options.MaximumResponseBytes))
            using (var request = new UnityWebRequest(_options.Endpoint(route).AbsoluteUri, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = download;
                request.disposeDownloadHandlerOnDispose = false;
                request.timeout = _options.RequestTimeoutSeconds;
                request.redirectLimit = 0; // Never forward bootstrap or bearer credentials to a redirect target.
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                if (bearer != null) request.SetRequestHeader("Authorization", "Bearer " + bearer);
                _inFlight = request;
                yield return request.SendWebRequest();
                var result = new HttpResult { Status = request.responseCode };
                if (!download.LimitExceeded && request.result == UnityWebRequest.Result.Success)
                {
                    try { result.Body = download.Body; result.Success = true; }
                    catch (DecoderFallbackException) { result.Success = false; }
                }
                _inFlight = null;
                completed(result);
            }
        }

        private IEnumerator RetryDelay(int seconds)
        {
            yield return new WaitForSecondsRealtime((float)(seconds * (0.8 + _jitter.NextDouble() * 0.4)));
        }

        private void Fault(string code)
        {
            _faulted = true;
            Debug.LogError("Fleet agent stopped admitting: " + code + ". Existing games remain with their host.", this);
        }

        private void Warn(string text)
        {
            if (Time.realtimeSinceStartupAsDouble - _lastWarning < 30) return;
            _lastWarning = Time.realtimeSinceStartupAsDouble;
            Debug.LogWarning(text, this);
        }

        private static bool IsPermanent(long status) => status >= 400 && status < 500 && status != 408 && status != 429;
        private static bool ValidRoomCommand(FleetCommand command) => command.Epoch > 0 &&
            AdmissionTokenVerifier.ValidIdentity(command.RoomId) && AdmissionTokenVerifier.ValidIdentity(command.AllocationId);
        private static bool ValidMetric(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        private static string SafeError(string code, string fallback)
        {
            return !string.IsNullOrEmpty(code) && code.Length <= 64 &&
                code.All(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_') ? code : fallback;
        }

        private sealed class HttpResult
        {
            public bool Success;
            public long Status;
            public string Body;
        }
    }
}

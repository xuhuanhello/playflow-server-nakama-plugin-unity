using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayFlow.Nakama.Fleet.Protocol
{
    public static class FleetProtocol
    {
        public const int SchemaVersion = 1;
        public const string BootstrapPath = "/fleet/v1/agent/bootstrap";
        public const string HeartbeatPath = "/fleet/v1/agent/heartbeat";
        public const string PrepareRoom = "prepare_room";
        public const string CancelRoom = "cancel_room";
        public const string Drain = "drain";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 16,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, JsonSettings);
        }

        public static T Deserialize<T>(string json, int maxCharacters = 262144) where T : class
        {
            if (string.IsNullOrEmpty(json) || json.Length > maxCharacters)
                throw new JsonSerializationException("Invalid fleet response size.");
            // Reject ambiguous duplicate keys; ignore extra fields for forward compatibility.
            using (var reader = new JsonTextReader(new System.IO.StringReader(json)))
            {
                reader.MaxDepth = 16;
                reader.DateParseHandling = DateParseHandling.None;
                var value = JObject.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                if (reader.Read()) throw new JsonSerializationException("Trailing fleet response content.");
                return value.ToObject<T>(JsonSerializer.Create(JsonSettings))
                    ?? throw new JsonSerializationException("Empty fleet response.");
            }
        }
    }

    [Serializable]
    public sealed class BootstrapRequest
    {
        [JsonProperty("worker_id")] public string WorkerId;
        [JsonProperty("bootstrap_token")] public string BootstrapToken;
        [JsonProperty("boot_id")] public string BootId;
        [JsonProperty("build_hash")] public string BuildHash;
    }

    [Serializable]
    public sealed class BootstrapResponse
    {
        [JsonProperty("agent_token", Required = Required.Always)] public string AgentToken;
        [JsonProperty("heartbeat_interval_seconds", Required = Required.Always)] public int HeartbeatIntervalSeconds;
    }

    [Serializable]
    public sealed class HeartbeatRequest
    {
        [JsonProperty("worker_id")] public string WorkerId;
        [JsonProperty("boot_id")] public string BootId;
        [JsonProperty("sequence")] public long Sequence;
        [JsonProperty("ready")] public bool Ready;
        [JsonProperty("rooms")] public RoomSnapshot[] Rooms = Array.Empty<RoomSnapshot>();
        [JsonProperty("metrics")] public FleetMetrics Metrics = new FleetMetrics();
        [JsonProperty("command_results")] public CommandResult[] CommandResults = Array.Empty<CommandResult>();
    }

    [Serializable]
    public sealed class HeartbeatResponse
    {
        [JsonProperty("commands", Required = Required.Always)] public FleetCommand[] Commands = Array.Empty<FleetCommand>();
        [JsonProperty("draining", Required = Required.Always)] public bool Draining;
        [JsonProperty("revision", Required = Required.Always)] public long Revision;
    }

    [Serializable]
    public sealed class RoomSnapshot
    {
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("state")] public string State;
        // Actual attached players, not the room's reserved roster.
        [JsonProperty("user_ids")] public string[] UserIds = Array.Empty<string>();
    }

    [Serializable]
    public sealed class FleetMetrics
    {
        [JsonProperty("simulation_pending")] public int SimulationPending;
        [JsonProperty("simulation_active")] public int SimulationActive;
        [JsonProperty("simulation_oldest_seconds")] public double SimulationOldestSeconds;
        [JsonProperty("frame_p99_ms")] public double FrameP99Ms;
        [JsonProperty("audit_pending")] public int AuditPending;
        [JsonProperty("audit_active")] public int AuditActive;
        [JsonProperty("pending_results")] public int PendingResults;
        // Whole-process memory (RSS/cgroup as measured by the host), not GC.GetTotalMemory.
        [JsonProperty("memory_bytes")] public long MemoryBytes;
    }

    [Serializable]
    public sealed class FleetCommand
    {
        [JsonProperty("command_id")] public string CommandId;
        [JsonProperty("type")] public string Type;
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("allocation_id")] public string AllocationId;
        [JsonProperty("epoch")] public long Epoch;
        [JsonProperty("user_ids")] public string[] UserIds = Array.Empty<string>();
        [JsonProperty("expires_at")] public long ExpiresAt;
    }

    [Serializable]
    public sealed class CommandResult
    {
        [JsonProperty("command_id")] public string CommandId;
        [JsonProperty("success")] public bool Success;
        [JsonProperty("error")] public string Error;
    }

    [Serializable]
    public sealed class AdmissionClaims
    {
        [JsonProperty("schema_version", Required = Required.Always)] public int SchemaVersion;
        [JsonProperty("user_id", Required = Required.Always)] public string UserId;
        [JsonProperty("worker_id", Required = Required.Always)] public string WorkerId;
        [JsonProperty("boot_id", Required = Required.Always)] public string BootId;
        [JsonProperty("room_id", Required = Required.Always)] public string RoomId;
        [JsonProperty("allocation_id", Required = Required.Always)] public string AllocationId;
        [JsonProperty("reservation_id", Required = Required.Always)] public string ReservationId;
        [JsonProperty("seat", Required = Required.Always)] public int Seat;
        [JsonProperty("epoch", Required = Required.Always)] public long Epoch;
        [JsonProperty("exp", Required = Required.Always)] public long ExpiresAt;
        [JsonProperty("nonce", Required = Required.Always)] public string Nonce;
        [JsonProperty("resume", Required = Required.Always)] public bool Resume;
    }

    [Serializable]
    public sealed class Assignment
    {
        [JsonProperty("schema_version")] public int SchemaVersion;
        [JsonProperty("allocation_id")] public string AllocationId;
        [JsonProperty("revision")] public long Revision;
        [JsonProperty("state")] public string State;
        [JsonProperty("worker_id")] public string WorkerId;
        [JsonProperty("boot_id")] public string BootId;
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("seat")] public int Seat;
        [JsonProperty("endpoint")] public GameEndpoint Endpoint;
        [JsonProperty("build_hash")] public string BuildHash;
        [JsonProperty("admission_token")] public string AdmissionToken;
        [JsonProperty("expires_at")] public long ExpiresAt;
        [JsonProperty("reconnect_seconds")] public int ReconnectSeconds;
        [JsonProperty("error")] public string Error;
    }

    [Serializable]
    public sealed class GameEndpoint
    {
        [JsonProperty("host")] public string Host;
        [JsonProperty("port")] public int Port;
        [JsonProperty("transport")] public string Transport;
    }
}

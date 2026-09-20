using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PlayFlow.Nakama.Fleet.Protocol;
using PlayFlow.Nakama.Fleet.Core;
using PlayFlow.Nakama.Fleet.Server;
using UnityEngine;

namespace PlayFlow.Nakama.Fleet.Samples
{
    /// <summary>
    /// Protocol demo only. No FishNet transport, gameplay, durable results, or production capacity measurement.
    /// Replace this adapter with your real game host before publishing a server build.
    /// </summary>
    public sealed class MinimalServerHost : MonoBehaviour, IFleetServerHost
    {
        [SerializeField] private int maximumRooms = 4;
        [Tooltip("Enable only after the real network transport is listening. The sample has no transport.")]
        [SerializeField] private bool listening;
        private bool _draining;
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        private readonly RoomCancellationGuard _cancelledRooms = new RoomCancellationGuard();

        private sealed class Room
        {
            public string Id;
            public string AllocationId;
            public long Epoch;
            public string[] Roster;
            public long ExpiresAt;
            public string State = "waiting_players";
            public readonly Dictionary<int, string> Connected = new Dictionary<int, string>();
        }

        public bool IsReady => listening;
        public void SetTransportListening(bool value) { listening = value; }

        private void Update()
        {
            // Expire unused reservations locally. Real gameplay must separately preserve active/reconnecting seats.
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var room in _rooms.Values)
                if (room.State == "waiting_players" && room.Connected.Count == 0 && room.ExpiresAt <= now)
                    room.State = "closed";
        }

        public RoomSnapshot[] CaptureRooms() => _rooms.Values.Select(room => new RoomSnapshot
        {
            RoomId = room.Id, State = room.State, UserIds = room.Connected.Values.ToArray()
        }).ToArray();

        public FleetMetrics CaptureMetrics()
        {
            long memoryBytes;
            using (var process = Process.GetCurrentProcess()) memoryBytes = process.WorkingSet64;
            return new FleetMetrics { MemoryBytes = memoryBytes };
        }

        public void OnHeartbeatAcknowledged(HeartbeatRequest request)
        {
            foreach (var snapshot in request.Rooms)
                if (snapshot.State == "closed" && _rooms.TryGetValue(snapshot.RoomId, out var room) && room.State == "closed")
                    _rooms.Remove(snapshot.RoomId);
        }

        public HostCommandResult PrepareRoom(FleetCommand command)
        {
            if (_cancelledRooms.IsCancelled(command.RoomId, command.AllocationId, command.Epoch))
                return HostCommandResult.Failed("room_cancelled");
            if (_rooms.TryGetValue(command.RoomId, out var existing))
                return SameOwner(existing, command) && existing.State != "closed" &&
                    existing.Roster.SequenceEqual(command.UserIds) ? HostCommandResult.Ok() : HostCommandResult.Failed("room_conflict");
            if (_draining || !listening) return HostCommandResult.Failed("not_admitting");
            if (command.UserIds.Length != 2) return HostCommandResult.Failed("expected_two_players");
            if (_rooms.Count >= maximumRooms) return HostCommandResult.Failed("room_capacity_reached");
            if (command.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return HostCommandResult.Failed("command_expired");
            _rooms.Add(command.RoomId, new Room
            {
                Id = command.RoomId, AllocationId = command.AllocationId, Epoch = command.Epoch,
                // The preparation deadline precedes Join's separate seat lease (at most 60 seconds).
                Roster = (string[])command.UserIds.Clone(), ExpiresAt = command.ExpiresAt + 60
            });
            return HostCommandResult.Ok();
        }

        public HostCommandResult CancelRoom(FleetCommand command)
        {
            if (!_cancelledRooms.TryRecord(command, out var error)) return HostCommandResult.Failed(error);
            if (!_rooms.TryGetValue(command.RoomId, out var room)) return HostCommandResult.Ok();
            if (!SameOwner(room, command)) return HostCommandResult.Failed("stale_room_owner");
            // A real game may need to disconnect players and durably submit results before returning success.
            if (room.Connected.Count != 0) return HostCommandResult.Failed("room_busy");
            room.State = "closed";
            return HostCommandResult.Ok();
        }

        public HostCommandResult BeginDrain()
        {
            _draining = true;
            return HostCommandResult.Ok();
        }

        public bool CanAdmit(AdmissionClaims claims, out string error)
        {
            error = "room_unavailable";
            if (_cancelledRooms.IsCancelled(claims.RoomId, claims.AllocationId, claims.Epoch)) return false;
            if (!_rooms.TryGetValue(claims.RoomId, out var room) || room.State == "closed" || room.State == "closing" ||
                room.AllocationId != claims.AllocationId || room.Epoch != claims.Epoch ||
                claims.Seat < 0 || claims.Seat >= room.Roster.Length || room.Roster[claims.Seat] != claims.UserId)
                return false;
            // This sample deliberately has no reconnect/replacement policy. Implement it in the real host.
            if (claims.Resume) { error = "sample_reconnect_unsupported"; return false; }
            if (room.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { error = "reservation_expired"; return false; }
            if (room.Connected.ContainsKey(claims.Seat)) { error = "seat_already_connected"; return false; }
            error = null;
            return true;
        }

        // Invoke on the same main-thread handshake immediately after agent.TryAuthorizeAdmission returns true.
        public void AttachAuthorizedConnection(AdmissionClaims claims)
        {
            if (!CanAdmit(claims, out var error)) throw new InvalidOperationException(error);
            var room = _rooms[claims.RoomId];
            room.Connected.Add(claims.Seat, claims.UserId);
            if (room.Connected.Count == room.Roster.Length) room.State = "playing";
        }

        // No fake result submission: this sample cannot mark an active match completed.
        // The production host does that only after result/audit persistence is acknowledged.
        private static bool SameOwner(Room room, FleetCommand command) =>
            room.AllocationId == command.AllocationId && room.Epoch == command.Epoch;
    }
}

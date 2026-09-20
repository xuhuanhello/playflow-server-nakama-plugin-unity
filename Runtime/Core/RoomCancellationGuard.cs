using System;
using System.Collections.Generic;
using System.Linq;
using PlayFlow.Nakama.Fleet.Protocol;

namespace PlayFlow.Nakama.Fleet.Core
{
    /// <summary>
    /// Fences delayed preparation/admission after cancellation, including cancel-before-prepare delivery.
    /// Retention must cover the controller's maximum preparation and admission lifetime.
    /// </summary>
    public sealed class RoomCancellationGuard
    {
        private readonly Dictionary<(string Room, string Allocation, long Epoch), long> _cancelled =
            new Dictionary<(string Room, string Allocation, long Epoch), long>();
        private readonly int _capacity;
        private readonly int _retentionSeconds;
        private readonly Func<long> _clock;

        public RoomCancellationGuard(int capacity = 2048, int retentionSeconds = 120, Func<long> clock = null)
        {
            if (capacity < 1 || retentionSeconds < 1) throw new ArgumentOutOfRangeException();
            _capacity = capacity;
            _retentionSeconds = retentionSeconds;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        // Reserve the fence BEFORE side effects/ACK; if full, leave cleanup pending for a future retry.
        public bool TryRecord(FleetCommand command, out string error)
        {
            error = "invalid_room_command";
            if (command == null || command.Type != FleetProtocol.CancelRoom || command.Epoch < 1 ||
                !AdmissionTokenVerifier.ValidIdentity(command.RoomId) || !AdmissionTokenVerifier.ValidIdentity(command.AllocationId))
                return false;
            var now = _clock();
            var key = (command.RoomId, command.AllocationId, command.Epoch);
            if (!_cancelled.ContainsKey(key) && _cancelled.Count >= _capacity)
            {
                foreach (var expired in _cancelled.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                    _cancelled.Remove(expired);
                if (_cancelled.Count >= _capacity) { error = "cancellation_fence_full"; return false; }
            }
            _cancelled[key] = checked(now + _retentionSeconds);
            error = null;
            return true;
        }

        public bool IsCancelled(string roomId, string allocationId, long epoch)
        {
            return _cancelled.TryGetValue((roomId, allocationId, epoch), out var until) && until > _clock();
        }
    }
}

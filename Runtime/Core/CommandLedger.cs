using System;
using System.Collections.Generic;
using System.Linq;
using PlayFlow.Nakama.Fleet.Protocol;

namespace PlayFlow.Nakama.Fleet.Core
{
    public enum CommandReceipt { New, Duplicate, Collision, Full }

    /// <summary>
    /// Main-thread ledger. Unacknowledged results cannot be evicted; prepare deduplication survives
    /// through its admission lease. Cleanup must also be idempotent in the host after result eviction.
    /// </summary>
    public sealed class CommandLedger
    {
        private sealed class Entry
        {
            public string Fingerprint;
            public long RetainUntil;
            public CommandResult Result;
            public bool Acknowledged;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        public int Count => _entries.Count;

        public CommandLedger(int capacity = 512)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public CommandReceipt TryBegin(FleetCommand command, long now)
        {
            if (command == null || !AdmissionTokenVerifier.ValidIdentity(command.CommandId))
                throw new ArgumentException("Command identity is required.");
            var fingerprint = FleetProtocol.Serialize(command);
            if (_entries.TryGetValue(command.CommandId, out var existing))
            {
                if (!string.Equals(fingerprint, existing.Fingerprint, StringComparison.Ordinal))
                    return CommandReceipt.Collision;
                // A retransmission asks for the same ACK again, even if it was previously sent.
                existing.Acknowledged = false;
                return CommandReceipt.Duplicate;
            }
            Prune(now);
            if (_entries.Count >= _capacity) return CommandReceipt.Full;
            _entries.Add(command.CommandId, new Entry
            {
                Fingerprint = fingerprint,
                RetainUntil = command.Type == FleetProtocol.PrepareRoom ? command.ExpiresAt : now
            });
            return CommandReceipt.New;
        }

        public void Complete(string commandId, bool success, string error = null)
        {
            if (!_entries.TryGetValue(commandId, out var entry) || entry.Result != null)
                throw new InvalidOperationException("Command must be new and incomplete.");
            entry.Result = new CommandResult { CommandId = commandId, Success = success, Error = success ? null : error };
        }

        public CommandResult[] PendingResults(int maximum = 128)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
            return _entries.Values.Where(value => !value.Acknowledged && value.Result != null)
                .Select(value => new CommandResult
                {
                    CommandId = value.Result.CommandId,
                    Success = value.Result.Success,
                    Error = value.Result.Error
                }).Take(maximum).ToArray();
        }

        // Call only after the controller has durably accepted this exact request.
        public void Acknowledge(IEnumerable<CommandResult> sent)
        {
            foreach (var result in sent)
                if (_entries.TryGetValue(result.CommandId, out var entry) && entry.Result != null)
                    entry.Acknowledged = true;
        }

        public bool HasCapacity(long now)
        {
            Prune(now);
            return _entries.Count < _capacity;
        }

        private void Prune(long now)
        {
            var expired = _entries.Where(pair => pair.Value.Acknowledged && pair.Value.RetainUntil <= now)
                .Select(pair => pair.Key).ToArray();
            foreach (var commandId in expired) _entries.Remove(commandId);
        }
    }
}

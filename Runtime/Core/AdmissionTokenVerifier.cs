using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PlayFlow.Nakama.Fleet.Protocol;

namespace PlayFlow.Nakama.Fleet.Core
{
    /// <summary>Verifies a worker-scoped ticket. A successful signature check is not room authorization.</summary>
    public sealed class AdmissionTokenVerifier : IDisposable
    {
        private readonly byte[] _key;
        private readonly string _workerId;
        private readonly string _bootId;
        private readonly Func<long> _clock;
        private readonly int _maximumLifetimeSeconds;
        private bool _disposed;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public AdmissionTokenVerifier(string base64UrlKey, string workerId, string bootId,
            Func<long> clock = null, int maximumLifetimeSeconds = 300)
        {
            if (!ValidIdentity(workerId) || !ValidIdentity(bootId))
                throw new ArgumentException("Worker and boot identity are required.");
            if (maximumLifetimeSeconds < 1 || maximumLifetimeSeconds > 3600)
                throw new ArgumentOutOfRangeException(nameof(maximumLifetimeSeconds));
            if (!TryDecodeBase64Url(base64UrlKey, 128, out _key) || _key.Length < 32 || _key.Length > 64)
                throw new ArgumentException("Admission key must encode 32 to 64 random bytes.", nameof(base64UrlKey));
            _workerId = workerId;
            _bootId = bootId;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _maximumLifetimeSeconds = maximumLifetimeSeconds;
        }

        public bool TryValidate(string token, out AdmissionClaims claims, out string error)
        {
            claims = null;
            error = "invalid_ticket";
            if (string.IsNullOrEmpty(token) || token.Length > 8192) return false;
            var separator = token.IndexOf('.');
            if (separator < 1 || separator != token.LastIndexOf('.')) return false;
            var payload = token.Substring(0, separator);
            var signature = token.Substring(separator + 1);
            if (!TryDecodeBase64Url(signature, 64, out var signatureBytes) || signatureBytes.Length != 32)
                return false;
            if (!TryDecodeBase64Url(payload, 8000, out var payloadBytes)) return false;

            byte[] expected;
            lock (_key)
            {
                if (_disposed) { error = "verifier_stopped"; return false; }
                using (var hmac = new HMACSHA256(_key))
                    expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(payload));
            }
            // The fixed-size signature comparison does not stop on its first differing byte.
            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ signatureBytes[i];
            Array.Clear(expected, 0, expected.Length);
            if (difference != 0) return false;

            AdmissionClaims parsed;
            try
            {
                parsed = FleetProtocol.Deserialize<AdmissionClaims>(StrictUtf8.GetString(payloadBytes), 8000);
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is DecoderFallbackException)
            {
                return false;
            }
            if (parsed.SchemaVersion != FleetProtocol.SchemaVersion || parsed.Epoch < 1 ||
                parsed.Seat < 0 || parsed.Seat > 255 || !ValidIdentity(parsed.UserId) ||
                !ValidIdentity(parsed.RoomId) || !ValidIdentity(parsed.AllocationId) ||
                !ValidIdentity(parsed.ReservationId) || !ValidIdentity(parsed.Nonce) || parsed.Nonce.Length < 8)
                return false;
            if (!string.Equals(parsed.WorkerId, _workerId, StringComparison.Ordinal) ||
                !string.Equals(parsed.BootId, _bootId, StringComparison.Ordinal))
            {
                error = "wrong_worker_or_boot";
                return false;
            }
            var now = _clock();
            if (parsed.ExpiresAt <= now) { error = "ticket_expired"; return false; }
            if (parsed.ExpiresAt > now + _maximumLifetimeSeconds)
            {
                error = "ticket_lifetime_exceeded";
                return false;
            }
            claims = parsed;
            error = null;
            return true;
        }

        public void Dispose()
        {
            lock (_key)
            {
                if (_disposed) return;
                Array.Clear(_key, 0, _key.Length);
                _disposed = true;
            }
        }

        public static bool ValidIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            for (var i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]) || char.IsWhiteSpace(value[i])) return false;
            return true;
        }

        internal static bool TryDecodeBase64Url(string encoded, int maximumCharacters, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > maximumCharacters || encoded.Length % 4 == 1)
                return false;
            foreach (var c in encoded)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') &&
                    !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            try
            {
                var padded = encoded.Replace('-', '+').Replace('_', '/');
                padded += new string('=', (4 - padded.Length % 4) % 4);
                bytes = Convert.FromBase64String(padded);
                // Reject alternate spellings with nonzero trailing padding bits.
                return string.Equals(ToBase64Url(bytes), encoded, StringComparison.Ordinal);
            }
            catch (FormatException) { return false; }
        }

        internal static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    /// <summary>Never evicts a live nonce to make room. A full cache denies new admissions.</summary>
    public sealed class AdmissionReplayGuard
    {
        private readonly Dictionary<string, long> _nonces = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly Func<long> _clock;
        private int _operations;

        public AdmissionReplayGuard(int capacity = 4096, Func<long> clock = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public bool TryConsume(AdmissionClaims claims, out string error)
        {
            error = "invalid_ticket";
            if (claims == null || !AdmissionTokenVerifier.ValidIdentity(claims.Nonce)) return false;
            lock (_nonces)
            {
                var now = _clock();
                if (claims.ExpiresAt <= now) { error = "ticket_expired"; return false; }
                if (++_operations % 64 == 0 || _nonces.Count >= _capacity) Prune(now);
                if (_nonces.TryGetValue(claims.Nonce, out var expiresAt) && expiresAt > now)
                {
                    error = "ticket_replayed";
                    return false;
                }
                if (_nonces.Count >= _capacity) { error = "admission_cache_full"; return false; }
                _nonces[claims.Nonce] = claims.ExpiresAt;
                error = null;
                return true;
            }
        }

        private void Prune(long now)
        {
            var expired = new List<string>();
            foreach (var pair in _nonces)
                if (pair.Value <= now) expired.Add(pair.Key);
            foreach (var nonce in expired) _nonces.Remove(nonce);
        }
    }
}

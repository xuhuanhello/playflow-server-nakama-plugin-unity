using System;

namespace PlayFlow.Nakama.Fleet.Server
{
    public sealed class FleetAgentOptions
    {
        public Uri ControlUrl { get; private set; }
        public string WorkerId { get; private set; }
        public string BuildHash { get; private set; }
        internal string BootstrapToken { get; private set; }
        internal string AdmissionKey { get; private set; }

        public int RequestTimeoutSeconds { get; set; } = 10;
        public int MaximumResponseBytes { get; set; } = 262144;
        public int MaximumSnapshotRooms { get; set; } = 1024;
        public int MaximumCommandBatch { get; set; } = 128;
        public int CommandLedgerCapacity { get; set; } = 512;
        public int CancellationFenceCapacity { get; set; } = 2048;
        public int MaximumPreparationLifetimeSeconds { get; set; } = 120;
        public int NonceCapacity { get; set; } = 4096;
        public int MaximumAdmissionLifetimeSeconds { get; set; } = 60;
        // Stop admitting before the companion controller's default 8-second heartbeat deadline.
        public int ControlStaleAfterSeconds { get; set; } = 6;

        public static FleetAgentOptions FromEnvironment(bool allowInsecureLoopback = false)
        {
            return Create(Environment.GetEnvironmentVariable("FLEET_CONTROL_URL"),
                Environment.GetEnvironmentVariable("FLEET_WORKER_ID"),
                Environment.GetEnvironmentVariable("FLEET_BOOTSTRAP_TOKEN"),
                Environment.GetEnvironmentVariable("FLEET_ADMISSION_KEY"),
                Environment.GetEnvironmentVariable("FLEET_BUILD_HASH"), allowInsecureLoopback);
        }

        // For container integrations that supply credentials without environment variables.
        // Do not serialize this object, place credentials in scenes, or send it to a client.
        public static FleetAgentOptions Create(string controlUrl, string workerId, string bootstrapToken,
            string admissionKey, string buildHash, bool allowInsecureLoopback = false)
        {
            if (!Uri.TryCreate(controlUrl, UriKind.Absolute, out var uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("FLEET_CONTROL_URL must be an absolute HTTP(S) service URL.");
            if (uri.Scheme != Uri.UriSchemeHttps &&
                !(allowInsecureLoopback && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
                throw new ArgumentException("FLEET_CONTROL_URL requires HTTPS; HTTP is allowed only for explicit loopback development.");
            if (!Core.AdmissionTokenVerifier.ValidIdentity(workerId) ||
                !Core.AdmissionTokenVerifier.ValidIdentity(buildHash))
                throw new ArgumentException("FLEET_WORKER_ID and FLEET_BUILD_HASH are required.");
            if (string.IsNullOrEmpty(bootstrapToken) || bootstrapToken.Length > 4096 ||
                string.IsNullOrEmpty(admissionKey) || admissionKey.Length > 128)
                throw new ArgumentException("Fleet bootstrap and admission credentials are required.");
            return new FleetAgentOptions
            {
                ControlUrl = uri,
                WorkerId = workerId,
                BootstrapToken = bootstrapToken,
                AdmissionKey = admissionKey,
                BuildHash = buildHash
            };
        }

        internal void Validate()
        {
            if (RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 120 || MaximumResponseBytes < 1024 ||
                MaximumResponseBytes > 1048576 || MaximumSnapshotRooms < 1 || MaximumSnapshotRooms > 4096 ||
                MaximumCommandBatch < 1 || MaximumCommandBatch > 256 ||
                CommandLedgerCapacity < MaximumCommandBatch || CommandLedgerCapacity > 16384 ||
                CancellationFenceCapacity < 1 || CancellationFenceCapacity > 65536 ||
                MaximumPreparationLifetimeSeconds < 1 || MaximumPreparationLifetimeSeconds > 3600 ||
                NonceCapacity < 1 || NonceCapacity > 65536 || ControlStaleAfterSeconds < 5 ||
                ControlStaleAfterSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(FleetAgentOptions), "Fleet agent limits are out of range.");
        }

        internal Uri Endpoint(string route)
        {
            return new Uri(ControlUrl.AbsoluteUri.TrimEnd('/') + route, UriKind.Absolute);
        }

        internal FleetAgentOptions Snapshot() => (FleetAgentOptions)MemberwiseClone();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlayFlow.Nakama.Fleet.Client;
using PlayFlow.Nakama.Fleet.Core;
using PlayFlow.Nakama.Fleet.Protocol;
using PlayFlow.Nakama.Fleet.Server;

internal static class Program
{
    private const long Now = 1700000000;
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static string KeyText => Encode(Key);
    private static int _passed;

    private static async Task Main()
    {
        Run("Go-generated admission token", TestGoFixture);
        Run("wrong signature/worker/boot/expiry rejected", TestTicketBoundaries);
        Run("ambiguous/missing claims rejected", TestMalformedClaims);
        Run("one-use nonce under concurrency", TestReplayConcurrency);
        Run("nonce capacity fails closed and expires", TestReplayCapacity);
        Run("unacknowledged command result survives expiry", TestCommandRetention);
        Run("duplicate and colliding command identity", TestCommandDeduplication);
        Run("cleanup results reclaim bounded space", TestCleanupReclaim);
        Run("cancel fences a delayed prepare at the same epoch", TestCancelledRoomFence);
        Run("strict JSON and audit metrics contract", TestProtocol);
        Run("optional latency windows preserve unavailable and old payloads", TestLatencyWindows);
        Run("HTTPS configuration and explicit local HTTP", TestControlUrl);
        Run("default admission fence precedes controller heartbeat timeout", TestControlDeadline);
        Run("assignment build and expiry validation", TestAssignmentValidation);
        Run("assignment revisions and ownership", TestAssignmentTracker);
        await RunAsync("poll through stale revision to assignment", TestPoll);
        await RunAsync("timeout works when SDK ignores cancellation", TestTimeout);
        await RunAsync("caller cancellation remains cancellation", TestCancellation);
        Console.WriteLine("PASS " + _passed + " portable contract/security tests.");
    }

    private static void TestLatencyWindows()
    {
        var old = FleetProtocol.Deserialize<FleetMetrics>("{\"simulation_pending\":2,\"memory_bytes\":1024}");
        Assert(old.ClientPresentationToReadyMs == null && old.SimulationWorkers == null, "old metrics invented samples");
        var empty = FleetProtocol.Serialize(new FleetMetrics { ClientPresentationToReadyMs = new LatencyWindow() });
        Assert(empty.Contains("\"p95\":null") && empty.Contains("\"last_sample_age_seconds\":null"), "zero samples must explicitly remain unavailable");
        Assert(!empty.Contains("simulation_workers"), "unspecified worker configuration must remain optional");
        var values = new FleetMetrics { SimulationWorkers = 2, AuditWorkers = 1 };
        var window = new LatencyWindow { Count = 3, P50 = 10, P95 = 20, P99 = 20, Max = 20, LastSampleAgeSeconds = 1 };
        values.ClientPresentationToReadyMs = window;
        values.ClientPresentationToSettlementMs = window;
        values.ServerFirstAckToReadyMs = window;
        values.ServerFirstAckToSettlementMs = window;
        values.ServerLastAckToReadyMs = window;
        values.ServerLastAckToSettlementMs = window;
        values.SimulationQueueMs = window;
        values.SimulationWorkMs = window;
        var roundtrip = FleetProtocol.Deserialize<FleetMetrics>(FleetProtocol.Serialize(values));
        Assert(roundtrip.SimulationWorkers == 2 && roundtrip.AuditWorkers == 1, "worker configuration lost");
        Assert(roundtrip.ClientPresentationToReadyMs.Count == 3 && roundtrip.ClientPresentationToReadyMs.P95 == 20 && roundtrip.ClientPresentationToReadyMs.WindowSeconds == 60, "client_presentation_to_ready_ms lost");
        Assert(roundtrip.ClientPresentationToSettlementMs.Count == 3 && roundtrip.ClientPresentationToSettlementMs.P95 == 20 && roundtrip.ClientPresentationToSettlementMs.WindowSeconds == 60, "client_presentation_to_settlement_ms lost");
        Assert(roundtrip.ServerFirstAckToReadyMs.Count == 3 && roundtrip.ServerFirstAckToReadyMs.P95 == 20 && roundtrip.ServerFirstAckToReadyMs.WindowSeconds == 60, "server_first_ack_to_ready_ms lost");
        Assert(roundtrip.ServerFirstAckToSettlementMs.Count == 3 && roundtrip.ServerFirstAckToSettlementMs.P95 == 20 && roundtrip.ServerFirstAckToSettlementMs.WindowSeconds == 60, "server_first_ack_to_settlement_ms lost");
        Assert(roundtrip.ServerLastAckToReadyMs.Count == 3 && roundtrip.ServerLastAckToReadyMs.P95 == 20 && roundtrip.ServerLastAckToReadyMs.WindowSeconds == 60, "server_last_ack_to_ready_ms lost");
        Assert(roundtrip.ServerLastAckToSettlementMs.Count == 3 && roundtrip.ServerLastAckToSettlementMs.P95 == 20 && roundtrip.ServerLastAckToSettlementMs.WindowSeconds == 60, "server_last_ack_to_settlement_ms lost");
        Assert(roundtrip.SimulationQueueMs.Count == 3 && roundtrip.SimulationQueueMs.P95 == 20 && roundtrip.SimulationQueueMs.WindowSeconds == 60, "simulation_queue_ms lost");
        Assert(roundtrip.SimulationWorkMs.Count == 3 && roundtrip.SimulationWorkMs.P95 == 20 && roundtrip.SimulationWorkMs.WindowSeconds == 60, "simulation_work_ms lost");
    }

    private static AdmissionClaims Claims() => new AdmissionClaims
    {
        SchemaVersion = 1, UserId = "user-a", WorkerId = "worker-a", BootId = "boot-a",
        RoomId = "room-a", AllocationId = "allocation-a", ReservationId = "reservation-a",
        Seat = 0, Epoch = 1, ExpiresAt = Now + 60, Nonce = "nonce-fixture-0001", Resume = false
    };

    private static AdmissionTokenVerifier Verifier(string worker = "worker-a", string boot = "boot-a") =>
        new AdmissionTokenVerifier(KeyText, worker, boot, () => Now, 60);

    private static void TestGoFixture()
    {
        var fixture = FleetProtocol.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/admission-v1.json")));
        using (var verifier = new AdmissionTokenVerifier(fixture.KeyBase64Url, "worker-a", "boot-a", () => fixture.Now, 60))
        {
            Assert(verifier.TryValidate(fixture.Token, out var claims, out _), "Go signature must verify in C#");
            Assert(claims.UserId == "user-a" && claims.Seat == 0 && claims.Epoch == 1 && !claims.Resume, "claims differ");
        }
        Assert(fixture.Token == Sign(Claims()), "C# and Go serialized/signature bytes differ");
    }

    private static void TestTicketBoundaries()
    {
        using (var verifier = Verifier())
        {
            var token = Sign(Claims());
            Assert(verifier.TryValidate(token, out _, out _), "valid ticket rejected");
            Assert(!verifier.TryValidate(token.Substring(0, token.Length - 1) + (token.EndsWith("A") ? "B" : "A"), out _, out _), "tampered ticket accepted");
            var claims = Claims(); claims.WorkerId = "other";
            Assert(!verifier.TryValidate(Sign(claims), out _, out _), "cross-worker accepted");
            claims = Claims(); claims.BootId = "old-boot";
            Assert(!verifier.TryValidate(Sign(claims), out _, out _), "old boot accepted");
            claims = Claims(); claims.ExpiresAt = Now;
            Assert(!verifier.TryValidate(Sign(claims), out _, out _), "expired accepted at exact boundary");
            claims.ExpiresAt = Now + 61;
            Assert(!verifier.TryValidate(Sign(claims), out _, out _), "excess lifetime accepted");
            claims = Claims(); claims.Epoch = 0;
            Assert(!verifier.TryValidate(Sign(claims), out _, out _), "zero epoch accepted");
            verifier.Dispose();
            Assert(!verifier.TryValidate(token, out _, out _), "disposed verifier accepted");
        }
    }

    private static void TestMalformedClaims()
    {
        using (var verifier = Verifier())
        {
            var json = FleetProtocol.Serialize(Claims());
            Assert(!verifier.TryValidate(SignRaw(json.Replace("\"seat\":0,", "")), out _, out _), "missing seat accepted");
            Assert(!verifier.TryValidate(SignRaw(json.Replace(",\"resume\":false", "")), out _, out _), "missing resume accepted");
            Assert(!verifier.TryValidate(SignRaw(json.Replace("\"user_id\":\"user-a\"", "\"user_id\":\"evil\",\"user_id\":\"user-a\"")), out _, out _), "duplicate identity accepted");
            Assert(!verifier.TryValidate(new string('a', 9000), out _, out _), "oversized token accepted");
            Assert(!verifier.TryValidate(Sign(Claims()) + "=", out _, out _), "noncanonical base64url accepted");
        }
    }

    private static void TestReplayConcurrency()
    {
        var guard = new AdmissionReplayGuard(64, () => Now);
        var count = 0;
        Parallel.For(0, 100, iteration => { if (guard.TryConsume(Claims(), out _)) Interlocked.Increment(ref count); });
        Assert(count == 1, "same nonce admitted more than once");
    }

    private static void TestReplayCapacity()
    {
        var now = Now;
        var guard = new AdmissionReplayGuard(1, () => now);
        Assert(guard.TryConsume(Claims(), out _), "first nonce rejected");
        var next = Claims(); next.Nonce = "another-nonce";
        Assert(!guard.TryConsume(next, out var error) && error == "admission_cache_full", "live nonce evicted");
        now += 60;
        next.ExpiresAt = now + 30;
        Assert(guard.TryConsume(next, out _), "expired nonce did not free space");
    }

    private static FleetCommand Command(string id, string type = FleetProtocol.PrepareRoom) => new FleetCommand
    {
        CommandId = id, Type = type, RoomId = "room-a", AllocationId = "allocation-a",
        Epoch = 1, ExpiresAt = Now + 30, UserIds = new[] { "user-a", "user-b" }
    };

    private static void TestCommandRetention()
    {
        var ledger = new CommandLedger(1);
        var command = Command("command-a");
        Assert(ledger.TryBegin(command, Now) == CommandReceipt.New, "new command rejected");
        ledger.Complete(command.CommandId, true);
        Assert(ledger.TryBegin(Command("command-b"), Now + 100) == CommandReceipt.Full, "unacked result evicted");
        var results = ledger.PendingResults();
        Assert(results.Length == 1 && results[0].Success, "result lost");
        ledger.Acknowledge(results);
        Assert(ledger.TryBegin(Command("command-b"), Now + 100) == CommandReceipt.New, "ACK did not free expired result");
    }

    private static void TestCommandDeduplication()
    {
        var ledger = new CommandLedger(1);
        var command = Command("command-a");
        Assert(ledger.TryBegin(command, Now) == CommandReceipt.New, "new expected");
        ledger.Complete(command.CommandId, false, "room_busy");
        ledger.Acknowledge(ledger.PendingResults());
        Assert(!ledger.HasCapacity(Now + 1), "live prepare dedupe removed after ACK");
        Assert(ledger.TryBegin(command, Now + 1) == CommandReceipt.Duplicate, "duplicate reran");
        Assert(ledger.PendingResults().Single().Error == "room_busy", "duplicate changed result");
        var collision = Command("command-a"); collision.Epoch = 2;
        Assert(ledger.TryBegin(collision, Now + 1) == CommandReceipt.Collision, "command ID reused with new payload");
    }

    private static void TestCleanupReclaim()
    {
        var ledger = new CommandLedger(1);
        var command = Command("cancel-a", FleetProtocol.CancelRoom); command.ExpiresAt = Now - 100;
        Assert(ledger.TryBegin(command, Now) == CommandReceipt.New, "late cleanup was ignored");
        ledger.Complete(command.CommandId, true);
        ledger.Acknowledge(ledger.PendingResults());
        Assert(ledger.HasCapacity(Now), "acknowledged cleanup blocked bounded ledger");
    }

    private static void TestProtocol()
    {
        var json = FleetProtocol.Serialize(new HeartbeatRequest { WorkerId = "w", BootId = "b", Sequence = 1 });
        Assert(json.Contains("\"audit_pending\":0") && json.Contains("\"audit_active\":0") && json.Contains("\"pending_results\":0"), "audit metrics missing");
        Throws<Newtonsoft.Json.JsonException>(() => FleetProtocol.Deserialize<HeartbeatResponse>("{\"revision\":1,\"revision\":2}"));
        Throws<Newtonsoft.Json.JsonException>(() => FleetProtocol.Deserialize<HeartbeatResponse>("{} {}"));
    }

    private static void TestCancelledRoomFence()
    {
        var now = Now;
        var guard = new RoomCancellationGuard(1, 120, () => now);
        var cancel = Command("cancel-first", FleetProtocol.CancelRoom);
        cancel.ExpiresAt = Now - 20; // Expired cleanup must still apply.
        Assert(guard.TryRecord(cancel, out _), "cancel before prepare not retained");
        var delayed = Command("prepare-late");
        Assert(guard.IsCancelled(delayed.RoomId, delayed.AllocationId, delayed.Epoch), "delayed prepare bypassed cancel");
        Assert(!guard.IsCancelled(delayed.RoomId, delayed.AllocationId, delayed.Epoch + 1), "cancel crossed epoch fence");
        Assert(!guard.IsCancelled(delayed.RoomId, "replacement-allocation", delayed.Epoch), "cancel crossed allocation fence");
        var other = Command("cancel-other", FleetProtocol.CancelRoom); other.RoomId = "room-b";
        Assert(!guard.TryRecord(other, out _), "live cancellation fence evicted");
        now += 120;
        Assert(guard.TryRecord(other, out _), "expired fence did not free bounded space");
    }

    private static void TestControlUrl()
    {
        FleetAgentOptions.Create("https://control.example.test", "worker-a", "bootstrap", KeyText, "build-a");
        FleetAgentOptions.Create("http://127.0.0.1:7350", "worker-a", "bootstrap", KeyText, "build-a", true);
        Throws<ArgumentException>(() => FleetAgentOptions.Create("http://control.example.test", "worker-a", "bootstrap", KeyText, "build-a", true));
        Throws<ArgumentException>(() => FleetAgentOptions.Create("http://127.0.0.1:7350", "worker-a", "bootstrap", KeyText, "build-a"));
        Throws<ArgumentException>(() => FleetAgentOptions.Create("https://user:pass@control.example.test", "worker-a", "bootstrap", KeyText, "build-a"));
    }

    private static void TestControlDeadline()
    {
        const int controllerHeartbeatTimeoutSeconds = 8;
        const int heartbeatIntervalSeconds = 2;
        var options = FleetAgentOptions.Create("https://control.example.test", "worker-a", "bootstrap", KeyText, "build-a");
        options.Validate();
        Assert(options.ControlStaleAfterSeconds < controllerHeartbeatTimeoutSeconds,
            "agent would continue admitting after the default controller freshness deadline");
        Assert(options.ControlStaleAfterSeconds >= heartbeatIntervalSeconds * 2,
            "default admission fence cannot tolerate one missed regular heartbeat");
    }

    private static Assignment Assigned() => new Assignment
    {
        SchemaVersion = 1, AllocationId = "allocation-a", Revision = 3, State = "assigned",
        WorkerId = "worker-a", BootId = "boot-a", RoomId = "room-a", Seat = 0,
        Endpoint = new GameEndpoint { Host = "127.0.0.1", Port = 7777, Transport = "tugboat-udp" },
        BuildHash = "build-a", AdmissionToken = Sign(Claims()), ExpiresAt = Now + 60, ReconnectSeconds = 90
    };

    private static void TestAssignmentValidation()
    {
        AssignmentClient.ValidateForConnection(Assigned(), "build-a", Now);
        Throws<InvalidOperationException>(() => AssignmentClient.ValidateForConnection(Assigned(), "wrong-build", Now));
        var assignment = Assigned(); assignment.ExpiresAt = Now;
        Throws<FormatException>(() => AssignmentClient.ValidateForConnection(assignment, "build-a", Now));
        assignment = Assigned(); assignment.Endpoint.Port = 0;
        Throws<FormatException>(() => AssignmentClient.ValidateForConnection(assignment, "build-a", Now));
    }

    private static void TestAssignmentTracker()
    {
        var tracker = new AssignmentTracker();
        Assert(tracker.TryAccept(Assigned()), "initial assignment rejected");
        var old = Assigned(); old.Revision = 2;
        Assert(!tracker.TryAccept(old), "stale revision accepted");
        old.AllocationId = "other-allocation";
        Throws<InvalidOperationException>(() => tracker.TryAccept(old));
    }

    private static async Task TestPoll()
    {
        var call = 0;
        var client = new AssignmentClient(_ =>
        {
            var value = Assigned();
            if (++call < 3) { value.Revision = call == 1 ? 2 : 1; value.State = "preparing_room"; }
            return Task.FromResult(FleetProtocol.Serialize(value));
        }, "build-a", () => Now);
        var result = await client.WaitUntilAssignedAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
        Assert(result.State == "assigned" && call == 3, "poll did not ignore old revision");
    }

    private static async Task TestTimeout()
    {
        var never = new TaskCompletionSource<string>();
        var client = new AssignmentClient(_ => never.Task, "build-a", () => Now);
        await ThrowsAsync<TimeoutException>(() => client.WaitUntilAssignedAsync(TimeSpan.FromMilliseconds(50)));
    }

    private static async Task TestCancellation()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var client = new AssignmentClient(_ => Task.FromResult(FleetProtocol.Serialize(Assigned())), "build-a", () => Now);
            await ThrowsAsync<OperationCanceledException>(() => client.WaitUntilAssignedAsync(TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token));
        }
    }

    private static string Sign(AdmissionClaims claims) => SignRaw(FleetProtocol.Serialize(claims));
    private static string SignRaw(string json)
    {
        var payload = Encode(Encoding.UTF8.GetBytes(json));
        using (var hmac = new HMACSHA256(Key)) return payload + "." + Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Run(string name, Action test) { test(); Console.WriteLine("PASS " + name); _passed++; }
    private static async Task RunAsync(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); _passed++; }
    private sealed class Fixture
    {
        [Newtonsoft.Json.JsonProperty("key_base64url")] public string KeyBase64Url { get; set; }
        [Newtonsoft.Json.JsonProperty("now")] public long Now { get; set; }
        [Newtonsoft.Json.JsonProperty("token")] public string Token { get; set; }
    }
}

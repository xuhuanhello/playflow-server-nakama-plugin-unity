# Official Nakama SDK integration

Install the official Nakama Unity SDK separately in your game. The fleet runtime assemblies do not reference it, so the dedicated-server agent does not require Nakama client types. The complete helper below compiles against the official `NakamaClient` 3.22.0 API; copy it into your client assembly that already references the SDK and this package.

Create this helper with your existing authenticated `IClient`/`ISession`. Subscribe to the existing socket's `ReceivedMatchmakerMatched` event **before** calling `EnterQueueAsync`; after that event succeeds, call `WaitAfterMatchedAsync`. The 180-second assignment deadline begins after matchmaking and matches the backend's default allocation timeout. Your matchmaking-search deadline is a separate game/UI policy.

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using PlayFlow.Nakama.Fleet.Client;
using PlayFlow.Nakama.Fleet.Protocol;

public sealed class NakamaFleetExample
{
    private readonly IClient _client;
    private readonly ISession _session;
    private readonly string _buildHash;

    public NakamaFleetExample(IClient client, ISession session, string buildHash)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _buildHash = !string.IsNullOrWhiteSpace(buildHash)
            ? buildHash : throw new ArgumentException("Build hash is required.");
    }

    public Task<IMatchmakerTicket> EnterQueueAsync(ISocket socket, string region)
    {
        if (socket == null) throw new ArgumentNullException(nameof(socket));
        if (string.IsNullOrWhiteSpace(region)) throw new ArgumentException("Region is required.");
        var properties = new Dictionary<string, string>
        {
            { "region", region },
            { "build_hash", _buildHash }
        };
        // Profile values must equal the backend's configured pool identity.
        var query = "+properties.region:" + KeywordTerm(region)
            + " +properties.build_hash:" + KeywordTerm(_buildHash);
        return socket.AddMatchmakerAsync(query, 2, 2, stringProperties: properties);
    }

    public Task<Assignment> WaitAfterMatchedAsync(CancellationToken screenLifetimeToken)
    {
        var assignments = new AssignmentClient(async cancellationToken =>
        {
            var response = await _client.RpcAsync(_session,
                "fleet_assignment_get_v1", "{}", canceller: cancellationToken);
            return response.Payload;
        }, expectedBuildHash: _buildHash);
        return assignments.WaitUntilAssignedAsync(TimeSpan.FromSeconds(180),
            cancellationToken: screenLifetimeToken);
    }

    public async Task<Assignment> ResumeAsync(string allocationId, CancellationToken token)
    {
        var response = await _client.RpcAsync(_session, "fleet_resume_v1",
            AllocationPayload(allocationId), canceller: token);
        var assignment = FleetProtocol.Deserialize<Assignment>(response.Payload);
        AssignmentClient.ValidateForConnection(assignment, _buildHash,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return assignment;
    }

    public async Task CancelAllocationAsync(string allocationId, CancellationToken token)
    {
        await _client.RpcAsync(_session, "fleet_assignment_cancel_v1",
            AllocationPayload(allocationId), canceller: token);
    }

    private static string KeywordTerm(string value)
    {
        if (value == null || !Regex.IsMatch(value, @"\A[A-Za-z0-9_.:-]{1,128}\z",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Use an ASCII region/build identifier (1-128 letters, digits, _, ., :, or -).", nameof(value));
        return value.Replace("-", @"\-").Replace(":", @"\:");
    }

    private static string AllocationPayload(string allocationId)
    {
        if (string.IsNullOrWhiteSpace(allocationId))
            throw new ArgumentException("Allocation ID is required.");
        return FleetProtocol.Serialize(new Dictionary<string, string>
        {
            { "allocation_id", allocationId }
        });
    }
}
```

The constructor shown above is the actual package API: `AssignmentClient(Func<CancellationToken, Task<string>> fetchJson, string expectedBuildHash, Func<long> clock = null)`. The official SDK cancellation argument is named `canceller`; forwarding it makes RPC cancellation explicit. The poller also bounds local waiting if an older/custom HTTP adapter ignores cancellation.

The backend routes and payloads are:

| Operation | RPC | Payload |
| --- | --- | --- |
| Current assignment | `fleet_assignment_get_v1` | `{}` for the user's current allocation, or `{"allocation_id":"..."}` for a specific one |
| Fresh reconnect ticket | `fleet_resume_v1` | `{"allocation_id":"..."}` |
| Cancel allocated match | `fleet_assignment_cancel_v1` | `{"allocation_id":"..."}` |

`region` and `build_hash` belong in matchmaker string properties and the filtering query; the Go matched hook validates both against its configured pool. A deployment namespace has one immutable region/build profile. Routing clients to another build/region requires coordinating the appropriate backend deployment; this package does not provide a multiple-pool router.

Nakama 3.41.0 indexes these strings as keyword fields without term positions. JSON-style quotes create a phrase query that cannot match them; `KeywordTerm` validates the identifier and escapes reserved characters at the query layer. Keep the original values in `stringProperties`. See the [property index](https://github.com/heroiclabs/nakama/blob/v3.41.0/server/match_common.go#L166) and [phrase parser](https://github.com/heroiclabs/nakama/blob/v3.41.0/vendor/github.com/blugelabs/query_string/query_string_parser.go#L212).

The backend's notification subject is `fleet_assignment` (code 1001), which is **not** an RPC name. Use notifications to trigger an earlier `fleet_assignment_get_v1` query; polling is still the recovery path when a notification is lost. Responses contain the assignment object itself, with only the current user's admission token.

After obtaining an assignment, marshal to Unity's main thread if needed, configure FishNet with `assignment.Endpoint.Host`/`Port`, connect, and submit `assignment.AdmissionToken` in your application authentication handshake. Do not call `JoinMatchAsync` for this PlayFlow room. Do not log the token or persist it in PlayerPrefs.

The helper accepts `assigned`/`active` as connectable and reports `failed`/`cancelled`/`expired`/`completed` as terminal. Caller cancellation or the 180-second timeout stops local polling; it **does not cancel the server allocation**. Before rematching, query its outcome and call `CancelAllocationAsync` when appropriate; a cancellation ACK means accepted, and cleanup may still be pending. While still in Nakama's matchmaker queue, cancel the matchmaker ticket through `RemoveMatchmakerAsync` instead. Resume obtains a fresh one-use token and requires the game's real reconnect/seat policy; never replay the original token.

This example was compile-checked against the published SDK, not exercised with a live Nakama socket or FishNet transport. SDK references: [IClient RPC API](https://github.com/heroiclabs/nakama-dotnet/blob/master/Nakama/IClient.cs) and [ISocket matchmaker API](https://github.com/heroiclabs/nakama-dotnet/blob/master/Nakama/ISocket.cs). Your game still implements FishNet authentication, room-aware routing, connection replacement, result upload, and reconnect UI.

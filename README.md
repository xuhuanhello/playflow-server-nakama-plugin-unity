# PlayFlow Nakama Fleet — Unity

Unity companion to the [Nakama PlayFlow FleetManager](https://github.com/xuhuanhello/nakama-playflow). It connects a multi-room dedicated server to the fleet controller and helps clients retrieve their server assignment.

This is a community package, not an official PlayFlow or Heroic Labs integration. Version `0.1.0` has passed [real PlayFlow lifecycle checks](https://github.com/xuhuanhello/nakama-playflow/blob/main/docs/validation-2026-09-21.md) with a game-specific Unity/FishNet adapter; production latency, capacity and multi-instance scaling remain unvalidated.

## Features

- Outbound server bootstrap and heartbeats, with bounded retries and command deduplication.
- Room preparation, cancellation and drain commands through a game-defined `IFleetServerHost`.
- Signed admission ticket verification, one-use nonces and worker/room/seat identity checks.
- An SDK-independent assignment client with polling, cancellation and build validation.

The package does not require changes to the official Nakama Unity SDK or FishNet. Your game supplies the transport, room logic, reconnect policy and result persistence.

## References

- [Nakama PlayFlow backend](https://github.com/xuhuanhello/nakama-playflow): the matching controller and [protocol specification](https://github.com/xuhuanhello/nakama-playflow/blob/main/docs/protocol-v1.md).
- [Edgegap Unity plugin](https://github.com/edgegap/edgegap-server-nakama-plugin-unity): reference for a companion Unity integration.
- [PlayFlow Matchmaker guide](https://docs.playflowcloud.com/guides/ugs-matchmaker): reference for separating matchmaking from server allocation.

## Usage

Requires **Unity 2022.3+**. Newtonsoft JSON `3.2.1` is installed as a package dependency.

In Package Manager, choose **Add package from Git URL**:

```text
https://github.com/xuhuanhello/playflow-server-nakama-plugin-unity.git#main
```

For local development, choose **Add package from disk** and select `package.json`. Pin a tested commit SHA instead of `main` for reproducible builds.

**Dedicated server**

1. Implement [IFleetServerHost](Runtime/ServerAgent/IFleetServerHost.cs) for your game's room lifecycle and metrics. Report ready only after the transport, room resources and result storage are usable.
2. Add `FleetServerAgent` to the server scene. On Unity's main thread, call `agent.StartAgent(host, FleetAgentOptions.FromEnvironment())` after host initialization. Automatic startup is disabled by default.
3. Let the controller supply the worker's control URL, identity and credentials through [environment configuration](Runtime/ServerAgent/FleetAgentOptions.cs). The control address must be reachable from PlayFlow over HTTPS.
4. In the transport authentication handshake, call `TryAuthorizeAdmission` and immediately bind its verified identity to the connection. Route gameplay using that identity.

The [Minimal Server Host sample](Samples~/MinimalServer/README.md) demonstrates the contract without gameplay or a network transport. Full integration responsibilities are in the [game integration guide](https://github.com/xuhuanhello/nakama-playflow/blob/main/docs/game-integration.md).

**Client**

Use your existing Nakama session and socket to match two players, then query `fleet_assignment_get_v1`. `AssignmentClient` waits for a usable assignment; configure FishNet with its endpoint and send its admission ticket in your authentication handshake. Use `fleet_resume_v1` for a fresh reconnect ticket and `fleet_assignment_cancel_v1` to cancel an allocation. See the [official SDK example](Samples~/NakamaClient/README.md) for the complete sequence.

Portable tests and optional Unity compilation checks are documented in [Tests](Tests~/Portable/README.md).

## Important notes

- Host callbacks run on Unity's main thread. Preload resources and keep callbacks nonblocking; acknowledge commands only when their required state is real.
- Drain preserves active games. Keep unfinished results in `closing`, report pending work accurately, and retain `closed` room observations until heartbeat acknowledgement.
- New connections require a fresh, one-use admission ticket. Stale controller contact blocks new admissions, including reconnect handshakes; existing gameplay remains under host control.
- Cancelling local assignment polling does not cancel the backend allocation. Resolve that allocation before matching again.
- Credentials belong only in server configuration, never scenes, client builds, logs or Git. See [SECURITY.md](SECURITY.md).
- Sample room limits are illustrative. Measure capacity on the target Linux server and validate FishNet, reconnect, result persistence and drain before production.

## Protocol and license

Uses [Fleet Protocol v1](https://github.com/xuhuanhello/nakama-playflow/blob/main/docs/protocol-v1.md). Distributed under the [MIT License](LICENSE).

# PlayFlow Nakama Fleet — Unity package

Community integration for the companion [nakama-playflow](https://github.com/xuhuanhello/nakama-playflow) Go FleetManager. This package supplies the Unity side of the control protocol: an outbound server agent, worker-scoped admission verification, and an SDK-independent client assignment helper. It does not modify the official Nakama Unity SDK or FishNet. It is not an official PlayFlow or Heroic Labs package.

Version `0.1.0` is an initial integration foundation. A game-specific host adapter is required; gameplay, reconnect, result persistence, capacity calibration, and PlayFlow cloud acceptance are separate integration work.

## Install

Requires Unity **2022.3+** and Unity's official Newtonsoft JSON package **3.2.1** (declared as a package dependency).

For local development, use Package Manager → Add package from disk and select this repository's `package.json`. To try the current source, use Package Manager → Add package from Git URL:

```text
https://github.com/xuhuanhello/playflow-server-nakama-plugin-unity.git#main
```

The `main` branch changes during development; replace it with a reviewed commit SHA for a reproducible dependency. Versioned releases will have explicit tags. Go `.so`/Nakama version compatibility is separate from Unity package compatibility. These two sides share protocol schema version `1`.

## What the game must implement

Add `FleetServerAgent` to the dedicated-server scene and provide an `IFleetServerHost` component. `Start Automatically` is off by default so an ordinary player scene does not attempt worker registration. Either assign a host and enable it in the dedicated-server scene, or call:

```csharp
agent.StartAgent(myGameHost, FleetAgentOptions.FromEnvironment());
```

Call this on Unity's main thread after your server bootstrap has constructed the host. Keep the agent alive for the worker process lifetime. Disabling/stopping it prevents new admissions; restart the worker process to start a new boot. It never calls `Application.Quit`, destroys a gameplay room, or asserts that active matches are safe to terminate.

The adapter is the boundary between generic orchestration and game rules:

| Member | Required behavior |
| --- | --- |
| `IsReady` | True only when FishNet is listening, room resources are ready, and result persistence is usable. A running container alone is insufficient. |
| `PrepareRoom` | Apply the hard local capacity limit; create the full roster in one room; check allocation/epoch fencing; return success only when the room is actually ready. |
| `CancelRoom` | Check room/allocation/epoch before cleanup. A delayed command must never remove a replacement room. Success means the requested room can no longer admit/play and durable result obligations are resolved. |
| `BeginDrain` | Stop new rooms and new rounds, preserve active games and reconnect policy, and keep drain sticky. Success acknowledges the start of drain, not permission to terminate. |
| `CaptureRooms` | Return complete bounded observations with actual attached users. Use `waiting_players`, `playing`, `closing`, `closed`. A reserved roster is not a connected-player list. |
| `CaptureMetrics` | Return simulation backlog/active/oldest age, frame P99, whole-process memory, audit backlog/active, and pending results. |
| `OnHeartbeatAcknowledged` | Release retained `closed` observations from this exact accepted request. Until then they continue occupying local room slots. |
| `CanAdmit` | Check room/allocation/epoch, roster and seat, reservation ownership, room state, and connection/reconnect policy immediately before binding the transport connection. |

All adapter calls run on the main thread and must finish promptly. The initial interface uses synchronous command completion: preload shared room resources during boot, outside `PrepareRoom`. Do not return success while a scene, listener, or room is still loading. Do not perform blocking HTTP/database work in these callbacks.

Room closure is distinct from an individual match result. If players can continue another round in the same room, keep the room occupied. While results or replay/audit records lack durable ownership outside the process, keep `closing` and nonzero `pending_results`/audit metrics. Report `closed` only after they are safe, and retain that report until heartbeat acknowledgement. Dropping a closed room early or reporting zero for unfinished work could allow premature scale-in.

The agent invokes `OnHeartbeatAcknowledged` before handling the response's new commands. A host can therefore release an acknowledged closed slot and prepare its replacement without reporting more than its configured maximum room count. The sample also counts retained closed entries toward its hard room limit.

`prepare_room.expires_at` is the **preparation deadline**, not the final player seat lease. `Join` issues the latter afterward. The minimal sample conservatively holds an unused room through preparation deadline + 60 seconds; the real game host must implement its reservation and reconnect rules using signed ticket claims and authoritative cancellation. Active/reconnecting rooms must not disappear just because the initial preparation deadline passes.

## Server credentials

The Go controller supplies these to each PlayFlow worker through its server configuration:

| Environment variable | Purpose |
| --- | --- |
| `FLEET_CONTROL_URL` | Base URL of the externally reachable Nakama control service. HTTPS is required. |
| `FLEET_WORKER_ID` | Logical worker identity assigned by the controller. |
| `FLEET_BOOTSTRAP_TOKEN` | Worker-scoped bootstrap credential. Same boot/token retries are allowed after a lost response. |
| `FLEET_ADMISSION_KEY` | Base64url encoding of the per-worker 32-byte HMAC key. Never a fleet-wide master key. |
| `FLEET_BUILD_HASH` | Exact compatible game/protocol build identity. |

The agent generates a fresh `boot_id` for each process. Do not store credentials in scenes, prefabs, Resources, client builds, PlayerPrefs, logs, or assignment payloads. `FleetAgentOptions.Create` supports integrations that provide secrets without environment variables. HTTP is available only for an explicitly enabled loopback URL, such as local development at `http://127.0.0.1:7350`. For Docker-to-Docker HTTP tests, use the backend simulator or configure TLS; the production agent does not weaken this policy for arbitrary hostnames. Redirects are disabled so authentication headers and bootstrap bodies are not forwarded elsewhere.

## Admission and FishNet

The ticket format is `base64url(JSON).base64url(HMAC-SHA256(payload-segment))`. Schema 1 binds user, worker, boot, room, allocation, reservation, seat, epoch, expiry, nonce, and resume flag. The initial deployment issues tickets with at most 60 seconds of remaining validity. Clock synchronization is required; expiration has no grace window.

During your server's application authentication handshake, call `TryAuthorizeAdmission` exactly once for a new connection. It verifies the signature with fixed-time comparison, validates the claims, checks the host's live room authorization, and consumes the one-use nonce. Immediately bind the returned identity and seat to that FishNet connection. The signed `reservation_id` can establish the initial reservation binding; subsequent admissions must follow the host's replacement/resume policy for that same reservation. Never route gameplay based on a later client-supplied room or user ID.

A repeated handshake on an already-authenticated connection uses its bound identity. A fresh connection, including reconnect, obtains a newly issued ticket. The replay cache never evicts live nonces to admit more traffic: it fails closed at its configured bound. Select a transport/authentication channel that protects these bearer tickets in transit.

Existing matches stay under game-host control during a control-plane outage. The agent refuses new transport admissions once the control connection is stale (default 6 seconds, below the companion controller's 8-second heartbeat freshness deadline). Keep this configured window below the controller deadline if either side changes. It does not terminate existing gameplay. Reconnect that requires a new handshake is therefore also unavailable while control is stale; this package does not promise outage-proof reconnect.

## Heartbeats and commands

The agent uses `UnityWebRequest` for:

```text
POST /fleet/v1/agent/bootstrap
POST /fleet/v1/agent/heartbeat
```

Heartbeat requests use a monotonically increasing sequence. Until a successful response, the exact same JSON body and sequence are retried with bounded exponential backoff and jitter. Admission freshness is measured conservatively from that sequence's original send time, so a late or retried response cannot extend the control window. The controller must durably process `command_results` and snapshots before returning success. Lost responses can safely trigger retransmission. Credentials and response bodies are never logged.

Commands have stable IDs. Duplicate IDs with the same payload replay the stored result; a changed payload for the same ID faults the agent. `prepare_room` expires at its deadline, while delayed `cancel_room` and `drain` still run because they are cleanup operations. Host methods must remain idempotent by room/allocation/epoch even after acknowledged cleanup entries are reclaimed from the bounded ledger.

Cancellation also creates a bounded room/allocation/epoch tombstone before cleanup. It rejects an already-in-flight prepare or ticket even if cancellation arrives first. Tombstones are retained for at least the configured maximum preparation/admission lifetime (120/60 seconds by default); keep these bounds consistent with the controller. They never evict a live fence to make room. Transient cleanup failures require the controller to issue a **new command ID** with backoff: redelivering an already-acknowledged command replays its previous outcome.

Defaults: 512 command records, at most 128 commands/results per batch, 4096 live nonces, 256 KiB request/response body bound, 1024 snapshot room records, 10-second request timeout. Configure smaller room bounds to your actual profile. The agent refuses/tracks overflow rather than silently truncating occupancy. A full command ledger suppresses readiness; the controller retains unacknowledged commands for redelivery. Invalid authentication, protocol/revision regressions, or command ID collisions stop new admission and require operator diagnosis.

## Client

`AssignmentClient` accepts a `Func<CancellationToken, Task<string>>` backed by your existing authenticated Nakama RPC call. It polls to `assigned`/`active`, rejects expired routes and the wrong build, honors cancellation/deadlines, and ignores stale revisions for a single allocation. It never needs an HMAC key. The sample README shows the official SDK bridge without introducing an SDK assembly dependency in the server package.

Nakama notifications are a wakeup hint; query the assignment after missed notifications or client recovery. Local polling cancellation does not cancel the allocation. Your game must call the backend cancellation/resume operation and resolve the previous allocation before matching again. Configure FishNet's external mapped host/port from the returned endpoint and submit the admission token in your application handshake.

## Validation and remaining integration

The portable suite checks Go↔C# HMAC bytes, rejection boundaries, missing/duplicate claims, concurrent nonce replay, bounded command retention, assignment revisions, and cancellation. It can compile the server and sample against installed Unity 2022.3 reference assemblies. See `Tests~/Portable/README.md` for reproducible commands.

Local validation on 2026-09-21: 17 portable tests passed using .NET SDK 10.0.103 and the official Unity Newtonsoft 3.2.1 DLL, and passed again with the public Newtonsoft NuGet dependency and no Unity reference paths. All runtime and sample sources compiled against Unity 2022.3.62f3 and .NET Standard 2.1 with zero warnings/errors. The exact client README example also compiled against the published NakamaClient 3.22.0 SDK. Five assembly definitions and 29 stable metadata GUIDs were checked. The GitHub Actions workflow runs only portable tests with the public Newtonsoft NuGet package; it requires no Unity installation or license. The workflow itself has not yet run on GitHub.

That does not exercise Unity's coroutine scheduler, UnityWebRequest in a live player, FishNet UDP, IL2CPP linking, gameplay, or PlayFlow instances. Before production, complete target Linux player tests, the game-specific adapter, fresh-token reconnect and result persistence, real cold starts/cancellation/drain, and capacity measurements on the target instance size. Four sample rooms or a configuration maximum is not a measured capacity promise.

# Minimal server host

Import this sample in Package Manager, add `MinimalServerHost` and `FleetServerAgent` to the same object, assign the host to the agent, and enable the agent's automatic start **only in the dedicated-server scene**. For a loopback local controller, enable `Allow Insecure Loopback` explicitly.

The sample starts with `listening = false`. It intentionally contains no transport. A local contract exercise can set that checkbox manually; a real build must set it only after FishNet reports that its transport is listening. Four room slots here are a demonstration limit, not measured DM capacity.

`prepare_room` creates a room with two reserved users. Hook your transport handshake to `FleetServerAgent.TryAuthorizeAdmission` and immediately call `AttachAuthorizedConnection` with the returned claims. Route all future traffic using that bound server identity, never client-supplied room/user fields. A repeated handshake on an already-authenticated connection reuses its identity; a new connection requires a fresh admission token.

The sample can expire an empty waiting room and retains the `closed` snapshot until acknowledged. It refuses cancellation of a room with connected players. It deliberately does not implement game completion, persistence, reconnect, or client replacement. The production DM adapter must implement these before cloud deployment.

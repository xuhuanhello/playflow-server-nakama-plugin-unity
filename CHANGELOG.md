# Changelog

## 0.1.0 — 2026-09-21

- Added schema 1 server/client DTOs and an SDK-independent assignment poller.
- Added outbound UnityWebRequest bootstrap/heartbeat agent with exact retry payloads, bounded buffers, sticky drain and main-thread adapter calls.
- Added per-worker HMAC verification, strict claims, one-use nonces and bounded cancellation fences.
- Added a minimal room host sample and official Nakama client integration notes.
- Added portable Go/C# contract tests and Unity reference-assembly compile checks.

This first version requires a game host adapter and cloud acceptance testing. It does not include game-specific FishNet integration or a result-persistence implementation.

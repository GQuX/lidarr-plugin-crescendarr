# Crescendarr v0.2.0 engineering checkpoint

- Decision: primary target `lscr.io/linuxserver/lidarr:latest`, Lidarr `3.1.6.5079`, .NET 8.
- User: backups completed; Watchtower Lidarr label disabled; existing Tubifarry retained until a real Crescendarr replacement is available.
- Source: independent merge planner, planned 29 synthetic C# specs, guarded non-delegating probe, optional isolated stable Docker lab, CI `.NET 8`.
- Safety: plan ID now hashes full snapshot fields and rejects shared existing track-file IDs.
- Verification: static source checks and isolated Compose Python checks only. No .NET SDK in authoring environment, no C# compilation, no Lidarr host probe or filesystem import test.
- Gate: no installation on user production, no file replacement, no queue manipulation and no user-side experimentation until test probe has been compiled and evaluated independently.

---
paths:
  - "src/AcSession.cs"
  - "docs/CLIENT_ANTICHEAT.md"
  - "docs/AC_MODULE_SIGNAL.md"
  - "scripts/test_ac_session.ps1"
---
# Anti-cheat session

- Protocol v1 is public by design; its value comes from the server's checks (docs/CLIENT_ANTICHEAT.md)
- The tool list is never in the repo, the exe or a config: the server sends it (`"tools"` in the session reply) and
  the client reports only `tool:<id>`
- Only labels leave the PC (`CleanSignals`): no process lists, file contents or paths. Sending anything new means
  updating the in-app notice (`ShowAcNoticeIfDue` in WpfLauncher.cs) and the doc's privacy section in the same change
- The session never blocks or delays PLAY; failures only go to launcher.log; never log the session key
- Endpoint must pass `IsAllowedEndpoint` (https; plain http only to a loopback/private IP); no redirects
- Server text reaches the log only through `Short()`. Test with `scripts\test_ac_session.ps1`

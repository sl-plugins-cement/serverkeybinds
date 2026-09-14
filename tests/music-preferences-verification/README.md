# Shared music verification

Local verification on 2026-09-14; no production deployment.

- ServerKeybinds, GMP, ScriptedWarhead and Reinforcements Release builds passed. The existing
  ServerKeybinds nullable warning and Reinforcements asset-preview warnings remain; no build errors.
- `dotnet run --project ServerKeybinds/tests/MusicPreferences.Unit -c Release`: **17 checks passed**.
  Covers persistence, independent users, in-flight predicate changes, duplicate client responses,
  menu precedence, reconnect, consumer leases, registration collisions, unauthenticated disconnect,
  failed writes and temporary-file cleanup. These are production preference sources with native
  boundary stubs, not a client networking test.
- Scenario lint: zero errors; existing legacy exemptions unchanged.
- Visible LocalAdmin, existing local port 7777: loaded committed consumer builds and the complete
  Reinforcements package (105 files). Used two native dummies with distinct test UserIDs and a
  temporary 180-second WAV for GMP/Omega. Ran `roundlock`, `reinforcements auto off`,
  `musicverify prepare`, `musicverify play <wav>`, `sw omega`, `reinforcements spawn nu7`, then
  `musicverify check` after the Nu-7 music-start log.
- **PASS:** exactly one native setting 24001; the actual speakers' recipient predicates excluded
  the muted dummy, included the other dummy, and reacted to unmute/remute on the same speaker
  objects (controllers 240 GMP, 255 Omega, 71 Reinforcements). Omega and Nu-7 playback-start logs
  were observed. This checks recipient routing; it does not record decoded sound at a client.
- Omega still reported 103 seconds remaining after the toggle check, consistent with its original
  182-second playback-relative countdown. No timer-reset path was added.
- Earlier probe attempts exposed test-driver issues: GMP requires an in-game RA sender, and
  native dummies assign `ID_Dummy` after creation. The final driver normalizes identities after
  initialization and validates its sender/permissions before dispatching the actual RA command.
- A subsequent GMP teardown-only change clears the recipient predicate before destroying its
  speaker, preventing a reused controller from inheriting the shared mute. Its Release build
  passed; the live excerpt predates that teardown-only change. Final and live hashes are separate.

Evidence: [live excerpt](live-excerpt.txt), [live hashes](live-sha256.json),
[final artifact locations and hashes](artifacts.json). Runtime source revisions:
Reinforcements `dff1c71`, Omega `7a7784d`, GMP `acad41e` (live `0c1cf12`),
ServerKeybinds preference source `efe20df` (probe `c4590d8`).

The test server was stopped after cleanup. All 110 staged local files were restored or moved to
the reversible test archive, with backup hashes verified. Pre-existing workspace edits were kept
out of these commits. The clean release worktrees/package and backups are retained at the path in
`artifacts.json`.

Still manual: real-client menu appearance, client-saved settings across reconnect/server restart,
and audible mute/unmute with two clients. Unit coverage includes persistence and scoped-audience
composition; the live run exercised global Nu-7 playback, not a participant-scoped intro. Omega's
single MP3 includes speech, so that embedded speech is muted with the music; separate subtitles
and native fallback warnings are unaffected.

# Changelog

All notable changes to ServerKeybinds. Versions follow the csproj `<Version>`; dates are the
commit dates on `main`. `KeybindRegistry.ApiVersion` was bumped in lockstep through 6.0.0 and is
frozen at 6 as a diagnostic since 6.1.0.

## 6.3.0 - 2026-09-25

- Foreign settings are blocked by default: entries written into `DefinedSettings` without a
  registry block are stripped on rebuild and on the next send, logged once per id, and listed by
  the new `keybinds foreign` RA subcommand. `KeybindRegistry.ForeignPolicy = Merge` restores the
  additive behaviour; `keybinds foreign block|merge` switches it at runtime.
- Blocks reserved for the ported production plugins: `AdditionalNameTags` 29000, `PlayerBadge`
  30000, `ScpTiers` 9100000 (historical ids kept as locals), `MvpSystem` now at 25000 local 1,
  `CustomizableUi` 530000 with historical ids as locals 210+.
- README id table corrected: `AircraftCarrier` is 1170000; bot lane blocks listed.

## 6.2.0 - 2026-09-25

- Per-player entries ported from the bot lane fork so that fork can be retired: `AddDropdownForPlayer`
  and `AddButtonForPlayer` with `DropdownModel`/`ButtonModel` (`Hidden` omits the entry for one player),
  `DropdownSelection` validated against the option list that player was actually sent, and
  `SssInterest`-routed refreshes (`SetPlayerInterests`, `InvalidatePlayer`,
  `InvalidatePopulationBoundary`, `RequestPlayerRefresh`) through one debounced, rate-limited budget
  that sends via the existing delivery coordinator. `keybinds status` reports the refresh budget.
- `AddNative` factories may return null to omit the entry for that recipient.
- Id table: `ScpslBotWarmup` 1130000, `StatsBots` 1131000, `ScpslBotTools` 1132000 recorded;
  `AircraftCarrier` moved to 1170000 because 1130000 was already deployed on the bot lane.
- Unit tests under `tests/PerPlayerEntries.Unit`. See `docs/per-player-entries.md`.

## 6.1.0 - 2026-09-24

- Contract frozen: the public surface is additive-only and never re-signatured; every server runs
  the newest build. `ApiVersion` stays 6 as a startup-log diagnostic only; consumers no longer
  probe it by reflection or document "requires API N". The csproj comment that told consumers to
  reflect-probe is gone.
- New entry types: `AddButton` (`SSButton`, click or completed hold, nothing on acquisition) and
  `AddPlaintext` (`SSPlaintextSetting`, reports on acquisition, cut to the character limit).
- `AddNative` escape hatch accepts any `ServerSpecificSettingBase`: the factory receives the
  absolute id and the recipient, must build with that id, and gets the raw response with only the
  visibility gate applied. A new native entry type no longer needs a library release.
- Startup identity: the first `Enable()` logs
  `[ServerKeybinds] ServerKeybinds <version> (API 6) loaded from <path> sha256 <hash>` and logs
  an error for every second ServerKeybinds assembly in the process, with its path and hash.
- Build contract: consumers build the library from source through a `ProjectReference` with the
  `ServerKeybindsProject` property convention documented in the README; a consumer build with
  `-p:DeployToLocalServer=true` refreshes every deployed copy under the local LabAPI install.
- Install location is documented by the host's `LabApi-<port>.yml` loader configuration instead
  of `dependencies/global`; on SR1 that is `LabAPI/plugins/7777`.
- Id table catch-up: `Scp5kGanzir` reserved at 1150000 (aircraft, jetpack and naval insertion);
  `CementExamples` (scpsl-plugin-examples teaching block) moved from 1150000 to 1160000 to clear
  the Ganzir collision. Chinese onboarding guide added under `docs/`.
- Assembly and file version 6.1.0.0.

## 6.0.0 - 2026-09-19

- API 6: shared per-player **plugin music volume** beside the mute toggle, at id 24002
  (`PluginMusicPreferences.VolumeSettingId`). Started as a 100/75/50/25 % dropdown, became an SSS
  slider snapping to speaker steps, then a 0-100 percent applied per listener before Opus encoding
  (`VolumePercentOf`, `VolumeOf`, `SetVolumePercent`, `VolumePercentChanged`), mirrored by UserId
  to `plugin_music_volume.txt`. Consumers that keep the stock single-stream transmitter play at
  full volume for everyone.
- Global Music Player 2.1 was the first consumer of the slider.

## 5.0.0 - 2026-09-14

- API 5: shared persistent **plugin music preference** (`PluginMusicPreferences`): a leased
  Announcements block at 24000 with an On / Muted two-button toggle at 24001, mirrored by UserId
  to `plugin_music_muted.txt` and read into memory before settings arrive. Id 24001 and the
  two-button type retain the client preferences saved for Global Music Player's default toggle;
  GMP's custom ids are no longer used, and its `gmpmute`/`gmpunmute` aliases now set this
  preference.
- ReinforcementsSystem, ScriptedWarhead (Omega) and Global Music Player were verified as
  consumers with a dummy-driven live probe (`tests/MusicPreferences.Live`) and a unit harness
  (`tests/MusicPreferences.Unit`).
- `Scp966` block reserved at 1140000.

## 4.0.0 - 2026-08-30

- API 4: **self-healing delivery** (`SettingsDeliveryCoordinator`). The personalised pack is sent
  with wire version 4, join readiness and acknowledgement are each retried a bounded three times,
  and a paced reconcile send repairs the collection after 30 s without a registry send. Reconcile
  defers while the settings tab is open or a key is latched; a latch defers at most one further
  interval, then is released as stale. A version-4 status report or any registry-owned response
  acknowledges a pending send.
- Diagnostics: `KeybindRegistry.Debug` gates a reason-tagged per-send audit line and the
  formerly silent join-send skip branches; `KeybindRegistry.PressTrace` gates a trace line at every
  routing decision.
- Self-registered RA command `keybinds` (alias `skb`): `status <id|name>`, `resend <id|name>`,
  `trace on|off`. LabAPI does not scan libraries for commands, so the registry registers it on the
  first Enable.
- Hygiene: press latches clear on role change (release edge dispatched first, so hold-to-act
  consumers are never stranded) and round restart; every per-player store is keyed by UserId and
  pruned on `ReferenceHub.OnPlayerRemoved`, closing the destroyed-hub-key hazard and the reused
  session player id inheriting another player's state.

## 3.0.0 - 2026-08-25

- API 3: **registry-owned menu order**. Blocks declare `InCategory(SettingsCategory)` and sort by
  `(category, Order, base id)`; the registry synthesises one `SSGroupHeader` per category at
  `RegistryHeaders (23000) + (int)category` and demotes each block's own header to a
  reduced-padding sub-header. Previously the menu was `Dictionary` enumeration order (plugin load
  order), and `DefinedSettings` was built foreign-first so every registry setting sat below the
  plugins that merge into the array themselves; registry entries now lead.
- `Order(int)` breaks ties within a category so the most-used settings can be pinned to the top.
- `AddTwoButtons` wraps the native `SSTwoButtonsSetting`; `AddTextArea` wraps `SSTextArea`.
- Per-player two-button defaults: an `AddTwoButtons` overload taking `Func<Player, bool>` decides
  the starting position per recipient; `DefaultTwoButtonsFor(player, settingId)` returns the default
  actually sent (recorded at serialisation, not recomputed) so an untouched acquisition report is
  not mistaken for a choice.
- Review fixes: `ApiVersion` became a static property (as a `const` it was inlined, so a version
  guard could never fire); `InCategory` rejects undeclared enum values (an unknown value invented a
  header the registry could not strip); `ClaimBlock` rejects base 23000; `LogBlock` reports value
  settings, text areas and the category (a toggle-only block used to log an empty list).
- Documented traps: converting a dropdown to a two-button toggle resets every player's saved value
  because the PlayerPrefs key contains the type code; a keybind default is only ever a
  `SuggestedKey`.
- Category header language via `KeybindRegistry.Language`. Adds a `Unity.TextMeshPro` reference
  required by `SSTextArea`'s constructor.

## 2.0.0 - 2026-08-14

- API 2: additive server settings registry. `AddDropdown`, `AddSlider` and `VisibleTo` join the
  block builder; the registry owns the single additive merge into `DefinedSettings`, personalised
  joining-player sends, setting-response visibility gates and collision detection. Consumers must
  not create a parallel global-settings merge path.
- `CustomItems.dll` hard-depends on the library.

## 1.0.0 - 2026-08-04

- Standalone repository established from the metarepo. API 1: `KeybindRegistry.ClaimBlock`,
  `KeybindBlock.Header`/`Add`/`SettingId`/`Enable`/`Disable`, fixed 1000-wide id blocks in
  `SssIdBlocks`, rising/falling key edges and `KeybindInfo` diagnostics. Deployed as a LabAPI
  `dependencies/global` library.

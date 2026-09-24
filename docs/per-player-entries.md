# Per-player entries, validated selections and budgeted refreshes

ServerKeybinds builds every player's settings collection per recipient. This page covers the entries
whose whole presentation is decided per player, how their responses are validated against what that
player was actually sent, and the refresh path that re-sends a player's view when server state changes.

## Per-player entries

`KeybindBlock.AddDropdownForPlayer` and `KeybindBlock.AddButtonForPlayer` take a resolver instead of
fixed presentation:

| Member | Resolver | Callback |
| --- | --- | --- |
| `AddDropdownForPlayer(local, modelForPlayer, onChanged, fallbackModel = null, onAcquired = null)` | `Func<Player, DropdownModel>` | `Action<Player, DropdownSelection>` |
| `AddButtonForPlayer(local, modelForPlayer, onPressed, fallbackModel = null)` | `Func<Player, ButtonModel>` | `Action<Player>` |

- `DropdownModel(label, options, defaultIndex = 0, hint = "", visible = true)` describes a regular
  (non-scrollable) dropdown with 1..255 options. `ButtonModel(label, buttonText, holdTimeSeconds = 0,
  hint = "", visible = true)` describes a native button.
- Return `DropdownModel.Hidden` / `ButtonModel.Hidden` (or a model with `visible: false`) to omit the entry
  for that player. Category and block headers are still emitted per recipient, so a player who sees no
  entry of a block still sees no dangling header only if the block's `VisibleTo` hides the whole block.
- The resolver runs once per send per player, plus once per response to revalidate. It must be cheap and
  side-effect free; a throwing or null-returning resolver hides the entry for that send.
- `fallbackModel` only shapes the shared `DefinedSettings` array, which players never receive (the
  registry suppresses the native join send). It exists so the native server prevalidates the id and type.
  A hidden or null fallback becomes an empty placeholder.
- The same rule applies to `AddNative`: a factory that returns null omits the entry from that build; a
  factory that builds the wrong id still throws.

## Validated selections

A dropdown response is interpreted against the option list the player was sent, never against the model
a resolver would return now. The registry records, per player (by UserId) and per send, the exact option
list of every personalised dropdown together with a send generation. Each response is classified:

| Response | Outcome |
| --- | --- |
| First value after a send | Acquisition: the client re-reports its stored value after every pack. `onAcquired` is called (if supplied) with the `DropdownSelection`; `onChanged` is not. |
| Later, different value | Change: `onChanged` is called once with the `DropdownSelection`. |
| Same value again | Duplicate: swallowed. |
| Index outside the sent list, no sent list for that id, or the option text no longer matches the current model | Stale: dropped, and a refresh of that player's view is requested through the budget below. |

`DropdownSelection` carries `Index`, `Value` (the option text that was sent at that index) and
`SendGeneration`. Consumers still recheck gameplay authorization when executing the value; the registry
only proves the player could have seen it.

A personalised button press is dispatched only while the model resolves visible for that player. A
button has no value, so nothing fires on acquisition.

Two-button defaults are recorded the same way; `KeybindRegistry.DefaultTwoButtonsFor(player, settingId)`
returns the default that player was sent.

## Interests

`SssInterest` is a flag set of state domains that can change a player's view (`Role`, `Item`, `Cooldown`,
`Title`, `Language`, `Zone`, `Permission`, `Display`, `WarmupMode`, `PopulationBoundary`; `AllPersonal` and
`All` combine them). `KeybindRegistry.InterestIndex` is an `SssInterestIndex<string>` keyed by UserId.

- `KeybindRegistry.SetPlayerInterests(player, interests)` registers what a player cares about. A player who
  was never registered is tracked with `All` the first time they are sent to or refreshed.
- `KeybindRegistry.InvalidatePlayer(player, changed, reason)` requests a refresh for that one player if
  their interests intersect `changed`. It never fans out. Returns whether a refresh was requested.
- `KeybindRegistry.InvalidatePopulationBoundary(before, after, reason)` requests a refresh for the players
  present in both sets when the population crosses 1-to-2 or 2-to-1 and they registered
  `PopulationBoundary`. A newcomer is left to its ordinary join send. Returns the number of refreshes
  requested.

Interest state is pruned when the player leaves, on round restart, and when the last block is disabled.

## Refresh path

`KeybindRegistry.RequestPlayerRefresh(player, reason)` queues a re-send through a per-player budget:

- trailing 500 ms debounce; repeated requests coalesce into one send whose reason lists every distinct
  reason (`refresh:role+zone`);
- the view is built and fingerprinted when the candidate is processed; a fingerprint identical to the
  last send is skipped;
- at least two seconds between sends and at most six sends per rolling minute per player; a blocked
  candidate waits and is replaced by the latest request.

Every send, budgeted or not, goes through the same delivery coordinator and the same build path, is
audited the same way, and is recorded in the budget with the fingerprint of what actually went out. An
immediate send (join, rebuild, reconcile, `RefreshPlayer`) therefore drops a pending candidate and starts
the two-second spacing. `KeybindRegistry.RefreshPlayer(player)` remains the immediate, acknowledged
re-send used by `keybinds resend`.

Diagnostics: `KeybindRegistry.RefreshCounters` (requested, sent, coalesced, rate-limited, identical) and
`KeybindRegistry.TryGetRefreshDiagnostics(player, out diagnostics)` (last send in monotonic seconds,
last-sent fingerprint, pending flag, sends in the rolling minute). `keybinds status <id|name>` prints both.

## Usage

```csharp
KeybindBlock block = KeybindRegistry
    .ClaimBlock(SssIdBlocks.ScpslBotWarmup, "Warmup")
    .Header("热身控制")
    .InCategory(SettingsCategory.Gameplay)
    .AddDropdownForPlayer(
        1,
        player => CanRespawn(player)
            ? new DropdownModel("重生为", new[] { "请选择" }.Concat(RolesFor(player)), hint: "选择后点击应用。")
            : DropdownModel.Hidden,
        onChanged: (player, selection) => Stage(player, selection.Value),
        onAcquired: (player, selection) => Stage(player, selection.Value))
    .AddButtonForPlayer(
        2,
        player => HasStagedRole(player) ? new ButtonModel("角色操作", "应用") : ButtonModel.Hidden,
        player => ApplyStagedRole(player));
block.Enable();

// When the player's role or inventory changes elsewhere:
KeybindRegistry.SetPlayerInterests(player, SssInterest.All);
KeybindRegistry.InvalidatePlayer(player, SssInterest.Role | SssInterest.Item, "role-changed");
```

## Id blocks

The bot lane's blocks keep their deployed bases: `SssIdBlocks.ScpslBotWarmup` (1130000),
`SssIdBlocks.StatsBots` (1131000) and `SssIdBlocks.ScpslBotTools` (1132000). `SssIdBlocks.AircraftCarrier`
is 1170000.

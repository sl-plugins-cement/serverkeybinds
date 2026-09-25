# ServerKeybinds

`ServerKeybinds.dll` is the process-wide owner of SCP:SL Server-Specific Settings (SSS) for
metarepo plugins. It is a shared library, not a LabAPI plugin: it owns the single additive merge
into `ServerSpecificSettingsSync.DefinedSettings`, the personalised join send, the menu order,
per-player visibility, key press/release edges, reliable delivery and id-collision detection.
A consumer plugin must not create a parallel merge path or call `SendToPlayer` itself.

`CustomItems.dll` hard-depends on ServerKeybinds; any custom-item plugin that needs settings or
keybinds uses this registry.

## Contract

- The public surface is **additive-only**. Members are never removed or re-signatured, so a
  consumer built against any earlier build runs against the current one.
- `KeybindRegistry.ApiVersion` is a startup-log diagnostic only. Nothing branches on it;
  consumers must not probe it and must not document "requires API N".
- Every server runs the newest build. The assembly is not strong-named, so the CLR binds by simple
  name and any build satisfies any consumer. `<Version>` in the csproj is SemVer for humans: the
  minor is bumped for additions, the major is reserved for a break the contract forbids.
- New native entry types are covered without a library change through `AddNative` (below).
- Consumers build the library from source through a `ProjectReference`, using exactly this
  convention so a checkout beside the consumer or three levels up is found automatically and any
  other layout is passed explicitly:

```xml
  <PropertyGroup>
    <ServerKeybindsProject Condition="'$(ServerKeybindsProject)' == '' And Exists('$(MSBuildThisFileDirectory)..\ServerKeybinds\ServerKeybinds.csproj')">$(MSBuildThisFileDirectory)..\ServerKeybinds\ServerKeybinds.csproj</ServerKeybindsProject>
    <ServerKeybindsProject Condition="'$(ServerKeybindsProject)' == '' And Exists('$(MSBuildThisFileDirectory)..\..\..\ServerKeybinds\ServerKeybinds.csproj')">$(MSBuildThisFileDirectory)..\..\..\ServerKeybinds\ServerKeybinds.csproj</ServerKeybindsProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(ServerKeybindsProject)" Private="false" />
  </ItemGroup>
  <Target Name="RequireServerKeybindsProject" BeforeTargets="BeforeBuild" Condition="!Exists('$(ServerKeybindsProject)')">
    <Error Text="ServerKeybinds.csproj not found. Pass -p:ServerKeybindsProject=&lt;path&gt;." />
  </Target>
```

`Private="false"` keeps the consumer's output free of its own `ServerKeybinds.dll` copy. A consumer
build with `-p:DeployToLocalServer=true` also refreshes every deployed `ServerKeybinds.dll` copy
under the local LabAPI install, because the `ProjectReference` builds this project with the same
global property.

Standalone build: `dotnet build ServerKeybinds.csproj -c Release -p:SCP_SL_MANAGED=<path to SCPSL_Data\Managed>`
produces `bin/Release/net48/ServerKeybinds.dll`.

## Installation

Install **exactly one copy per server**, in the folder that port's `LabAPI/LabApi-<port>.yml`
loader reads. On SR1 production that is `LabAPI/plugins/7777`: its loader lists only the port
folders, so `plugins/global` and `dependencies/global` are never read. Check the host runbook
before placing the file. A copy in an unread folder is silently ignored, and a stale copy in the
read folder wins over a fresh copy anywhere else.

The first `Enable()` logs which build is active:

```
[ServerKeybinds] ServerKeybinds <version> (API 6) loaded from <path> sha256 <hash>
```

If a second ServerKeybinds assembly is loaded in the same process, the registry logs an **error**
naming that copy's path, version and hash beside the line above. Two copies mean two registries
and two join-send owners; the stale or forked copy must be removed. Each enabled block also logs
`'<owner>' enabled block <base> under <category> [<entries>]`, and the first Enable logs the
registration of the `keybinds` RA command.

## Id allocation

Every consumer owns one fixed, 1000-wide block: id = `base + local`, where local 0 is the block
header and 1..999 are entries. `SssIdBlocks` is the reservation table; the runtime is the
enforcement: `ClaimBlock` rejects a base that is not 1000-aligned or equals `RegistryHeaders`, and
`Enable()` throws when two blocks share a base, whatever constants they used. A plugin may ship
with a new aligned base and add the table row in the same change; adding a row is bookkeeping,
not a contract change. Settings written into `DefinedSettings` by anything other than a block are
foreign; see [Foreign settings](#foreign-settings).

| Base | Constant | Owner |
| --- | --- | --- |
| 1060000 | `Scp106` | scp-106 abilities |
| 1090000 | `Reinforcements` | reinforcements-system SRA deploy |
| 1091000 | `ReinforcementsMedic` | reinforcements-system Medic |
| 1092000 | `SerpentsHand` | reinforcements-system Serpent's Hand |
| 1100000 | `GocNuke` | goc-nuke (reserved, unused) |
| 1110000 | `SpinBot` | SpinBot observer controls |
| 1120000 | `InvincibleWarMark` | InvincibleWarMark ability |
| 1130000 | `ScpslBotWarmup` | SCPSLBot warmup controls (bot lane) |
| 1131000 | `StatsBots` | StatsBots display preferences and title picker (bot lane) |
| 1132000 | `ScpslBotTools` | SCPSLBot staff tools (bot lane) |
| 1140000 | `Scp966` | SCP-966 and night vision |
| 1150000 | `Scp5kGanzir` | Ganzir aircraft, jetpack and naval insertion |
| 1160000 | `CementExamples` | scpsl-plugin-examples teaching code only; never deployed beside a product |
| 1170000 | `AircraftCarrier` | AircraftCarrier barrage and drone |
| 1200000 | `SpatialSurveyMarkers` | SpatialSurveyMarkers survey mode |
| 9100000 | `ScpTiers` | ScpTiers ability keybinds; keeps its historical ids as locals 100+ |
| 23000 | `RegistryHeaders` | the registry's own category headers; not claimable |
| 24000 | `GlobalMusic` | shared plugin music (24001 mute, 24002 volume) |
| 25000 | `MvpSystem` | MvpSystem music toggle at local 1 |
| 26000 | `EffectDisplay` | reserved; the plugin still uses 2030/2031 |
| 27000 | `NewPlayerGuide` | single opt-out toggle defined by SB_WelcomeMessage, read by reinforcements-system |
| 28000 | `ProjectMer` | tool-gun schematic selector |
| 29000 | `AdditionalNameTags` | prefix toggle, custom name text and mode |
| 30000 | `PlayerBadge` | badge picker at local 1 |
| 31000 | `SecretLabNAudioDemo` | SecretLabNAudio.Demo grab-slider keybind at local 1 |
| 530000 | `CustomizableUi` | CustomizableUIMeow HUD toggles; keeps its historical ids as locals 210+ |

## Usage

```csharp
KeybindBlock block = KeybindRegistry.ClaimBlock(SssIdBlocks.MyPlugin, "My Plugin")
    .InCategory(SettingsCategory.Gameplay)
    .Header("我的插件")
    .VisibleTo(HasAccess)
    .AddTextArea(9, "建议按键需要玩家自行采纳。")
    .Add(1, "切换", KeyCode.V, "切换功能。", OnToggle)
    .AddDropdown(2, "模式", new[] { "A", "B" }, 0, "选择模式。", OnMode)
    .AddSlider(3, "速度", 0f, 100f, 50f, true, "0", "{0}", "设置速度。", OnSpeed)
    .AddTwoButtons(4, "提示", "开启", "关闭", defaultIsB: false, "关闭提示。", OnTips);
block.Enable();   // in Plugin.Enable
// block.Disable(); in Plugin.Disable
```

Local ids must be unique within the block and 0 is reserved for `Header(name)`; violations throw
at registration. Entries added after `Enable()` re-merge immediately. `block.SettingId(local)`
returns the absolute id, for example to render key glyphs elsewhere. A block emits its headers,
then text areas, then keybinds, then value settings. Every label, hint and caption is authored and
localised by the consumer.

## Entry types

- **`Header(name)` / `Header(local, name)`** — a group header. It renders as a reduced-padding
  sub-header beneath the category header the registry synthesises.
- **`AddTextArea(local, content, foldout, collapsedText)`** — read-only text directly under the
  header, above the keybinds. `SSTextArea` has `UserResponseMode.None`, so it costs no client
  response and cannot be forged back at the server.
- **`Add(local, label, defaultKey, hint, onPressed, onReleased?, preventInteractionOnGui,
  allowSpectatorTrigger)`** — a keybind. `onPressed` fires on the rising edge, `onReleased` on
  the falling edge for hold-to-act. The default key is only a **suggestion**: the native parameter
  is `SuggestedKey` and `SerializeEntry` sends nothing else; `AssignedKeyCode` is client-owned and
  the player must click the client's apply-suggestion control. Say so in an `AddTextArea` next to
  the keybinds. Presses are latched per UserId, so a repeated press without a release is ignored;
  role change, registry rebuild and round restart release latches and dispatch the release edge
  first, so a hold-to-act consumer is never left held.
- **`AddDropdown(local, label, options, defaultIndex, hint, onChanged, entryType)`** — the
  callback receives the validated, clamped index.
- **`AddTwoButtons(local, label, optionA, optionB, defaultIsB, hint, onChanged)`** — the native
  on/off control; the callback receives `true` when the player picks option B. The overload taking
  `Func<Player, bool> defaultIsBFor, bool fallbackIsB` decides the starting position per player at
  every personalised send (the fallback shapes only the shared array nobody receives; a throwing
  resolver falls back too). The client reports its value on **acquisition** as well as on change,
  so a callback is not proof the player touched anything: compare against
  `KeybindRegistry.DefaultTwoButtonsFor(player, settingId)`, which returns the default that player
  was actually sent. **PlayerPrefs caveat:** the client stores values under
  `SrvSp_<server>_<typeCode>_<settingId>` (`ServerSpecificSettingBase.GeneratePrefsKey`), and the
  type code is part of the key, so converting an existing dropdown to a two-button toggle silently
  resets every player's saved choice. Do that deliberately.
- **`AddSlider(local, label, min, max, default, integer, valueFormat, displayFormat, hint,
  onChanged)`** — the callback receives the value clamped to the range.
- **`AddButton(local, label, buttonText, hint, onPressed, holdTimeSeconds?)`** — fires once per
  click, or once per completed hold when a hold time is given. A button has no stored value, so
  nothing fires on acquisition.
- **`AddPlaintext(local, label, hint, onChanged, placeholder, characterLimit, contentType)`** — a
  text field. Like every value setting it reports on acquisition too, so the first callback
  carries the player's saved text (or an empty string), not an edit. Text is cut to the limit.
- **`AddNative(local, label, factory, onResponse?)`** — the escape hatch for any
  `ServerSpecificSettingBase` the typed helpers do not cover. The factory receives the absolute
  setting id and the recipient of the personalised send (null for the shared array), is called once
  per send, and **must** construct the entry with that id; a different id throws. `onResponse`
  receives the raw deserialised client response for that id. The registry applies its visibility
  gate and nothing else, so a keybind registered this way sees both edges and gets no press latch.

Per-player entries (`AddDropdownForPlayer`, `AddButtonForPlayer`), validated selections and
interest-routed refreshes are described in [docs/per-player-entries.md](docs/per-player-entries.md).

## Categories and order

Blocks are sorted by `(category, Order, base id)`, so the menu is identical on every server
regardless of plugin load order. The registry emits **one** `SSGroupHeader` per category that has a
visible block, at the stable id `SssIdBlocks.RegistryHeaders + (int)category`; a category with no
visible block emits no header for that player. `InCategory` rejects a value that is not in
`SssIdBlocks.AllCategories`.

| Order | `SettingsCategory` | For |
| --- | --- | --- |
| 0 | `Gameplay` | ability keybinds and anything that changes what the player can do |
| 10 | `Display` | HUD, nametag and other presentation toggles |
| 20 | `Announcements` | opt-in/opt-out switches for notices, music and guidance |
| 30 | `Tools` | staff/observer/authoring controls, usually with `VisibleTo` |
| 100 | `Other` | the fallback for a block that never called `InCategory` |

`Order(int)` sorts within a category, lower first, default 0. Use a negative value to pin the
settings players reach for most to the top; reinforcements-system pins its ability keybinds at
`-1000`. `InCategory` and `Order` are presentational and change no setting id, so re-categorising
never costs a player a saved value.

Registry entries are emitted **before** foreign ones. Plugins that merge into `DefinedSettings`
themselves keep their own relative order and are otherwise untouched.

Category header labels follow `KeybindRegistry.Language`: empty or `cn` renders Chinese, `en`
renders English. It is one global value (`DefinedSettings` is one array), set by a consumer from
its own config; last writer wins, so a server should set it from one plugin only.

## Visibility

`VisibleTo(predicate)` restricts the whole block. A hidden player receives none of its entries, and
any response the client sends for those ids is swallowed server-side. Visibility is checked before
a key press can enter the latch; a press that was accepted still receives its release edge if a
role change hides the block before the key comes up. Visibility is presentation, not
authorisation: callbacks must still check the item, role, cooldown or permission they act on.

## Delivery and reconcile

The native settings pack is a replace, not an add, so the registry suppresses the native join send
(`SendOnJoinFilter`) and sends each player a personalised collection with wire version 4:

- **Join:** 0.75 s after the client is ready; readiness is retried up to three times at 1.5 s.
- **Acknowledgement:** a native status report for version 4, or any registry-owned setting
  response, acknowledges a send; an unacknowledged join or refresh send is repeated up to three
  times. The native status version is the version the player accepted in the menu, not a transport
  receipt, so a player who never opened the tab can legitimately stay at version 0.
- **Reconcile:** every 30 s, each ready player who has had no registry send in the last 30 s gets a
  paced repair send. It is skipped while an acknowledgement is pending, the settings tab is open,
  or a key is latched; a latch defers at most one further interval, then is released as stale so a
  lost key-up cannot stop reconciliation.
- **Rebuild:** every `Enable`, `Disable` or late entry addition re-merges the shared array and
  re-sends to everyone. `KeybindRegistry.RefreshPlayer(player)` re-sends to one player with
  acknowledgement.

Sent two-button defaults, send audits, acknowledgement state and key latches are keyed by UserId, so
a reused session player id never inherits another player's state. Every per-player store is pruned
when the player leaves; latches and pending acknowledgements are cleared on round restart.

## Foreign settings

The registry is the only sanctioned writer of `ServerSpecificSettingsSync.DefinedSettings`. A
setting that another plugin writes there without a block is foreign, and
`KeybindRegistry.ForeignPolicy` decides what happens to it:

- `Block` (default): the entry is stripped from the shared array on every rebuild and on the next
  send of any kind, so it never reaches a client, and the client's responses for that id fail the
  game's own prevalidation. Each id is logged once as a warning and listed by `keybinds foreign`.
- `Merge`: the previous additive behaviour, with registry entries first and foreign entries after
  in their own order. Use it only while a server must run a plugin that has not been ported.

A foreign plugin that calls the native `SendToAll` between two registry sends can still reach
clients once; the reconcile send replaces that collection within its 30-second window. Set the
policy from a consumer's config like `Language`, or at runtime with `keybinds foreign block|merge`;
a change rebuilds immediately.

## Diagnostics

- `KeybindRegistry.Debug` (static bool, consumer-set like `Language`; last writer wins) gates the
  per-send audit line and the readiness/acknowledgement retry lines. Send reasons are `join`,
  `join-ready-retry`, `join-ack-retry`, `refresh`, `rebuild` and `reconcile`.
- `KeybindRegistry.PressTrace` gates a trace line at every routing decision: swallowed value
  responses, unknown ids, latch outcomes and which block/binding a press was routed to.
- `KeybindRegistry.Registered`, `TryGetSendAudit`, `PressedFor` and `DefaultTwoButtonsFor` expose
  the same state to consumers.
- RA command **`keybinds`** (alias `skb`, requires `ServerConsoleCommands`), self-registered by
  the registry because LabAPI does not scan libraries for commands:
  - `keybinds status <id|name>` — entries the player would receive now, the last send (UTC time,
    count, reason), pending acknowledgement attempt, pressed latches, the client's accepted
    version and whether the settings tab is open.
  - `keybinds resend <id|name>` — re-pushes the personalised collection to that player.
  - `keybinds trace on|off`, `keybinds foreign [block|merge]` — toggles `PressTrace` at runtime.

## Shared plugin music

Players use **Announcements → Plugin music** to control music from ReinforcementsSystem,
ScriptedWarhead (Omega) and Global Music Player together: a two-button **On / Muted** toggle at
id **24001** (`PluginMusicPreferences.SettingId`) and a **Plugin music volume** slider (0-100 %) at
id **24002** (`PluginMusicPreferences.VolumeSettingId`). Both change delivery during playback
without restarting tracks or changing cinematic/countdown timing. Reinforcement spatial effects and
native game audio are unaffected. Omega's custom MP3 is one mixed track, so muting it also mutes
the speech embedded in that file; its subtitles and native fallback announcement remain.

ServerKeybinds owns the preference; GMP is only a consumer and is not required by the others. The
setting exists while at least one consumer holds a lease, and disabling one consumer never removes
it from the others. Each consumer acquires on enable and disposes **after stopping its audio**:

```csharp
IDisposable musicPreferences = PluginMusicPreferences.Acquire();
// Set ControllerId first; the recipient predicate belongs to that controller's transmitter.
speaker.ValidPlayers = player => ExistingAudience(player) && PluginMusicPreferences.CanReceiveMusic(player);
// On shutdown: stop/destroy your speakers, then musicPreferences.Dispose().
```

**Mute.** Compose `CanReceiveMusic` with the existing audience predicate; never replace audience
restrictions. Opt-outs are mirrored by UserId to
`LabAPI/configs/<port>/ServerKeybinds/plugin_music_muted.txt` and read into memory before settings
arrive; no file reads occur on the audio path. `SetMuted` persists a console choice, but a native
client-owned toggle cannot be repositioned from the server: the menu may still show its previous
choice, a duplicate menu response does not undo the console change, and a changed menu choice or
the client's saved choice on reconnect wins. GMP's `gmpmute`/`gmpunmute` aliases set this same
preference; use the menu for a lasting client preference and `gmpmute status` for the effective
state.

**Volume.** A speaker's network volume is one value every listener shares, so a consumer applies
the preference server-side: scale each PCM frame by `VolumeOf(player)` before Opus encoding, group
listeners with the same percent so each distinct value costs one encode, and send each group its
own `AudioMessage` on the same controller id (Global Music Player's `PerListenerTransmitter` is the
reference). `VolumePercentOf` / `VolumeOf` are safe on audio threads; `SetVolumePercent` persists
a console choice and `VolumePercentChanged` fires on the game thread. Values are mirrored by
UserId to `LabAPI/configs/<port>/ServerKeybinds/plugin_music_volume.txt` (`userId<TAB>percent`;
100 is absent). A consumer that keeps the stock single-stream transmitter plays at full volume for
everyone; the mute still applies through `CanReceiveMusic`.

Use this predicate for music only; it is not an interceptor for unrelated audio, and a new plugin
must integrate it explicitly. Labels follow `KeybindRegistry.Language`.

Native evidence: `../.references/LabAPI/LabApi/Features/Wrappers/AdminToys/SpeakerToy.cs`
(`ValidPlayers`), `../.references/LabAPI/LabApi/Features/Audio/AudioTransmitter.cs` (`Transmit`,
packet recipient filtering) and
`../.references/Decompiled/DedicatedServer/Assembly-CSharp/UserSettings/ServerSpecific/SSTwoButtonsSetting.cs`
(`SendValueUpdate` and `DeserializeUpdate` restrict value updates to server-only settings).

## 中文

新协作者请先阅读 [中文协作者入门](docs/入门.md) 与
[完整教学示例](https://github.com/sl-plugins-cement/scpsl-plugin-examples/tree/onboarding-foundations-zh)。

`ServerKeybinds.dll` 是元仓库插件使用的 SCP:SL“服务器专属设置”进程级唯一管理器，是共享依赖库而非独立玩法插件。它统一负责对 `ServerSpecificSettingsSync.DefinedSettings` 的加法合并、个性化加入发送、菜单顺序、按玩家可见性、按键按下/释放边沿、可靠投递与 ID 冲突检测。使用插件不得再创建并行的合并路径或自行调用 `SendToPlayer`。`CustomItems.dll` 硬依赖本库。

**契约。** 公开接口只增不改：成员永不删除或改签名，任何早期构建的使用者都能在当前构建上运行。`KeybindRegistry.ApiVersion` 仅用于启动日志诊断，任何代码不得据此分支，也不要在文档中写“需要 API N”。所有服务器都运行最新构建；程序集未强命名，CLR 只按简单名绑定。使用者通过上文英文部分给出的 `ProjectReference` 约定从源码构建本库；带 `-p:DeployToLocalServer=true` 的使用者构建会一并刷新本地 LabAPI 安装中所有已部署的 `ServerKeybinds.dll`。

**安装。** 每台服务器只安装一份，放在该端口 `LabAPI/LabApi-<port>.yml` 加载器实际读取的目录中；SR1 生产服只加载端口目录，因此放 `LabAPI/plugins/7777`，`plugins/global` 与 `dependencies/global` 均不会被读取。放在未读取目录中的副本会被静默忽略，已读取目录中的旧副本会生效。首次 `Enable()` 输出 `[ServerKeybinds] ServerKeybinds <version> (API 6) loaded from <path> sha256 <hash>`；若进程中还加载了第二个 ServerKeybinds 程序集，会额外输出一条错误日志并给出其路径，此时必须删除陈旧或分叉的副本。

**ID 分配。** 每个使用者占用一个 1000 宽的固定区块，本地 ID 0 为标题，1–999 为条目。`SssIdBlocks` 是登记表，运行时才是强制：`ClaimBlock` 拒绝非 1000 对齐或等于 `RegistryHeaders` 的基址，`Enable()` 在两个区块基址相同时抛出异常。插件可以先带着新的对齐基址发布，并在同一次修改中补上表格行。当前登记见上文表格（1060000 Scp106 … 1200000 SpatialSurveyMarkers，23000 注册表标题，24000 插件音乐，25000–30000、530000 与 9100000 为配置类及已接入的第三方插件，1130000–1132000 为机器人服）。

**外部设置。** 注册表是 `DefinedSettings` 的唯一合法写入者。其他插件绕过注册表写入的设置称为外部设置，由 `KeybindRegistry.ForeignPolicy` 决定处理方式：`Block`（默认）在每次重建及下一次发送时剔除它们，使其永远到不了客户端，并对每个 ID 只记录一次警告，`keybinds foreign` 可列出；`Merge` 保留旧的追加合并行为，仅在服务器必须运行未接入插件时使用。运行时可用 `keybinds foreign block|merge` 切换，切换后立即重建。

**条目类型。** `Header`（分组标题，作为分类标题下的子标题显示）；`AddTextArea`（只读说明，显示在标题下、按键上，客户端不回传）；`Add`（按键：上升沿/下降沿回调；默认键只是 `SuggestedKey` 建议，玩家必须自行采纳，请用说明文本告知；按 UserId 锁存，角色变化、重建与回合重启会先派发释放再清除锁存）；`AddDropdown`（回传经校验的索引）；`AddTwoButtons`（原生开关，选中 B 时回调 `true`；另有按玩家决定初始位置的重载，客户端在获取时也会回报值，请与 `DefaultTwoButtonsFor` 返回的已发送默认值比较；PlayerPrefs 键包含类型码，把下拉改为双按钮会重置玩家已保存的选择）；`AddSlider`（回传裁剪后的值）；`AddButton`（每次点击或完成长按触发一次，无存储值，获取时不触发）；`AddPlaintext`（文本框，获取时同样回报已保存文本或空串）；`AddNative`（兜底：工厂函数收到绝对 ID 与个性化发送的接收者，必须用该 ID 构造条目，回调收到原始响应，注册表只做可见性过滤，不做锁存）。

**分类与顺序。** 区块按（分类、`Order`、基址）排序；每个分类只生成一个 `SSGroupHeader`，ID 为 `RegistryHeaders + (int)category`，对某玩家全部不可见的分类不发送标题。分类：`Gameplay`(0)、`Display`(10)、`Announcements`(20)、`Tools`(30)、`Other`(100，未调用 `InCategory` 的默认归属)。`Order` 负值可置顶（reinforcements-system 的能力按键为 `-1000`）。分类与顺序仅影响展示，不改变任何 ID。注册表条目排在外部条目之前，外部条目保持其相对顺序。分类标题语言由 `KeybindRegistry.Language` 决定（空或 `cn` 为中文，`en` 为英文），全局唯一，后写者生效。

**可见性。** `VisibleTo` 限制整个区块：不可见玩家收不到条目，其回传被丢弃；按键进入锁存前先检查可见性；已接受的按下即使随后因角色变化不可见仍会收到释放。可见性只是展示，回调仍须检查物品、角色、冷却与权限。

**投递与对账。** 原生设置包是整体替换，因此注册表抑制原生加入发送，改为以 wire version 4 发送个性化集合：加入后 0.75 秒发送，就绪最多重试三次；version 4 状态报告或任意注册表设置回传视为确认，未确认最多重发三次（状态版本是玩家在菜单中接受的版本，从未打开设置页的玩家保持 0 属正常）；每 30 秒对 30 秒内无发送的玩家做节流修复发送，待确认、设置页打开或按键锁存时跳过，锁存最多再延后一个周期后按陈旧释放。`Enable`/`Disable`/后续添加条目会重建并向所有人重发；`RefreshPlayer` 向单个玩家重发。所有按玩家状态以 UserId 为键，离开时清理，回合重启时清空锁存与待确认。

**诊断。** `KeybindRegistry.Debug` 控制发送与重试日志（原因：`join`、`join-ready-retry`、`join-ack-retry`、`refresh`、`rebuild`、`reconcile`）；`PressTrace` 控制按键路由跟踪。RA 命令 **`keybinds`**（别名 `skb`，需 `ServerConsoleCommands`）：`status <ID|名称>`、`resend <ID|名称>`、`trace on|off`。

**共享插件音乐。** 玩家在 **公告与提示 → 插件音乐** 中统一控制增援系统、Omega 核弹与全服音乐播放器的音乐：**开启 / 静音** 开关（ID 24001）与 **插件音乐音量** 滑块（0–100%，ID 24002）。切换即时生效，不重启曲目，不影响演出、字幕与倒计时；增援空间音效与原生音频不受影响；Omega 的 MP3 为混合音轨，静音会同时屏蔽其中语音，独立字幕与原生备用广播保留。本库拥有该偏好，GMP 只是使用者。使用者启用时 `PluginMusicPreferences.Acquire()`，将 `CanReceiveMusic` 与原有听众条件取交集，停用时先停止销毁音频再释放租约。音量需在服务端应用：Opus 编码前按 `VolumeOf(player)` 缩放音频帧，按相同百分比分组各编码一次并在同一控制器 ID 上分别发送（参考 GMP 的 `PerListenerTransmitter`）；仍用原生单流发送器的使用者全音量播放，静音仍通过 `CanReceiveMusic` 生效。`VolumePercentOf`/`VolumeOf` 可在音频线程调用；`SetMuted`/`SetVolumePercent` 保存控制台选择，`VolumePercentChanged` 在游戏线程触发。偏好按 UserId 镜像到 `LabAPI/configs/<port>/ServerKeybinds/plugin_music_muted.txt` 与 `plugin_music_volume.txt`（100 不保存），音频路径不读文件。原生客户端拥有的开关无法由服务器改变位置：菜单可能仍显示旧选择，重复回传不会撤销控制台修改，而新的菜单选择或重连时客户端保存的选择优先。GMP 的 `gmpmute`/`gmpunmute` 设置同一偏好，`gmpmute status` 查看实际状态。该谓词仅用于音乐，新插件必须主动接入。

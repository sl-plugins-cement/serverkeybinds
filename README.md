# ServerKeybinds

## English

`ServerKeybinds.dll` is the process-wide owner of SCP:SL Server-Specific Settings used by metarepo plugins. It is a shared dependency library installed under LabAPI's `dependencies/global` directory, not a standalone gameplay plugin.

API 6 supports group headers, keybinds, dropdowns, sliders, native two-button toggles, and per-player block visibility through one claimed 1000-ID block per consumer, and it owns the **order** of the menu. API 6 adds a shared per-player music volume step beside the API 5 mute toggle. The registry owns the single additive merge into `ServerSpecificSettingsSync.DefinedSettings`, personalized joining-player sends, setting-response visibility gates, rising/falling key edges, and collision detection. Consumer plugins must not create a parallel global-settings merge path.

Typical usage:

```csharp
KeybindBlock block = KeybindRegistry.ClaimBlock(SssIdBlocks.MyPlugin, "My Plugin")
    .InCategory(SettingsCategory.Gameplay)
    .Header("My Plugin")
    .VisibleTo(HasAccess)
    .Add(1, "Toggle", KeyCode.V, "Toggle the feature.", OnToggle)
    .AddDropdown(2, "Mode", new[] { "A", "B" }, 0, "Select a mode.", OnMode)
    .AddSlider(3, "Speed", 0f, 100f, 50f, true, "0", "{0}", "Set speed.", OnSpeed)
    .AddTwoButtons(4, "Show tips", "On", "Off", defaultIsB: false, "Turn the tips off.", OnTips);
block.Enable();
```

Call `Disable()` during plugin shutdown. Add a fixed base to `SssIdBlocks` before introducing a new consumer.

### Shared plugin music (API 5, volume step in API 6)

Players use **Announcements → Plugin music → On / Muted** to control music from
ReinforcementsSystem, ScriptedWarhead (Omega), and Global Music Player together. The setting
changes delivery during playback, without restarting tracks or changing cinematic/countdown timing.
Reinforcement spatial sound effects and native game audio remain audible. Omega's custom MP3 is
one mixed track: muting it also mutes speech embedded in that file; its separate subtitles and native
fallback announcement remain enabled.

ServerKeybinds owns this preference. GMP is only a consumer and is not required by the other plugins.
The setting exists while at least one consumer is enabled. Disabling one consumer never removes it
from the others. Install ServerKeybinds 5 with these consumer builds in LabAPI dependencies.

Each consumer acquires a lease on enable and disposes it **after stopping its audio** on disable:

```csharp
IDisposable musicPreferences = PluginMusicPreferences.Acquire();
// Set ControllerId first; the recipient predicate belongs to that controller's transmitter.
speaker.ValidPlayers = player => ExistingAudience(player) && PluginMusicPreferences.CanReceiveMusic(player);
// On shutdown: stop/destroy your speakers, then musicPreferences.Dispose().
```

**Volume (API 6).** The same block also offers a **Plugin music volume** slider (25-100%) at ID
**24002** (`PluginMusicPreferences.VolumeSettingId`). A speaker's volume is one network value every listener
shares, so the slider value snaps to the nearest entry of `PluginMusicPreferences.VolumeSteps`
(100/75/50/25%, `NearestVolumeStep`), and a consumer delivers per-player volume by running one speaker per
step and routing each listener to exactly one of them:

```csharp
for (int step = 0; step < PluginMusicPreferences.VolumeSteps.Count; step++)
{
    int captured = step;
    speaker[step].ControllerId = (byte)(baseId + step);
    speaker[step].Volume = master * PluginMusicPreferences.VolumeSteps[step];
    speaker[step].ValidPlayers = p => Audience(p) && PluginMusicPreferences.CanReceiveMusic(p)
                                       && PluginMusicPreferences.VolumeStepOf(p) == captured;
}
// Feed every speaker the same samples in the same frame; the transmitters stay in lockstep.
```

`VolumeStepOf(player)` / `VolumeOf(player)` are safe on audio threads; `SetVolumeStep(player, step)` persists a
console choice and `VolumeStepChanged` fires on the game thread. Steps are mirrored by UserId to
`LabAPI/configs/<port>/ServerKeybinds/plugin_music_volume.txt` (`userId<TAB>step`, step 0 is absent).
A consumer that keeps a single speaker ignores the step and plays at full volume; today only Global Music
Player is tiered.

Use this predicate for music only. Future plugins must explicitly integrate it; this is not an
interceptor for unrelated audio. Labels follow `KeybindRegistry.Language` (empty/cn → Chinese, en → English).
The native two-button type and ID **24001** retain default-ID GMP client preferences. Custom GMP IDs
are no longer used. Opt-outs are atomically mirrored by UserId to
`LabAPI/configs/<port>/ServerKeybinds/plugin_music_muted.txt`, read into memory before settings arrive.
No file reads occur on the audio path. SSS choices are also saved by the native client.

GMP's `gmpmute`/`gmpunmute` aliases set this same preference. Native client-owned settings cannot be
repositioned from a server command: the menu may still display its previous choice. Duplicate menu
responses do not undo a console change; a changed menu choice or the saved client choice on reconnect
wins. Use the menu for a lasting client preference, and `gmpmute status` for the effective state.

Native evidence: `../.references/LabAPI/LabApi/Features/Wrappers/AdminToys/SpeakerToy.cs` (`ValidPlayers`),
`../.references/LabAPI/LabApi/Features/Audio/AudioTransmitter.cs` (`Transmit`, packet recipient filtering),
and `../.references/Decompiled/DedicatedServer/Assembly-CSharp/UserSettings/ServerSpecific/SSTwoButtonsSetting.cs`
(`SendValueUpdate` and `DeserializeUpdate` restrict value updates to server-only settings).

### Categories (API 3)

Before API 3 the menu order was `Dictionary<int, KeybindBlock>` enumeration order — that is, plugin load
order — so the settings list read as an unsorted pile. Blocks are now sorted by `(category, base id)`, the
registry emits **one** `SSGroupHeader` per category, and each block's own header renders beneath it as a
reduced-padding sub-header.

| Order | `SettingsCategory` | For |
| --- | --- | --- |
| 0 | `Gameplay` | ability keybinds and anything that changes what the player can do |
| 10 | `Display` | HUD, nametag and other presentation toggles |
| 20 | `Announcements` | opt-in/opt-out switches for notices, music and guidance |
| 30 | `Tools` | staff/observer/authoring controls, usually with a `VisibleTo` filter |
| 100 | `Other` | the fallback for a block that never called `InCategory` |

Within a category, blocks sort by `Order(int)` (lower first, default 0) and then by base id. Use a negative
`Order` to pin the settings players reach for most to the top — reinforcements-system pins its ability
keybinds at `-1000`, because nothing that plugin does works until those two keys are accepted.

**Registry settings are emitted BEFORE foreign ones.** `DefinedSettings` is shared with plugins that merge
into it themselves (HUD toggles, music mutes), and appending used to push every registry setting below all of
them — so the ability keybinds sat at the very bottom of the menu no matter how the categories were ordered.
Foreign entries keep their own relative order and are otherwise untouched.

`InCategory` and `Order` are purely presentational and change **no setting id**, so re-categorising never
costs a player their saved values. A category whose only block is hidden from a given player emits no header
for them.

`AddTextArea` adds a read-only explanation that renders directly under the block's header, above its keybinds
and settings — a block emits headers, then text areas, then keybinds, then value settings. `SSTextArea` has
`UserResponseMode.None`, so it costs no client response and cannot be forged back at the server.

**Keybind defaults are only a suggestion.** The native parameter is `SuggestedKey`, and `SerializeEntry`
sends only that: `AssignedKeyCode` is client-owned and the server can never set it. The client shows an
"apply suggestion" control the player has to click, so a suggested key is a one-click default, not a binding.
Explain that in an `AddTextArea` next to the keybinds rather than assuming players will discover it.

Category headers are synthesised at `SssIdBlocks.RegistryHeaders + (int)category`, so they have stable ids
inside the allocation scheme rather than the label hash an id-less `SSGroupHeader` would use. Their language
comes from `KeybindRegistry.Language` (empty or `cn` for Chinese, `en` for English; Chinese is the fallback).
Every other string in the menu is authored and localised by the consuming plugin.

### `AddTwoButtons` and the PlayerPrefs caveat

`AddTwoButtons` maps to the native `SSTwoButtonsSetting` and is the right control for an on/off switch; a
two-option dropdown works but reads as a list the player has to open. The callback receives `true` when the
player picks option **B**.

The client stores each value in PlayerPrefs under `SrvSp_<server>_<typeCode>_<settingId>`
(`ServerSpecificSettingBase.GeneratePrefsKey`), and **the type code is part of the key**. Converting an
existing dropdown to a two-button toggle therefore silently resets it to its default for every player who had
already chosen a value. Do that deliberately, not as a drive-by tidy-up.

### Reliable delivery and diagnostics (API 4)

The native settings pack is a replace operation, not an additive one. API 4 therefore sends its personalized
pack with wire version 4, retries join readiness and acknowledgement a bounded three times, and performs a
paced repair send after 30 seconds without a registry send. Repair defers while the player's settings tab is
open or a key is latched. A latch can defer one additional 30-second repair interval; after that it is
released as stale before repair so a lost key-up cannot disable reconciliation indefinitely. A native status report for
version 4 or any registry-owned setting response acknowledges a pending delivery. The native status version
is the version the player has accepted in the menu, not a transport receipt; a player who has never opened
the tab can legitimately remain at version 0.

- `SentTwoButtonDefaults`, send audits, acknowledgement state, and key latches are keyed by UserId. This
  prevents a reused session player id from inheriting another player's defaults or diagnostics.
- Visibility is checked before a key press can enter the latch. Role change, registry rebuild, round restart,
  and disconnect release or clear the corresponding state.

- `KeybindRegistry.Debug` (static bool, consumer-set like `Language`; last writer wins) gates per-send,
  readiness-retry, and acknowledgement-retry diagnostics. Send reasons include `join`,
  `join-ready-retry`, `join-ack-retry`, `refresh`, `rebuild`, and `reconcile`.
- `KeybindRegistry.PressTrace` gates a trace line at every keybind routing decision: swallowed value
  responses, unknown setting ids, press/release latch outcomes, and which block/binding a press was
  routed to.
- RA command **`keybinds`** (alias `skb`, requires `ServerConsoleCommands`), self-registered by the
  registry because LabAPI does not scan dependency libraries for commands:
  - `keybinds status <id|name>` — the entry count the player would receive now, the last recorded send
    (UTC time + count + reason), pending acknowledgement attempt, pressed latches, and the client's
    accepted settings version.
  - `keybinds resend <id|name>` — re-pushes the personalized settings collection to that player.
  - `keybinds trace on|off` — toggles `PressTrace` at runtime.
- State hygiene: press latches clear on role change, rebuild, and round restart, and every per-player
  store is pruned when the player leaves.

`CustomItems.dll` hard-depends on ServerKeybinds. Any custom-item plugin requiring settings or keybinds must use this registry.

## 中文

`ServerKeybinds.dll` 是元仓库插件使用的 SCP:SL“服务器专属设置”进程级唯一管理器。它是安装在 LabAPI `dependencies/global` 目录中的共享依赖库，不是独立游戏插件。

API 6 支持分组标题、按键绑定、下拉菜单、滑条、原生双按钮开关及按玩家控制区块可见性，并在 API 5 的静音开关旁新增每人独立的音乐音量档位；每个使用者占用一个 1000 ID 的固定区块，并且由注册表统一决定菜单的**显示顺序**。注册表统一负责对 `ServerSpecificSettingsSync.DefinedSettings` 的加法合并、个性化加入发送、设置响应权限过滤、按键按下/释放边沿以及冲突检测。使用插件不得再创建并行的全局设置合并路径。

插件停用时必须调用 `Disable()`。新增使用者前，应先在 `SssIdBlocks` 中分配固定基址。

### 共享插件音乐（API 5，音量档位见 API 6）

玩家在 **公告 → 插件音乐 → 开启 / 静音** 中统一控制增援系统、Omega 核弹及全服音乐播放器的音乐。
播放过程中切换立即影响后续音频包，不重启曲目，也不改变演出、字幕或核弹倒计时。
增援空间音效及原生游戏声音不受影响。Omega 的 MP3 是混合音轨：静音也会屏蔽文件中的语音，
但独立字幕及原生备用广播仍保留。

设置由 ServerKeybinds 提供；GMP 只是使用者，其他插件不依赖 GMP。至少一个使用者启用时显示一个设置；
停用其中一个不会影响其他使用者。请随这些插件构建一同安装 ServerKeybinds 5 依赖库。
使用者启用时调用 `PluginMusicPreferences.Acquire()`，将 `CanReceiveMusic` 与原有听众条件取交集，
停用时先停止并销毁音频，再释放租约。未来插件必须主动接入；不会拦截无关音频。

**音量（API 6）。** 同一区块还提供 **插件音乐音量** 滑块（25-100%，ID **24002**，
`PluginMusicPreferences.VolumeSettingId`），滑块值会吸附到 `VolumeSteps` 最近的一档（100/75/50/25%，
`NearestVolumeStep`）。扬声器音量是所有听众共享的同一个网络值，因此使用者需要按
`PluginMusicPreferences.VolumeSteps` 的每个档位各建一个扬声器，并用 `VolumeStepOf(player) == 档位`
把每名玩家路由到其中一个，同一帧向所有扬声器喂入相同采样即可保持同步。`VolumeStepOf` / `VolumeOf`
可在音频线程调用；`SetVolumeStep` 保存控制台选择，`VolumeStepChanged` 在游戏线程触发。档位按 UserId
镜像到 `LabAPI/configs/<port>/ServerKeybinds/plugin_music_volume.txt`（`userId<TAB>档位`，档位 0 不保存）。
仍使用单个扬声器的使用者忽略档位、按全音量播放；目前只有 Global Music Player 采用多扬声器。
界面语言遵循 `KeybindRegistry.Language`（空/cn 为中文，en 为英文）。

保留原 GMP 默认双按钮类型及 ID **24001**；旧自定义 ID 不再使用。按 UserId 原子保存至
`LabAPI/configs/<port>/ServerKeybinds/plugin_music_muted.txt`，在客户端设置到达前恢复，音频路径不读文件。
客户端也会保存原生设置。GMP 的 `gmpmute` / `gmpunmute` 命令操作同一偏好，但原生客户端开关无法由服务器
命令移动，因此菜单可能仍显示旧选项。重复回传不会撤销命令；更改菜单选项或重连后的客户端保存值优先。
长期偏好请在菜单中设置，用 `gmpmute status` 查询实际状态。

### 分类（API 3）

API 3 之前，菜单顺序取决于 `Dictionary<int, KeybindBlock>` 的枚举顺序（即插件加载顺序），因此设置列表杂乱无序。现在区块按 `(分类, 基址)` 排序，注册表为每个分类只生成**一个** `SSGroupHeader`，各插件自己的标题则作为缩小间距的子标题显示在其下。

| 顺序 | `SettingsCategory` | 用途 |
| --- | --- | --- |
| 0 | `Gameplay` | 能力按键，以及一切改变玩家可执行动作的设置 |
| 10 | `Display` | HUD、名牌等展示类开关 |
| 20 | `Announcements` | 提示、音乐、引导类的开关 |
| 30 | `Tools` | 管理/观察/创作工具，通常配合 `VisibleTo` 使用 |
| 100 | `Other` | 未调用 `InCategory` 的区块的默认归属 |

同一分类内，区块先按 `Order(int)`（越小越靠前，默认 0）、再按基址排序。使用负值可将最常用的设置**固定在最顶部**——reinforcements-system 将其能力按键固定为 `-1000`，因为在这两个键被采纳之前该插件的任何功能都无法使用。

**注册表的设置会排在外部设置之前。** `DefinedSettings` 与自行合并的插件（HUD 开关、音乐静音等）共用；以前采用追加方式，会把注册表的所有设置挤到它们之后——无论分类如何排序，能力按键都会落在菜单最底部。外部条目保持其原有相对顺序，不做其他改动。

`InCategory` 与 `Order` 仅影响展示，**不会改变任何设置 ID**，因此重新分类不会丢失玩家已保存的值。若某分类下的区块对某玩家全部不可见，则不会向他发送该分类标题。

`AddTextArea` 可添加只读说明文本，它渲染在区块标题之下、按键与设置之上（区块的发出顺序为：标题 → 说明文本 → 按键 → 取值类设置）。`SSTextArea` 的 `UserResponseMode` 为 `None`，因此不产生客户端响应，也无法被伪造回传。

**按键默认值只是“建议”。** 原生参数名为 `SuggestedKey`，且 `SerializeEntry` 只会发送它：`AssignedKeyCode` 归客户端所有，服务器永远无法设置。客户端会显示一个需要玩家点击的“采纳建议”控件，所以建议按键是“一键默认”而非已绑定。请用 `AddTextArea` 在按键旁边说明这一点，不要假设玩家会自行发现。

分类标题由注册表在 `SssIdBlocks.RegistryHeaders + (int)category` 处生成，以便在分配方案内拥有稳定 ID（而不是无 ID 的 `SSGroupHeader` 所用的标签哈希）。其语言由 `KeybindRegistry.Language` 决定（留空或 `cn` 为中文，`en` 为英文，默认回退中文）。菜单中其余文本均由使用插件自行本地化。

### `AddTwoButtons` 与 PlayerPrefs 注意事项

`AddTwoButtons` 对应原生 `SSTwoButtonsSetting`，是开/关类开关的正确控件；两选项下拉菜单虽然可用，但玩家需要展开才能选择。玩家选中选项 **B** 时回调传入 `true`。

客户端将每个设置值保存在 PlayerPrefs 的 `SrvSp_<服务器>_<类型码>_<设置ID>` 下（见 `ServerSpecificSettingBase.GeneratePrefsKey`），**类型码是键的一部分**。因此将现有下拉菜单改为双按钮开关，会静默地把所有已选过值的玩家重置为默认值。请有意识地进行这类转换，不要顺手改。

### 可靠发送与诊断（API 4）

原生设置包是“整体替换”而不是追加操作。API 4 使用 wire version 4 发送个性化设置包；加入就绪与确认均最多重试三次，并在某玩家连续 30 秒没有注册表发送后进行节流修复发送。设置页打开或存在按键锁存时会延后修复；锁存最多再延后一个 30 秒修复周期，之后会先作为陈旧状态释放再修复，避免丢失的松键事件永久关闭对账。version 4 状态报告，或任意注册表自有设置响应，都会确认待处理发送。原生状态版本表示玩家已在菜单中接受的版本，并非网络传输回执；从未打开设置页的玩家保持 version 0 属于正常情况。

- 双按钮已发送默认值、发送审计、确认状态与按键锁存均按 UserId 保存，避免复用会话 PlayerId 时继承其他玩家的状态。
- 可见性会在按键进入锁存前检查；角色变化、注册表重建、回合重启和断线都会释放或清理对应状态。

- `KeybindRegistry.Debug`（静态布尔值，与 `Language` 一样由使用插件设置，后写者生效）控制发送、就绪重试与确认重试日志。发送原因包括 `join`、`join-ready-retry`、`join-ack-retry`、`refresh`、`rebuild` 与 `reconcile`。
- `KeybindRegistry.PressTrace` 控制按键路由每个决策点的跟踪日志：被丢弃的设置值响应、未知设置 ID、按下/释放锁存结果，以及按键被路由到哪个区块与绑定。
- RA 命令 **`keybinds`**（别名 `skb`，需要 `ServerConsoleCommands` 权限），由注册表自行注册（LabAPI 不会扫描依赖库中的命令）：
  - `keybinds status <ID|名称>` —— 该玩家此刻应收到的条目数量、最近一次记录的发送（UTC 时间 + 数量 + 原因）、待确认尝试次数、按下锁存列表，以及客户端已接受的设置版本。
  - `keybinds resend <ID|名称>` —— 向该玩家重新推送个性化设置集合。
  - `keybinds trace on|off` —— 运行时切换 `PressTrace`。
- 状态清理：按下锁存会在角色变更、注册表重建与回合重启时清除；玩家离开时会清理其全部按玩家状态。

`CustomItems.dll` 硬依赖 ServerKeybinds。任何需要设置或按键绑定的自定义物品插件都必须使用该注册表。

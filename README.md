# ServerKeybinds

## English

`ServerKeybinds.dll` is the process-wide owner of SCP:SL Server-Specific Settings used by metarepo plugins. It is a shared dependency library installed under LabAPI's `dependencies/global` directory, not a standalone gameplay plugin.

API 3 supports group headers, keybinds, dropdowns, sliders, native two-button toggles, and per-player block visibility through one claimed 1000-ID block per consumer, and it owns the **order** of the menu. The registry owns the single additive merge into `ServerSpecificSettingsSync.DefinedSettings`, personalized joining-player sends, setting-response visibility gates, rising/falling key edges, and collision detection. Consumer plugins must not create a parallel global-settings merge path.

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

`CustomItems.dll` hard-depends on ServerKeybinds. Any custom-item plugin requiring settings or keybinds must use this registry.

## 中文

`ServerKeybinds.dll` 是元仓库插件使用的 SCP:SL“服务器专属设置”进程级唯一管理器。它是安装在 LabAPI `dependencies/global` 目录中的共享依赖库，不是独立游戏插件。

API 3 支持分组标题、按键绑定、下拉菜单、滑条、原生双按钮开关及按玩家控制区块可见性；每个使用者占用一个 1000 ID 的固定区块，并且由注册表统一决定菜单的**显示顺序**。注册表统一负责对 `ServerSpecificSettingsSync.DefinedSettings` 的加法合并、个性化加入发送、设置响应权限过滤、按键按下/释放边沿以及冲突检测。使用插件不得再创建并行的全局设置合并路径。

插件停用时必须调用 `Disable()`。新增使用者前，应先在 `SssIdBlocks` 中分配固定基址。

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

`CustomItems.dll` 硬依赖 ServerKeybinds。任何需要设置或按键绑定的自定义物品插件都必须使用该注册表。

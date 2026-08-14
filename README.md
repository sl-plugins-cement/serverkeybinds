# ServerKeybinds

## English

`ServerKeybinds.dll` is the process-wide owner of SCP:SL Server-Specific Settings used by metarepo plugins. It is a shared dependency library installed under LabAPI's `dependencies/global` directory, not a standalone gameplay plugin.

API 2 supports group headers, keybinds, dropdowns, sliders, and per-player block visibility through one claimed 1000-ID block per consumer. The registry owns the single additive merge into `ServerSpecificSettingsSync.DefinedSettings`, personalized joining-player sends, setting-response visibility gates, rising/falling key edges, and collision detection. Consumer plugins must not create a parallel global-settings merge path.

Typical usage:

```csharp
KeybindBlock block = KeybindRegistry.ClaimBlock(SssIdBlocks.MyPlugin, "My Plugin")
    .Header("My Plugin")
    .VisibleTo(HasAccess)
    .Add(1, "Toggle", KeyCode.V, "Toggle the feature.", OnToggle)
    .AddDropdown(2, "Mode", new[] { "A", "B" }, 0, "Select a mode.", OnMode)
    .AddSlider(3, "Speed", 0f, 100f, 50f, true, "0", "{0}", "Set speed.", OnSpeed);
block.Enable();
```

Call `Disable()` during plugin shutdown. Add a fixed base to `SssIdBlocks` before introducing a new consumer.

`CustomItems.dll` hard-depends on ServerKeybinds. Any custom-item plugin requiring settings or keybinds must use this registry.

## 中文

`ServerKeybinds.dll` 是元仓库插件使用的 SCP:SL“服务器专属设置”进程级唯一管理器。它是安装在 LabAPI `dependencies/global` 目录中的共享依赖库，不是独立游戏插件。

API 2 支持分组标题、按键绑定、下拉菜单、滑条及按玩家控制区块可见性；每个使用者占用一个 1000 ID 的固定区块。注册表统一负责对 `ServerSpecificSettingsSync.DefinedSettings` 的加法合并、个性化加入发送、设置响应权限过滤、按键按下/释放边沿以及冲突检测。使用插件不得再创建并行的全局设置合并路径。

插件停用时必须调用 `Disable()`。新增使用者前，应先在 `SssIdBlocks` 中分配固定基址。

`CustomItems.dll` 硬依赖 ServerKeybinds。任何需要设置或按键绑定的自定义物品插件都必须使用该注册表。

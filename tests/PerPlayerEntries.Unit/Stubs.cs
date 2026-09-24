// Only the native entry types are stubbed, with the presentation properties the fingerprint reads.
// The coordinator, interest index, ledger and fingerprint under test are production code.
namespace UnityEngine
{
    public enum KeyCode { None = 0, X = 120, Z = 122 }
}

namespace TMPro
{
    public enum TextAlignmentOptions { TopLeft = 257, Center = 514 }
    public class TMP_InputField { public enum ContentType { Standard, IntegerNumber } }
}

namespace UserSettings.ServerSpecific
{
    public abstract class ServerSpecificSettingBase
    {
        protected ServerSpecificSettingBase(int id, string label, string? hint)
        {
            SettingId = id;
            Label = label;
            HintDescription = hint ?? string.Empty;
        }

        public int SettingId { get; }
        public string Label { get; }
        public string HintDescription { get; }
        public byte CollectionId { get; protected set; } = byte.MaxValue;
        public bool IsServerOnly { get; protected set; }
    }

    public sealed class SSGroupHeader(int id, string label, bool reducedPadding = false)
        : ServerSpecificSettingBase(id, label, null)
    { public bool ReducedPadding { get; } = reducedPadding; }

    public sealed class SSKeybindSetting(int id, string label, UnityEngine.KeyCode key, bool preventInteractionOnGui = true, bool allowSpectatorTrigger = false, string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public UnityEngine.KeyCode SuggestedKey { get; } = key;
        public bool PreventInteractionOnGUI { get; } = preventInteractionOnGui;
        public bool AllowSpectatorTrigger { get; } = allowSpectatorTrigger;
    }

    public sealed class SSDropdownSetting(int id, string label, string[] options, int defaultIndex = 0, SSDropdownSetting.DropdownEntryType entryType = SSDropdownSetting.DropdownEntryType.Regular, string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public enum DropdownEntryType { Regular, Scrollable, ScrollableLoop, Hybrid, HybridLoop }
        public string[] Options { get; } = options;
        public int DefaultOptionIndex { get; } = defaultIndex;
        public DropdownEntryType EntryType { get; } = entryType;
    }

    public sealed class SSTwoButtonsSetting(int id, string label, string optionA, string optionB, bool defaultIsB = false, string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public string OptionA { get; } = optionA;
        public string OptionB { get; } = optionB;
        public bool DefaultIsB { get; } = defaultIsB;
    }

    public sealed class SSSliderSetting(int id, string label, float min, float max, float defaultValue, bool integer = false, string valueFormat = "0.##", string displayFormat = "{0}", string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public float MinValue { get; } = min;
        public float MaxValue { get; } = max;
        public float DefaultValue { get; } = defaultValue;
        public bool Integer { get; } = integer;
        public string ValueToStringFormat { get; } = valueFormat;
        public string FinalDisplayFormat { get; } = displayFormat;
    }

    public sealed class SSTextArea(int id, string content, SSTextArea.FoldoutMode foldout = SSTextArea.FoldoutMode.NotCollapsable, TMPro.TextAlignmentOptions alignment = TMPro.TextAlignmentOptions.TopLeft)
        : ServerSpecificSettingBase(id, content, null)
    {
        public enum FoldoutMode { NotCollapsable, CollapseOnEntry, CollapsedByDefault, ExtendedByDefault }
        public FoldoutMode Foldout { get; } = foldout;
        public TMPro.TextAlignmentOptions AlignmentOptions { get; } = alignment;
    }

    public sealed class SSPlaintextSetting(int id, string label, string placeholder = "...", int characterLimit = 64, TMPro.TMP_InputField.ContentType contentType = TMPro.TMP_InputField.ContentType.Standard, string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public string Placeholder { get; } = placeholder;
        public string DefaultText { get; } = string.Empty;
        public TMPro.TMP_InputField.ContentType ContentType { get; } = contentType;
        public int CharacterLimit { get; } = characterLimit;
    }

    public sealed class SSButton(int id, string label, string buttonText, float? holdTime = null, string? hint = null)
        : ServerSpecificSettingBase(id, label, hint)
    {
        public string ButtonText { get; } = buttonText;
        public float HoldTimeSeconds { get; } = holdTime ?? 0f;
    }
}

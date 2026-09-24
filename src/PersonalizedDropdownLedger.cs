using System.Collections.Generic;
using System.Linq;

namespace ServerKeybinds;

internal enum PersonalizedDropdownResponseKind
{
    /// <summary>The client's first value after a send: its stored selection, not a choice made now.</summary>
    Acquisition,

    /// <summary>A later value that differs from the previous one in the same send generation.</summary>
    Change,

    /// <summary>The same value again; not a new action.</summary>
    Duplicate,
}

internal enum PersonalizedDropdownResponseOutcome
{
    /// <summary>The index addresses the option list this player was sent; see the kind for what it means.</summary>
    Accepted,

    /// <summary>Repeated value; swallow it.</summary>
    Duplicate,

    /// <summary>
    /// Nothing was sent for this id, or the index is outside the list that was sent. The client's view
    /// does not match the server's, so the caller should refresh it.
    /// </summary>
    Stale,
}

/// <summary>
/// Classifies the client's first value for a sent dropdown separately from later deliberate changes.
/// Consumers may opt into acquisition for staging-only workflows without executing an action.
/// </summary>
internal sealed class PersonalizedDropdownResponseLatch
{
    private bool _hasValue;
    private int _currentIndex;

    public PersonalizedDropdownResponseKind Observe(int index)
    {
        if (!_hasValue)
        {
            _hasValue = true;
            _currentIndex = index;
            return PersonalizedDropdownResponseKind.Acquisition;
        }

        if (_currentIndex == index)
        {
            return PersonalizedDropdownResponseKind.Duplicate;
        }

        _currentIndex = index;
        return PersonalizedDropdownResponseKind.Change;
    }
}

/// <summary>
/// One player's record of the personalized dropdowns they were ACTUALLY sent: per send generation, the
/// exact option list of each entry plus a response latch. A response is only ever interpreted against
/// this record, never against the model a resolver would return now, because live state may have moved
/// on since the list reached the player's screen.
/// </summary>
internal sealed class PersonalizedDropdownLedger
{
    private readonly Dictionary<int, SentDropdown> _sent = new();

    public long Generation { get; private set; }

    /// <summary>Starts a new send generation; every entry recorded before it is forgotten.</summary>
    public void StartGeneration()
    {
        Generation++;
        _sent.Clear();
    }

    /// <summary>Records the option list sent for <paramref name="settingId"/> in the current generation.</summary>
    public void Record(int settingId, IEnumerable<string> options)
    {
        _sent[settingId] = new SentDropdown(Generation, options.ToArray());
    }

    public PersonalizedDropdownResponseOutcome Observe(
        int settingId,
        int rawIndex,
        out DropdownSelection selection,
        out PersonalizedDropdownResponseKind kind)
    {
        selection = default;
        kind = PersonalizedDropdownResponseKind.Duplicate;
        if (!_sent.TryGetValue(settingId, out SentDropdown? sent) || rawIndex < 0 || rawIndex >= sent.Options.Length)
        {
            return PersonalizedDropdownResponseOutcome.Stale;
        }

        kind = sent.Latch.Observe(rawIndex);
        if (kind == PersonalizedDropdownResponseKind.Duplicate)
        {
            return PersonalizedDropdownResponseOutcome.Duplicate;
        }

        selection = new DropdownSelection(rawIndex, sent.Options[rawIndex], sent.Generation);
        return PersonalizedDropdownResponseOutcome.Accepted;
    }

    private sealed class SentDropdown
    {
        public SentDropdown(long generation, string[] options)
        {
            Generation = generation;
            Options = options;
        }

        public long Generation { get; }

        public string[] Options { get; }

        public PersonalizedDropdownResponseLatch Latch { get; } = new();
    }
}

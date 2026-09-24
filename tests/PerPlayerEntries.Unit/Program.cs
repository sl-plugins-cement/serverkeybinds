using ServerKeybinds;
using UserSettings.ServerSpecific;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Test)[] tests =
        {
            // Refresh coordinator: debounce, coalesce, identical skip, rate limits, single recording point.
            ("debounce_coalesces_and_keeps_latest_snapshot", DebounceCoalescesAndKeepsLatestSnapshot),
            ("identical_fingerprint_is_suppressed", IdenticalFingerprintIsSuppressed),
            ("fingerprint_is_taken_when_processed_not_when_requested", FingerprintIsTakenWhenProcessed),
            ("minimum_interval_keeps_latest_replacement", MinimumIntervalKeepsLatestReplacement),
            ("rolling_minute_caps_at_six", RollingMinuteCapsAtSix),
            ("idle_time_does_not_create_work", IdleTimeDoesNotCreateWork),
            ("send_path_that_records_itself_is_not_double_counted", SendPathThatRecordsItselfIsNotDoubleCounted),
            ("out_of_band_send_drops_pending_and_counts_against_budget", OutOfBandSendDropsPendingAndCountsAgainstBudget),
            // Interest index.
            ("personal_interest_never_fans_out", PersonalInterestNeverFansOut),
            ("population_boundaries_route_only_intersection", PopulationBoundariesRouteOnlyIntersection),
            // View fingerprint.
            ("fingerprint_is_stable_for_equal_views", FingerprintIsStableForEqualViews),
            ("fingerprint_changes_with_presentation_and_order", FingerprintChangesWithPresentationAndOrder),
            // Dropdown response ledger.
            ("dropdown_first_value_is_acquisition_then_change_then_duplicate", DropdownAcquisitionThenChangeThenDuplicate),
            ("dropdown_new_generation_resets_latch_and_carries_generation", DropdownNewGenerationResetsLatch),
            ("dropdown_response_outside_sent_list_is_stale", DropdownResponseOutsideSentListIsStale),
        };

        try
        {
            foreach ((string name, Action test) in tests)
            {
                test();
                Console.WriteLine("PASS " + name);
            }

            Console.WriteLine($"PASS all {tests.Length} scenarios");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
    }

    private static void DebounceCoalescesAndKeepsLatestSnapshot()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        for (int i = 0; i < 30; i++)
        {
            sut.Request(7, "view-" + i, "reason-" + i);
            clock.Advance(0.01);
        }

        Equal(0, sent.Count, "requests must not send synchronously");
        Equal(1, sut.PendingCount, "one player has one candidate");
        Equal(30L, sut.Counters.Requested, "requested counter");
        Equal(29L, sut.Counters.Coalesced, "coalesced counter");

        clock.Advance(0.48);
        sut.ProcessDue();
        Equal(0, sent.Count, "trailing debounce has not elapsed");
        clock.Advance(0.02);
        sut.ProcessDue();
        Equal(1, sent.Count, "one send after debounce");
        Equal("view-29", sent[0].Snapshot, "latest snapshot wins");
        Equal(30, sent[0].Reasons.Count, "all distinct reasons coalesce");
    }

    private static void IdenticalFingerprintIsSuppressed()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);
        sut.RecordSent(1, "same");

        clock.Advance(10);
        sut.Request(1, "same", "role");
        clock.Advance(0.5);
        sut.ProcessDue();

        Equal(0, sent.Count, "identical view not sent");
        Equal(1L, sut.Counters.IdenticalSnapshots, "identical counter");
        Equal(0, sut.PendingCount, "identical candidate removed");
    }

    private static void FingerprintIsTakenWhenProcessed()
    {
        // The registry hands the coordinator a lazy snapshot (the player); the view it hashes must be the
        // one as of processing, or a request queued before a state change would be skipped as identical.
        FakeClock clock = new();
        List<Sent> sent = new();
        string liveView = "before";
        SssRefreshCoordinator<int, Func<string>> sut = new(
            () => clock.Now,
            snapshot => snapshot(),
            (_, snapshot, reasons) => { sent.Add(new Sent(snapshot(), reasons.ToArray())); return true; });
        sut.RecordSent(1, () => "before");

        clock.Advance(10);
        sut.Request(1, () => liveView, "role");
        liveView = "after";
        clock.Advance(0.5);
        sut.ProcessDue();

        Equal(1, sent.Count, "changed view is sent");
        Equal("after", sent[0].Snapshot, "view resolved at processing time");
        Equal(0L, sut.Counters.IdenticalSnapshots, "not mistaken for identical");
    }

    private static void MinimumIntervalKeepsLatestReplacement()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        sut.Request(1, "first", "join-followup");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(1, sent.Count, "first candidate sent");

        sut.Request(1, "second", "role");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(1, sent.Count, "minimum interval blocks second");
        Equal(1L, sut.Counters.RateLimited, "rate limit counted once");

        clock.Advance(0.2);
        sut.Request(1, "latest", "zone");
        clock.Advance(1.3);
        sut.ProcessDue();
        Equal(2, sent.Count, "send at two-second boundary");
        Equal("latest", sent[1].Snapshot, "blocked candidate was replaced");
        SequenceEqual(new[] { "role", "zone" }, sent[1].Reasons, "reasons survive replacement");
    }

    private static void RollingMinuteCapsAtSix()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        for (int i = 0; i < 6; i++)
        {
            sut.Request(4, "v" + i, "change");
            clock.Advance(0.5);
            sut.ProcessDue();
            if (i < 5)
            {
                clock.Advance(1.5);
            }
        }

        Equal(6, sent.Count, "first six sends fit budget");
        clock.Advance(1.5);
        sut.Request(4, "v6", "change");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(6, sent.Count, "seventh blocked inside rolling minute");

        clock.Set(60.49);
        sut.ProcessDue();
        Equal(6, sent.Count, "still inside window");
        clock.Set(60.5);
        sut.ProcessDue();
        Equal(7, sent.Count, "oldest send expires exactly at sixty seconds");
    }

    private static void IdleTimeDoesNotCreateWork()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);
        sut.RecordSent(1, "join");
        clock.Advance(600);
        Equal(null, sut.ProcessDue(), "ten idle minutes have no timer");
        Equal(0, sent.Count, "idle time sends nothing");
    }

    private static void SendPathThatRecordsItselfIsNotDoubleCounted()
    {
        // In the registry the coordinated send goes through the shared delivery path, which records every
        // send with the fingerprint of what actually went out. That record must win and count once.
        FakeClock clock = new();
        SssRefreshCoordinator<int, string>? sut = null;
        int sends = 0;
        sut = new SssRefreshCoordinator<int, string>(
            () => clock.Now,
            snapshot => snapshot,
            (player, _, _) => { sends++; sut!.RecordSent(player, "actual"); return true; });

        sut.Request(1, "requested", "role");
        clock.Advance(0.5);
        sut.ProcessDue();

        Equal(1, sends, "one wire send");
        Equal(1L, sut.Counters.Sent, "counted once");
        True(sut.TryGetDiagnostics(1, out SssRefreshPlayerDiagnostics diagnostics), "state exists");
        Equal("actual", diagnostics.Fingerprint, "recorded fingerprint is the one that went out");
        Equal(1, diagnostics.SendsInRollingMinute, "one send in the rolling window");

        sut.Request(1, "actual", "zone");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(1, sends, "view identical to the actual send is skipped");
        Equal(1L, sut.Counters.IdenticalSnapshots, "identical counter");
    }

    private static void OutOfBandSendDropsPendingAndCountsAgainstBudget()
    {
        FakeClock clock = new();
        List<Sent> sent = new();
        SssRefreshCoordinator<int, string> sut = Coordinator(clock, sent);

        sut.Request(1, "queued", "role");
        sut.RecordSent(1, "rebuild");
        Equal(0, sut.PendingCount, "an immediate send delivered the latest view; the candidate is dropped");

        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(0, sent.Count, "nothing left to send");

        sut.Request(1, "next", "zone");
        clock.Advance(0.5);
        sut.ProcessDue();
        Equal(0, sent.Count, "the immediate send started the two-second spacing");
        clock.Advance(1.5);
        sut.ProcessDue();
        Equal(1, sent.Count, "sent once the spacing elapsed");
    }

    private static void PersonalInterestNeverFansOut()
    {
        SssInterestIndex<string> sut = new(StringComparer.Ordinal);
        sut.Track("a@steam", SssInterest.All);
        sut.Track("b@steam", SssInterest.All);
        sut.Track("c@steam", SssInterest.Permission);

        SssInterest[] personal =
        {
            SssInterest.Role,
            SssInterest.Item,
            SssInterest.Cooldown,
            SssInterest.Title,
            SssInterest.Language,
            SssInterest.Zone,
            SssInterest.Permission,
            SssInterest.Display,
            SssInterest.WarmupMode,
        };

        foreach (SssInterest interest in personal)
        {
            SequenceEqual(new[] { "a@steam" }, sut.ResolvePersonal("a@steam", interest), interest + " routes only player a");
        }

        Equal(0, sut.ResolvePersonal("c@steam", SssInterest.Role).Count, "unsubscribed interest ignored");
        SequenceEqual(new[] { "c@steam" }, sut.ResolvePersonal("c@steam", SssInterest.Permission), "subscribed interest routed");
        Equal(0, sut.ResolvePersonal("nobody", SssInterest.All).Count, "untracked player ignored");
        True(sut.IsTracked("c@steam") && !sut.IsTracked("nobody"), "tracking is observable");
    }

    private static void PopulationBoundariesRouteOnlyIntersection()
    {
        SssInterestIndex<string> sut = new(StringComparer.Ordinal);
        foreach (string player in new[] { "1", "2", "3" })
        {
            sut.Track(player, SssInterest.All);
        }

        sut.Track("4", SssInterest.AllPersonal);

        SequenceEqual(new[] { "1" }, sut.ResolvePopulationBoundary(new[] { "1" }, new[] { "1", "2" }), "1 to 2 former sole");
        SequenceEqual(new[] { "1" }, sut.ResolvePopulationBoundary(new[] { "1", "2" }, new[] { "1" }), "2 to 1 remaining");
        Equal(0, sut.ResolvePopulationBoundary(new[] { "1", "2" }, new[] { "1", "2", "3" }).Count, "2 to 3 no fanout");
        Equal(0, sut.ResolvePopulationBoundary(new[] { "1", "2", "3" }, new[] { "1", "2" }).Count, "3 to 2 no fanout");
        Equal(0, sut.ResolvePopulationBoundary(Array.Empty<string>(), new[] { "1" }).Count, "join handled separately");
        Equal(0, sut.ResolvePopulationBoundary(new[] { "4" }, new[] { "4", "2" }).Count, "no boundary interest, no refresh");
    }

    private static void FingerprintIsStableForEqualViews()
    {
        string first = SssViewFingerprint.Compute(SampleView(optionB: "Beta", defaultIsB: false));
        string second = SssViewFingerprint.Compute(SampleView(optionB: "Beta", defaultIsB: false));
        Equal(first, second, "equal views hash identically across separately built instances");
        Equal(64, first.Length, "sha256 hex");
    }

    private static void FingerprintChangesWithPresentationAndOrder()
    {
        string baseline = SssViewFingerprint.Compute(SampleView(optionB: "Beta", defaultIsB: false));
        NotEqual(baseline, SssViewFingerprint.Compute(SampleView(optionB: "Gamma", defaultIsB: false)), "dropdown option text");
        NotEqual(baseline, SssViewFingerprint.Compute(SampleView(optionB: "Beta", defaultIsB: true)), "two-button default");

        List<ServerSpecificSettingBase> reordered = SampleView(optionB: "Beta", defaultIsB: false);
        (reordered[1], reordered[2]) = (reordered[2], reordered[1]);
        NotEqual(baseline, SssViewFingerprint.Compute(reordered), "entry order");

        List<ServerSpecificSettingBase> omitted = SampleView(optionB: "Beta", defaultIsB: false);
        omitted.RemoveAt(omitted.Count - 1);
        NotEqual(baseline, SssViewFingerprint.Compute(omitted), "hidden entry");
    }

    private static void DropdownAcquisitionThenChangeThenDuplicate()
    {
        PersonalizedDropdownLedger sut = new();
        sut.StartGeneration();
        sut.Record(1130001, new[] { "Select", "Scientist", "ClassD" });

        Equal(PersonalizedDropdownResponseOutcome.Accepted, sut.Observe(1130001, 1, out DropdownSelection first, out PersonalizedDropdownResponseKind kind), "first response accepted");
        Equal(PersonalizedDropdownResponseKind.Acquisition, kind, "first visible client value is an acquisition");
        Equal("Scientist", first.Value, "value resolved from the SENT list");
        Equal(1L, first.SendGeneration, "generation stamped");

        Equal(PersonalizedDropdownResponseOutcome.Duplicate, sut.Observe(1130001, 1, out _, out _), "repeat is swallowed");

        Equal(PersonalizedDropdownResponseOutcome.Accepted, sut.Observe(1130001, 2, out DropdownSelection change, out kind), "different value accepted");
        Equal(PersonalizedDropdownResponseKind.Change, kind, "later deliberate change is distinct from acquisition");
        Equal("ClassD", change.Value, "changed value");
    }

    private static void DropdownNewGenerationResetsLatch()
    {
        PersonalizedDropdownLedger sut = new();
        sut.StartGeneration();
        sut.Record(5, new[] { "a", "b" });
        sut.Observe(5, 0, out _, out _);
        sut.Observe(5, 1, out _, out _);

        sut.StartGeneration();
        sut.Record(5, new[] { "a", "b", "c" });
        Equal(PersonalizedDropdownResponseOutcome.Accepted, sut.Observe(5, 1, out DropdownSelection selection, out PersonalizedDropdownResponseKind kind), "accepted after re-send");
        Equal(PersonalizedDropdownResponseKind.Acquisition, kind, "the client's re-report after a new send is an acquisition, not a change");
        Equal(2L, selection.SendGeneration, "generation advanced");
    }

    private static void DropdownResponseOutsideSentListIsStale()
    {
        PersonalizedDropdownLedger sut = new();
        Equal(PersonalizedDropdownResponseOutcome.Stale, sut.Observe(9, 0, out _, out _), "nothing sent yet");

        sut.StartGeneration();
        sut.Record(9, new[] { "a", "b", "c" });
        sut.Observe(9, 2, out _, out _);

        // The player's list shrank on the next send; an index that was valid for the previous generation
        // no longer addresses anything they were sent.
        sut.StartGeneration();
        sut.Record(9, new[] { "a", "b" });
        Equal(PersonalizedDropdownResponseOutcome.Stale, sut.Observe(9, 2, out _, out _), "index from the previous generation is stale");
        Equal(PersonalizedDropdownResponseOutcome.Stale, sut.Observe(9, -1, out _, out _), "negative index is stale");
        Equal(PersonalizedDropdownResponseOutcome.Stale, sut.Observe(10, 0, out _, out _), "id never sent is stale");

        // A hidden entry is not recorded in its generation, so a response for it is stale too.
        sut.StartGeneration();
        Equal(PersonalizedDropdownResponseOutcome.Stale, sut.Observe(9, 0, out _, out _), "entry hidden this generation");
    }

    private static List<ServerSpecificSettingBase> SampleView(string optionB, bool defaultIsB) => new()
    {
        new SSGroupHeader(23000, "Gameplay"),
        new SSKeybindSetting(1090001, "Deploy", UnityEngine.KeyCode.X, hint: "Press to deploy."),
        new SSDropdownSetting(1130001, "Respawn as", new[] { "Select", "Alpha", optionB }, 0, SSDropdownSetting.DropdownEntryType.Regular, "Pick one."),
        new SSTwoButtonsSetting(24001, "Music", "On", "Off", defaultIsB, "Mute music."),
        new SSSliderSetting(24002, "Volume", 0, 100, 70, integer: true),
        new SSTextArea(1090002, "Explanation", SSTextArea.FoldoutMode.CollapsedByDefault),
        new SSPlaintextSetting(1090003, "Callsign"),
        new SSButton(1130002, "Role action", "Apply", 0.5f, "Applies the staged role."),
    };

    private static SssRefreshCoordinator<int, string> Coordinator(FakeClock clock, List<Sent> sent)
    {
        return new SssRefreshCoordinator<int, string>(
            () => clock.Now,
            snapshot => snapshot,
            (_, snapshot, reasons) =>
            {
                sent.Add(new Sent(snapshot, reasons.ToArray()));
                return true;
            });
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
        }
    }

    private static void NotEqual<T>(T unexpected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new InvalidOperationException($"{message}: expected a value other than '{unexpected}'");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }
    }

    private sealed class FakeClock
    {
        public double Now { get; private set; }
        public void Advance(double seconds) => Now += seconds;
        public void Set(double seconds) => Now = seconds;
    }

    private sealed record Sent(string Snapshot, IReadOnlyList<string> Reasons);
}

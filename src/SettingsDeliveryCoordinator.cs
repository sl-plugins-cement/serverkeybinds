using System;
using System.Collections.Generic;
using System.Linq;
using CentralAuth;
using LabApi.Features.Wrappers;
using MEC;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace ServerKeybinds;

/// <summary>
/// Owns reliable per-player settings delivery. The registry still owns collection construction; this
/// coordinator owns join readiness retries, acknowledgement retries, and a low-frequency repair send for
/// a ready player who has not received any registry send yet (every join attempt failed).
///
/// This product build does not re-send to players who already received a collection. Another plugin's
/// <c>SendToPlayer</c> replaces the client's whole collection, and on a shared server that per-player view
/// is that plugin's to manage; re-sending ours every interval would overwrite it.
/// </summary>
internal sealed class SettingsDeliveryCoordinator
{
    internal const int WireVersion = 4;

    private const int MaxJoinAttempts = 3;
    private const int MaxAckAttempts = 3;
    private const float JoinDelaySeconds = 0.75f;
    private const float RetryDelaySeconds = 1.5f;
    private const float ReconcileIntervalSeconds = 30f;
    private const float ReconcilePaceSeconds = 0.05f;
    private const double MaxLatchDeferralSeconds = 30d;

    private readonly Func<Player, string, int> _send;
    private readonly Func<Player, bool> _hasPressedLatch;
    private readonly Action<Player, string> _releasePressedLatches;
    private readonly Dictionary<string, PendingAcknowledgement> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SendAudit> _audit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _latchDeferredSince = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _scheduledJoins = new();
    private CoroutineHandle _reconcile;
    private int _nextToken;
    private int _generation;
    private bool _running;

    public SettingsDeliveryCoordinator(
        Func<Player, string, int> send,
        Func<Player, bool> hasPressedLatch,
        Action<Player, string> releasePressedLatches)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _hasPressedLatch = hasPressedLatch ?? throw new ArgumentNullException(nameof(hasPressedLatch));
        _releasePressedLatches = releasePressedLatches ?? throw new ArgumentNullException(nameof(releasePressedLatches));
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _generation++;
        PlayerAuthenticationManager.OnInstanceModeChanged += OnInstanceModeChanged;
        ServerSpecificSettingsSync.ServerOnStatusReceived += OnStatusReceived;
        ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;
        _reconcile = Timing.RunCoroutine(ReconcileLoop());
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _generation++;
        PlayerAuthenticationManager.OnInstanceModeChanged -= OnInstanceModeChanged;
        ServerSpecificSettingsSync.ServerOnStatusReceived -= OnStatusReceived;
        ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;
        if (_reconcile.IsRunning)
        {
            Timing.KillCoroutines(_reconcile);
        }

        _pending.Clear();
        _audit.Clear();
        _latchDeferredSince.Clear();
        _scheduledJoins.Clear();
    }

    public void ResetRound()
    {
        _generation++;
        _pending.Clear();
        _latchDeferredSince.Clear();
        _scheduledJoins.Clear();
    }

    public bool Send(Player player, string reason, bool requireAcknowledgement)
    {
        if (!CanSend(player))
        {
            return false;
        }

        string userId = player.UserId;
        if (!TrySend(player, reason, out int count))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            _audit[userId] = new SendAudit(DateTime.UtcNow, count, reason);
            if (requireAcknowledgement)
            {
                AwaitAcknowledgement(player.ReferenceHub, userId, attempt: 1);
            }
        }

        return true;
    }

    public bool TryGetAudit(Player player, out DateTime sentAtUtc, out int entryCount, out string reason)
    {
        sentAtUtc = default;
        entryCount = 0;
        reason = string.Empty;
        if (player == null || string.IsNullOrWhiteSpace(player.UserId) ||
            !_audit.TryGetValue(player.UserId, out SendAudit audit))
        {
            return false;
        }

        sentAtUtc = audit.SentAtUtc;
        entryCount = audit.EntryCount;
        reason = audit.Reason;
        return true;
    }

    public bool IsAwaitingAcknowledgement(Player player, out int attempt)
    {
        attempt = 0;
        if (player == null || string.IsNullOrWhiteSpace(player.UserId) ||
            !_pending.TryGetValue(player.UserId, out PendingAcknowledgement pending))
        {
            return false;
        }

        attempt = pending.Attempt;
        return true;
    }

    /// <summary>A registry-owned setting response proves the client processed a collection containing it.</summary>
    public void AcknowledgeInput(ReferenceHub hub)
    {
        string userId = UserIdOf(hub);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _pending.Remove(userId);
        }
    }

    private void OnInstanceModeChanged(ReferenceHub hub, ClientInstanceMode mode)
    {
        if (mode != ClientInstanceMode.ReadyClient || hub == null || !_scheduledJoins.Add(hub.netId))
        {
            return;
        }

        ScheduleJoinAttempt(hub, attempt: 1, JoinDelaySeconds);
    }

    private void ScheduleJoinAttempt(ReferenceHub? hub, int attempt, float delaySeconds)
    {
        int generation = _generation;
        Timing.CallDelayed(delaySeconds, () =>
        {
            if (!_running || generation != _generation)
            {
                return;
            }

            Player? player = hub == null ? null : Player.Get(hub);
            if (hub == null || hub.connectionToClient == null || !CanSend(player))
            {
                RetryJoinReadiness(hub, attempt, player == null ? "no ready Player wrapper" : "no client connection");
                return;
            }

            _scheduledJoins.Remove(hub.netId);
            Send(player!, attempt == 1 ? "join" : "join-ready-retry", requireAcknowledgement: true);
        });
    }

    private void RetryJoinReadiness(ReferenceHub? hub, int attempt, string why)
    {
        if (hub != null && attempt < MaxJoinAttempts)
        {
            Logger.Debug(
                $"[ServerKeybinds] Join delivery for netId {hub.netId} deferred ({why}); " +
                $"readiness attempt {attempt + 1}/{MaxJoinAttempts} in {RetryDelaySeconds:0.0}s.",
                KeybindRegistry.Debug);
            ScheduleJoinAttempt(hub, attempt + 1, RetryDelaySeconds);
            return;
        }

        if (hub != null)
        {
            _scheduledJoins.Remove(hub.netId);
        }

        Logger.Warn(
            $"[ServerKeybinds] Join delivery failed after {MaxJoinAttempts} readiness attempts ({why}); " +
            "the periodic reconcile will continue trying while the player remains connected.");
    }

    private void AwaitAcknowledgement(ReferenceHub? hub, string userId, int attempt)
    {
        int token = ++_nextToken;
        _pending[userId] = new PendingAcknowledgement(hub, attempt, token);
        int generation = _generation;
        Timing.CallDelayed(RetryDelaySeconds, () => CheckAcknowledgement(userId, token, generation));
    }

    private void CheckAcknowledgement(string userId, int token, int generation)
    {
        if (!_running || generation != _generation ||
            !_pending.TryGetValue(userId, out PendingAcknowledgement pending) || pending.Token != token)
        {
            return;
        }

        ReferenceHub? hub = pending.Hub;
        if (hub != null && ServerSpecificSettingsSync.GetUserVersion(hub) == WireVersion)
        {
            _pending.Remove(userId);
            return;
        }

        Player? player = hub == null ? null : Player.Get(hub);
        if (pending.Attempt >= MaxAckAttempts || !CanSend(player))
        {
            _pending.Remove(userId);
            Logger.Warn(
                $"[ServerKeybinds] No acknowledgement from '{userId}' after {pending.Attempt} settings sends; " +
                "the periodic reconcile remains active.");
            return;
        }

        int nextAttempt = pending.Attempt + 1;
        if (!TrySend(player!, "join-ack-retry", out int count))
        {
            _pending.Remove(userId);
            return;
        }

        _audit[userId] = new SendAudit(DateTime.UtcNow, count, "join-ack-retry");
        Logger.Debug(
            $"[ServerKeybinds] No acknowledgement from {player!.Nickname} ({player.PlayerId}); " +
            $"delivery attempt {nextAttempt}/{MaxAckAttempts} sent.",
            KeybindRegistry.Debug);
        AwaitAcknowledgement(hub, userId, nextAttempt);
    }

    private void OnStatusReceived(ReferenceHub hub, SSSUserStatusReport status)
    {
        if (status.Version != WireVersion)
        {
            return;
        }

        string userId = UserIdOf(hub);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _pending.Remove(userId);
        }
    }

    private void OnPlayerRemoved(ReferenceHub hub)
    {
        if (hub == null)
        {
            return;
        }

        string userId = UserIdOf(hub);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _pending.Remove(userId);
            _audit.Remove(userId);
            _latchDeferredSince.Remove(userId);
        }

        _scheduledJoins.Remove(hub.netId);
    }

    private IEnumerator<float> ReconcileLoop()
    {
        while (_running)
        {
            yield return Timing.WaitForSeconds(ReconcileIntervalSeconds);
            foreach (Player player in Player.ReadyList.ToArray())
            {
                if (!_running)
                {
                    yield break;
                }

                string userId = player?.UserId ?? string.Empty;
                bool delivered = !string.IsNullOrWhiteSpace(userId) && _audit.ContainsKey(userId);
                if (!CanSend(player) || delivered ||
                    (!string.IsNullOrWhiteSpace(userId) && _pending.ContainsKey(userId)) ||
                    ServerSpecificSettingsSync.IsTabOpenForUser(player!.ReferenceHub))
                {
                    continue;
                }

                if (_hasPressedLatch(player))
                {
                    DateTime now = DateTime.UtcNow;
                    if (!_latchDeferredSince.TryGetValue(userId, out DateTime deferredSince))
                    {
                        _latchDeferredSince[userId] = now;
                        continue;
                    }

                    if ((now - deferredSince).TotalSeconds < MaxLatchDeferralSeconds)
                    {
                        continue;
                    }

                    _releasePressedLatches(player, "stale reconcile latch");
                }

                _latchDeferredSince.Remove(userId);

                Send(player, "reconcile", requireAcknowledgement: false);
                yield return Timing.WaitForSeconds(ReconcilePaceSeconds);
            }
        }
    }

    private bool TrySend(Player player, string reason, out int count)
    {
        count = 0;
        try
        {
            count = _send(player, reason);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn(
                $"[ServerKeybinds] Settings send to {player.Nickname} ({player.PlayerId}) [{reason}] failed: " +
                exception.GetBaseException());
            return false;
        }
    }

    private static bool CanSend(Player? player) =>
        player != null && !player.IsDestroyed && player.IsPlayer && player.IsReady &&
        player.ReferenceHub != null && player.ReferenceHub.connectionToClient != null;

    private static string UserIdOf(ReferenceHub hub) => hub?.authManager?.UserId ?? string.Empty;

    private readonly struct PendingAcknowledgement
    {
        public PendingAcknowledgement(ReferenceHub? hub, int attempt, int token)
        {
            Hub = hub;
            Attempt = attempt;
            Token = token;
        }

        public ReferenceHub? Hub { get; }

        public int Attempt { get; }

        public int Token { get; }
    }

    private readonly struct SendAudit
    {
        public SendAudit(DateTime sentAtUtc, int entryCount, string reason)
        {
            SentAtUtc = sentAtUtc;
            EntryCount = entryCount;
            Reason = reason;
        }

        public DateTime SentAtUtc { get; }

        public int EntryCount { get; }

        public string Reason { get; }
    }
}

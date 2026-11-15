using System.Runtime.CompilerServices;
using LabApi.Features.Wrappers;
#if SKB_HSM
using HsmAdapter;
#endif

namespace ServerKeybinds;

/// <summary>
/// TEMPORARY (15.0 beta): the one-time "use .binds" notice, on HsmAdapter's shared bottom region.
///
/// ServerKeybinds is a library every consumer builds, and most never pass HsmAdapter, so the reference is
/// optional: the csproj defines <c>SKB_HSM</c> only when <c>HsmAdapterProject</c> resolves, and without it
/// the notice is simply absent (the console line still goes out). At runtime the adapter is a plugin that
/// may be missing; the scope is held as <see cref="object"/> and every adapter type stays inside
/// non-inlined methods, so a missing assembly fails only the call, which the caller catches.
/// </summary>
internal static class ConsoleBindHint
{
    private const string Key = "console-bind";
    private const float DurationSeconds = 15f;

    private static object? _scope;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TryShow(Player player)
    {
#if SKB_HSM
        var scope = (HintScope)(_scope ??= global::HsmAdapter.Hints.Acquire("ServerKeybinds"));
        // Onboarding: plain readouts that name the control and what to type.
        NoticeResult result = scope.ShowNotice(player, Key, NoticeRegion.Bottom, new[]
        {
            new TextRow(24, new TextSpan("技能按键需要在控制台绑定", "#FFCB6B", bold: true)),
            new TextRow(22, new TextSpan("按 ~ 打开控制台，输入 ", "#EAF6FF"), new TextSpan(".binds", "#FFCB6B", bold: true)),
            new TextRow(22, new TextSpan("照着列出的 cmdbind 命令逐行输入即可", "#EAF6FF")),
        }, height: 100, priority: 50, duration: DurationSeconds);
        return result is NoticeResult.Visible or NoticeResult.Queued;
#else
        return false;
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Dispose()
    {
        if (_scope == null)
        {
            return;
        }

        try
        {
            DisposeScope();
        }
        catch
        {
            // The adapter went away first; its scope went with it.
        }

        _scope = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeScope() => (_scope as System.IDisposable)?.Dispose();
}

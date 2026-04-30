using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

// VSTHRD001 / VSTHRD110: this file deliberately bypasses
// JoinableTaskFactory.SwitchToMainThreadAsync — see the class doc-comment
// for the reasoning. Suppressing the analyzer locally is the right move;
// don't promote these to project-level NoWarn (other files genuinely benefit
// from the JTF guidance).
#pragma warning disable VSTHRD001
#pragma warning disable VSTHRD110

namespace ChatRelay;

/// <summary>
/// Single source of truth for UI-thread marshalling inside the VSIX.
/// <para>
/// We use the WPF <see cref="Dispatcher"/> directly rather than
/// <c>JoinableTaskFactory.SwitchToMainThreadAsync</c>. JTF's "main
/// thread" model does not always align with the WPF dispatcher in
/// tool-window code paths — in practice, JsonRpc continuations resumed
/// on the threadpool can stay there even after a JTF switch, which
/// silently drops <c>ObservableCollection</c> binding updates and makes
/// any direct <c>Panel.Children</c> access throw "different thread owns
/// it." The WPF dispatcher's <c>CheckAccess</c> / <c>InvokeAsync</c>
/// is rock-solid because every WPF element's owning thread is exactly
/// its dispatcher's thread.
/// </para>
/// <para>
/// The dispatcher reference is captured lazily from
/// <c>Application.Current</c> on first access. ChatControl's ctor is
/// the natural priming site since it runs on the UI thread during
/// package activation.
/// </para>
/// </summary>
public static class UiThread
{
    static Dispatcher? _dispatcher;

    static Dispatcher? GetDispatcher()
    {
        if (_dispatcher != null) return _dispatcher;
        _dispatcher = Application.Current?.Dispatcher;
        return _dispatcher;
    }

    /// <summary>Await this to make sure the next line runs on the WPF UI thread.</summary>
    public static async Task SwitchToUi()
    {
        var disp = GetDispatcher();
        if (disp == null || disp.CheckAccess()) return;
        await disp.InvokeAsync(() => { }, DispatcherPriority.Send).Task;
    }

    /// <summary>Fire-and-forget UI-thread invocation. Inline if already on UI; otherwise queued via the dispatcher.</summary>
    public static void OnUi(Action action)
    {
        var disp = GetDispatcher();
        if (disp == null || disp.CheckAccess()) { action(); return; }
        disp.BeginInvoke(action, DispatcherPriority.Send);
    }
}

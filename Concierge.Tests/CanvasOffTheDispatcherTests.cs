using Microsoft.AspNetCore.Components;
using Bunit;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Recording a change from a thread that is not the renderer's.
///
/// **This killed the whole application, twice, before the trace was caught.**
///
/// Every design tool that does real asynchronous work before recording — reading a folder off
/// the disk, fetching a sound, generating a picture, speaking words — resumes on a thread-pool
/// thread. `DesignSession.Record` then raises `Changed` from there, the canvas handled it with
/// `async void`, and the parent's callback reached `StateHasChanged` off the dispatcher:
///
///     System.InvalidOperationException: The current thread is not associated with the
///     Dispatcher. Use InvokeAsync() to switch execution to the Dispatcher.
///
/// In an `async void` that is an unhandled exception on a pool thread, which does not fail the
/// edit — it takes the process down. Concierge simply vanished off the screen.
///
/// It had never shown up because every tool that existed recorded synchronously and stayed on
/// the dispatcher by luck. `design_bring_in` reads files, so it was the first to go async
/// before recording, and it found a fault that was waiting for all of them.
///
/// **The three behavioural tests below do not reproduce it, and that was measured rather than
/// hoped.** The old `async void` was put back and all three still passed: bUnit's renderer
/// does not enforce dispatcher affinity the way the MAUI host does, so the throw never
/// happens here. They are worth keeping — they pin that a change from another thread reaches
/// the screen and the host — but they are not the proof.
///
/// The proof is the running app, where the crash was reproduced twice and the fix watched to
/// hold. What stops it coming back is the last test in this file, which reads the source and
/// goes red if either half of the mistake returns. Crude, and it is what is available.
/// </summary>
public sealed class CanvasOffTheDispatcherTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Workspace.Design> Canvas(DesignSession session)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Workspace.Design>(parameters => parameters
            .Add(design => design.Session, session)
            .Add(design => design.Changed, EventCallback.Factory.Create(this, () => { })));
    }

    /// <summary>
    /// The canvas survives a change recorded from somewhere else and shows it.
    /// </summary>
    [Fact]
    public async Task A_change_recorded_off_the_renderer_thread_does_not_take_the_app_down()
    {
        var session = new DesignSession();
        var canvas = Canvas(session);

        // Task.Run so the record genuinely happens on a pool thread, which is what an awaited
        // file read leaves you on. Awaited here so a throw inside it fails this test rather
        // than escaping to kill the run.
        await Task.Run(() => session.Record(
            session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Sports Day"))),
            "Brought in 1"));

        canvas.WaitForAssertion(
            () => Assert.Contains("Sports Day", canvas.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// And the parent is told, which is the half that actually threw — the exception came out
    /// of the callback to the workspace, not out of the canvas's own render.
    /// </summary>
    [Fact]
    public async Task And_whatever_is_hosting_the_canvas_is_still_told()
    {
        var session = new DesignSession();
        var told = 0;

        JSInterop.Mode = JSRuntimeMode.Loose;

        var canvas = Render<Concierge.Shared.Components.Workspace.Design>(parameters => parameters
            .Add(design => design.Session, session)
            .Add(design => design.Changed, EventCallback.Factory.Create(this, () => told++)));

        await Task.Run(() => session.Record(
            session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Hello"))),
            "Brought in 1"));

        canvas.WaitForAssertion(() => Assert.True(told > 0), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Several in a row, because an import records once for a folder and a routine records
    /// many times — and the crash needed a second one before it showed.
    /// </summary>
    [Fact]
    public async Task And_several_changes_in_a_row_from_off_the_thread()
    {
        var session = new DesignSession();
        var canvas = Canvas(session);

        for (var i = 0; i < 5; i++)
        {
            var n = i;

            await Task.Run(() => session.Record(
                session.Current.Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", $"Number {n}"))),
                $"Brought in {n}"));
        }

        canvas.WaitForAssertion(
            () => Assert.Contains("Number 4", canvas.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The tripwire. Two things have to stay true, and the second is the one that bit.
    ///
    /// `async void` on an event handler means an exception has nowhere to go but the process.
    /// And marshalling only the render is not enough — the callback to whatever hosts the
    /// canvas has to be inside the same `InvokeAsync`, because that is what actually threw.
    /// </summary>
    [Fact]
    public void The_session_handler_never_goes_back_to_being_fire_and_forget()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Concierge.Shared.Components", "Workspace", "Design.razor")));

        var handler = source[source.IndexOf("void OnSessionChanged", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("public void Dispose", StringComparison.Ordinal)];

        Assert.DoesNotContain("async void", handler, StringComparison.Ordinal);
        Assert.Contains("InvokeAsync(async", handler, StringComparison.Ordinal);
        Assert.Contains("await Changed.InvokeAsync()", handler, StringComparison.Ordinal);
    }
}

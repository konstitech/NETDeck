using System.Diagnostics;
using System.Text;
using konstitech.DotNetDeck.Tui;
using NSubstitute;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

[assembly: TUnit.Core.NotInParallel]

namespace konstitech.DotNetDeck.Foundation.Tests;

public class TuiApplicationTests
{
    private const string Arguments = "NDK001: This initialization build does not accept arguments.";
    private const string Redirected = "NDK002: An interactive input and output terminal is required.";
    private const string Failed = "NDK003: The terminal session failed; terminal cleanup was attempted.";

    [Test]
    public async Task InteractiveSessionRunsAndDisposes()
    {
        var app = CreateApp();
        var calls = new List<string>();
        bool windowDisposing = false;
        string? title = null;
        string[]? texts = null;
        Type[]? types = null;
        app.Init(null).Returns(_ => { calls.Add("init"); return app; });
        app.Run(Arg.Any<IRunnable>(), null).Returns(info =>
        {
            calls.Add("run");
            var window = (Window)info[0];
            window.Disposing += (_, _) => windowDisposing = true;
            title = window.Title.ToString();
            texts = window.SubViews.Select(v => v.Text.ToString()).ToArray();
            types = window.SubViews.Select(v => v.GetType()).ToArray();
            return null;
        });
        app.When(x => x.Dispose()).Do(_ => calls.Add(windowDisposing ? "dispose-after-window" : "dispose-before-window"));
        int factories = 0;
        using var error = new StringWriter();
        var result = TuiApplication.Run([], false, false, () => { factories++; return app; }, error);
        await AssertOutcomeAsync(result, TerminalSessionStatus.Completed, true, 0, null);
        await Assert.That(factories).IsEqualTo(1);
        await Assert.That(calls.SequenceEqual(new[] { "init", "run", "dispose-after-window" })).IsTrue();
        await Assert.That(title).IsEqualTo(".NETDeck");
        await Assert.That(types).IsNotNull();
        await Assert.That(types!.SequenceEqual(new[] { typeof(Label), typeof(Button) })).IsTrue();
        await Assert.That(texts).IsNotNull();
        await Assert.That(texts!.SequenceEqual(new[] { "Project tools are not implemented yet. Press Esc or Quit to exit.", "_Quit" })).IsTrue();
        await Assert.That(windowDisposing).IsTrue();
        await Assert.That(error.ToString()).IsEmpty();
        app.Received(1).Init(null);
        app.Received(1).Run(Arg.Any<IRunnable>(), null);
        app.Received(1).Dispose();
    }

    [Test]
    public async Task QuitButtonAndEscapeBindingTargetApplication()
    {
        var app = CreateApp();
        app.Run(Arg.Any<IRunnable>(), null).Returns(info =>
        {
            var window = (Window)info[0];
            var button = window.SubViews.OfType<Button>().Single();
            button.InvokeCommand(Command.Accept);
            return null;
        });
        using var error = new StringWriter();
        var result = TuiApplication.Run([], false, false, () => app, error);
        await Assert.That(result.IsSuccess).IsTrue();
        app.Received(1).RequestStop();
        await Assert.That(app.Keyboard.KeyBindings.GetCommands(Key.Esc).SequenceEqual(new[] { Command.Quit })).IsTrue();
    }

    [Test]
    [TUnit.Core.Arguments("--help", null)]
    [TUnit.Core.Arguments("", null)]
    [TUnit.Core.Arguments("a", "b")]
    public async Task ArgumentsAreRejectedBeforeCreatingApplication(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];
        using var listener = Listen(out var activities);
        using var error = new StringWriter();
        var result = TuiApplication.Run(args, false, false, () => throw new InvalidOperationException(), error);
        await AssertOutcomeAsync(result, TerminalSessionStatus.UnsupportedArguments, false, 2, Arguments);
        await Assert.That(error.ToString()).IsEqualTo(Arguments + error.NewLine);
        await Assert.That(activities).IsEmpty();
    }

    [Test]
    [TUnit.Core.Arguments(true, false)]
    [TUnit.Core.Arguments(false, true)]
    [TUnit.Core.Arguments(true, true)]
    public async Task RedirectedTerminalIsRejectedBeforeCreatingApplication(bool input, bool output)
    {
        using var listener = Listen(out var activities);
        using var error = new StringWriter();
        var result = TuiApplication.Run([], input, output, () => throw new InvalidOperationException(), error);
        await AssertOutcomeAsync(result, TerminalSessionStatus.NonInteractiveTerminal, false, 2, Redirected);
        await Assert.That(error.ToString()).IsEqualTo(Redirected + error.NewLine);
        await Assert.That(activities).IsEmpty();
    }

    [Test]
    [TUnit.Core.Arguments(true, false)]
    [TUnit.Core.Arguments(false, true)]
    [TUnit.Core.Arguments(true, true)]
    public async Task ArgumentsTakePrecedenceOverRedirection(bool input, bool output)
    {
        using var listener = Listen(out var activities);
        using var error = new StringWriter();
        var result = TuiApplication.Run(["secret"], input, output, () => throw new InvalidOperationException(), error);
        await AssertOutcomeAsync(result, TerminalSessionStatus.UnsupportedArguments, false, 2, Arguments);
        await Assert.That(error.ToString()).IsEqualTo(Arguments + error.NewLine);
        await Assert.That(activities).IsEmpty();
    }

    [Test]
    public async Task InitializationFailureStillDisposesApplication()
    {
        var app = CreateApp();
        bool disposed = false;
        app.Init(null).Returns(_ => throw new InvalidOperationException("secret"));
        app.When(x => x.Dispose()).Do(_ => disposed = true);
        using var error = new RecordingWriter(() => disposed);
        var result = TuiApplication.Run([], false, false, () => app, error);
        await AssertFailureAsync(result, error);
        await Assert.That(error.CleanupObserved).IsTrue();
        app.Received(1).Init(null);
        app.DidNotReceive().Run(Arg.Any<IRunnable>(), Arg.Any<Func<Exception, bool>?>());
        app.Received(1).Dispose();
    }

    [Test]
    public async Task RunFailureDisposesWindowAndApplicationBeforeDiagnostic()
    {
        var app = CreateApp();
        bool windowDisposing = false, disposed = false, windowDisposedAtAppDispose = false;
        app.Run(Arg.Any<IRunnable>(), null).Returns(info => { var window = (Window)info[0]; window.Disposing += (_, _) => windowDisposing = true; throw new InvalidOperationException("secret"); });
        app.When(x => x.Dispose()).Do(_ => { disposed = true; windowDisposedAtAppDispose = windowDisposing; });
        using var error = new RecordingWriter(() => disposed);
        var result = TuiApplication.Run([], false, false, () => app, error);
        await AssertFailureAsync(result, error);
        await Assert.That(windowDisposing).IsTrue();
        await Assert.That(windowDisposedAtAppDispose).IsTrue();
        await Assert.That(error.CleanupObserved).IsTrue();
        app.Received(1).Run(Arg.Any<IRunnable>(), null);
        app.Received(1).Dispose();
    }

    [Test]
    [TUnit.Core.Arguments(true)]
    [TUnit.Core.Arguments(false)]
    public async Task FactoryOrDisposalFailureReturnsFailure(bool factoryFails)
    {
        var app = CreateApp();
        int attempts = 0;
        app.When(x => x.Dispose()).Do(_ => { attempts++; throw new InvalidOperationException("secret"); });
        using var error = new StringWriter();
        var result = TuiApplication.Run([], false, false, () => factoryFails ? throw new InvalidOperationException("secret") : app, error);
        await AssertOutcomeAsync(result, TerminalSessionStatus.Failed, false, 1, Failed);
        await Assert.That(error.ToString()).IsEqualTo(Failed + error.NewLine);
        await Assert.That(attempts).IsEqualTo(factoryFails ? 0 : 1);
        if (factoryFails)
        {
            app.DidNotReceive().Init(null);
            app.DidNotReceive().Run(Arg.Any<IRunnable>(), Arg.Any<Func<Exception, bool>?>());
        }
    }

    [Test]
    [TUnit.Core.Arguments(0, true, 0, null)]
    [TUnit.Core.Arguments(1, false, 2, Arguments)]
    [TUnit.Core.Arguments(2, false, 2, Redirected)]
    [TUnit.Core.Arguments(3, false, 1, Failed)]
    [TUnit.Core.Arguments(999, false, 1, Failed)]
    public async Task OutcomeMapsEveryStatusWithoutThrowing(int value, bool success, int code, string? diagnostic)
    {
        var status = (TerminalSessionStatus)value;
        await AssertOutcomeAsync(new(status), status, success, code, diagnostic);
        await AssertOutcomeAsync(default, TerminalSessionStatus.Completed, true, 0, null);
    }

    [Test]
    public async Task SessionActivitiesReportCleanupOutcome()
    {
        bool disposed = false, failedDisposed = false;
        var cleanupAtStop = new List<bool>();
        int activitiesCount = 0;
        using var listener = Listen(out var activities, () => cleanupAtStop.Add(activitiesCount++ == 0 ? disposed : failedDisposed));
        var app = CreateApp();
        app.When(x => x.Dispose()).Do(_ => disposed = true);
        using var error = new StringWriter();
        var success = TuiApplication.Run([], false, false, () => app, error);
        await Assert.That(disposed).IsTrue();
        await Assert.That(success.IsSuccess).IsTrue();
        var failedApp = CreateApp();
        failedApp.Run(Arg.Any<IRunnable>(), null).Returns(_ => throw new InvalidOperationException("secret"));
        failedApp.When(x => x.Dispose()).Do(_ => failedDisposed = true);
        var failure = TuiApplication.Run([], false, false, () => failedApp, error);
        await Assert.That(failedDisposed).IsTrue();
        await Assert.That(failure.IsSuccess).IsFalse();
        await Assert.That(activities.Count).IsEqualTo(2);
        await Assert.That(cleanupAtStop.SequenceEqual(new[] { true, true })).IsTrue();
        foreach (var activity in activities)
        {
            await Assert.That(activity.Source.Name).IsEqualTo("konstitech.DotNetDeck.Tui");
            await Assert.That(activity.OperationName).IsEqualTo("dotnetdeck.tui.session");
            await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Internal);
            await Assert.That(activity.TagObjects).IsEmpty();
        }
        await Assert.That(activities[0].Status).IsEqualTo(ActivityStatusCode.Ok);
        await Assert.That(activities[1].Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activities[1].StatusDescription).IsEqualTo("terminal_failure");
    }

    [Test]
    [TUnit.Core.Arguments(0)]
    [TUnit.Core.Arguments(1)]
    [TUnit.Core.Arguments(2)]
    public async Task DiagnosticWriterFailurePropagatesWithoutRetry(int path)
    {
        var app = CreateApp();
        bool disposed = false;
        app.When(x => x.Dispose()).Do(_ => disposed = true);
        app.Init(null).Returns(_ => throw new InvalidOperationException("secret"));
        using var error = new ThrowingWriter();
        await Assert.That(() => TuiApplication.Run(path == 0 ? ["arg"] : [], path == 1, false, () => app, error)).ThrowsExactly<IOException>();
        await Assert.That(error.Attempts).IsEqualTo(1);
        await Assert.That(disposed).IsEqualTo(path == 2);
    }

    private static IApplication CreateApp()
    {
        var app = Substitute.For<IApplication>();
        var keyboard = Substitute.For<IKeyboard>();
        keyboard.KeyBindings.Returns(new KeyBindings());
        app.Keyboard.Returns(keyboard);
        app.Init(null).Returns(app);
        return app;
    }

    private static ActivityListener Listen(out List<Activity> activities, Action? stopped = null)
    {
        var captured = new List<Activity>();
        activities = captured;
        var listener = new ActivityListener { ShouldListenTo = s => s.Name == "konstitech.DotNetDeck.Tui", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded, ActivityStopped = activity => { captured.Add(activity); stopped?.Invoke(); } };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task AssertOutcomeAsync(TerminalSessionOutcome result, TerminalSessionStatus status, bool success, int exitCode, string? diagnostic)
    {
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.IsSuccess).IsEqualTo(success);
        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(result.Diagnostic).IsEqualTo(diagnostic);
    }

    private static async Task AssertFailureAsync(TerminalSessionOutcome result, StringWriter writer)
    {
        await AssertOutcomeAsync(result, TerminalSessionStatus.Failed, false, 1, Failed);
        await Assert.That(writer.ToString()).IsEqualTo(Failed + writer.NewLine);
    }

    private sealed class RecordingWriter(Func<bool> cleanup) : StringWriter
    {
        public bool CleanupObserved { get; private set; }
        public override void WriteLine(string? value) { CleanupObserved = cleanup(); base.WriteLine(value); }
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public int Attempts { get; private set; }
        public override void WriteLine(string? value) { Attempts++; throw new IOException("sink failure"); }
    }
}

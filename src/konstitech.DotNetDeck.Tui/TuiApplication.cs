using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace konstitech.DotNetDeck.Tui;

internal static class TuiApplication
{
    internal static readonly ActivitySource Activities = new("konstitech.DotNetDeck.Tui");

    internal static TerminalSessionOutcome Run(
        ReadOnlySpan<string> args,
        bool isInputRedirected,
        bool isOutputRedirected,
        Func<IApplication> createApplication,
        TextWriter error)
    {
        if (args.Length != 0)
            return Report(TerminalSessionStatus.UnsupportedArguments, error);

        if (isInputRedirected || isOutputRedirected)
            return Report(TerminalSessionStatus.NonInteractiveTerminal, error);

        using Activity? activity = Activities.StartActivity("dotnetdeck.tui.session", ActivityKind.Internal);
        try
        {
            using (IApplication app = createApplication())
            {
                app.Init();
                using Window window = new() { Title = ".NETDeck", Width = Dim.Fill(), Height = Dim.Fill() };
                window.Add(new Label { X = 1, Y = 1, Text = "Project tools are not implemented yet. Press Esc or Quit to exit." });
                Button quit = new() { X = 1, Y = 3, Text = "_Quit" };
                window.Add(quit);
                quit.Accepting += (_, _) => app.RequestStop();
                app.Keyboard.KeyBindings.AddApp(Key.Esc, null, Command.Quit);
                app.Run(window);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return new TerminalSessionOutcome(TerminalSessionStatus.Completed);
        }
        catch (Exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "terminal_failure");
        }

        return Report(TerminalSessionStatus.Failed, error);
    }

    private static TerminalSessionOutcome Report(TerminalSessionStatus status, TextWriter error)
    {
        TerminalSessionOutcome outcome = new(status);
        error.WriteLine(outcome.Diagnostic);
        return outcome;
    }
}

namespace konstitech.DotNetDeck.Tui;

internal static class Program
{
    private static int Main(string[] args) => TuiApplication.Run(
        args,
        Console.IsInputRedirected,
        Console.IsOutputRedirected,
        static () => Terminal.Gui.App.Application.Create(),
        Console.Error).ExitCode;
}

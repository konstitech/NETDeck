namespace konstitech.DotNetDeck.Tui;

internal enum TerminalSessionStatus
{
    Completed = 0,
    UnsupportedArguments = 1,
    NonInteractiveTerminal = 2,
    Failed = 3
}

internal readonly record struct TerminalSessionOutcome(TerminalSessionStatus Status)
{
    internal bool IsSuccess => Status == TerminalSessionStatus.Completed;

    internal int ExitCode => Status switch
    {
        TerminalSessionStatus.Completed => 0,
        TerminalSessionStatus.UnsupportedArguments
            or TerminalSessionStatus.NonInteractiveTerminal => 2,
        _ => 1
    };

    internal string? Diagnostic => Status switch
    {
        TerminalSessionStatus.Completed => null,
        TerminalSessionStatus.UnsupportedArguments =>
            "NDK001: This initialization build does not accept arguments.",
        TerminalSessionStatus.NonInteractiveTerminal =>
            "NDK002: An interactive input and output terminal is required.",
        _ => "NDK003: The terminal session failed; terminal cleanup was attempted."
    };
}

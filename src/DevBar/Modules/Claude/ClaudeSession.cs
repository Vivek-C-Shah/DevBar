namespace DevBar.Modules.Claude;

public enum SessionState { Working, WaitingOnYou, Idle }

public sealed class ClaudeSession
{
    public required string Label { get; init; }
    public required int ProcessId { get; init; }
    public SessionState State { get; set; } = SessionState.Working;
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

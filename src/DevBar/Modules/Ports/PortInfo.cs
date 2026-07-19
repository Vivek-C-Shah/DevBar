namespace DevBar.Modules.Ports;

public sealed class PortInfo
{
    public required int Port { get; init; }
    public required int Pid { get; init; }
    public required string ProcessName { get; init; }
}

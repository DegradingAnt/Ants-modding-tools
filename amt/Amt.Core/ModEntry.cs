namespace Amt.Core;

/// One installed mod, read from a jar in the mods folder. The UI binds to these.
public sealed class ModEntry
{
    public string DisplayName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Version { get; init; } = "";
    public string ModId { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

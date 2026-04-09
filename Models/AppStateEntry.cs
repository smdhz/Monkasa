namespace Monkasa.Models;

public sealed class AppStateEntry
{
    public string StateKey { get; set; } = string.Empty;

    public string StateValue { get; set; } = string.Empty;

    public long UpdatedUtcTicks { get; set; }
}

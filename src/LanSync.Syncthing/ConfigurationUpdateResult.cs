namespace LanSync.Syncthing;

public sealed record ConfigurationUpdateResult(IReadOnlyList<string> ChangedProperties, bool RestartRequired);


namespace LanSync.Core;

public readonly record struct DeviceId
{
    public DeviceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Count(character => character == '-') != 7)
        {
            throw new ArgumentException("A Syncthing device ID must contain eight dash-separated groups.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}


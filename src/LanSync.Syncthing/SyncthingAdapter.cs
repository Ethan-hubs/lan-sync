namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public IReadOnlyList<string> LastConnectionWarnings { get; private set; } = Array.Empty<string>();

    public SyncthingAdapter(SyncthingRestClient restClient)
    {
        RestClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
    }

    private SyncthingRestClient RestClient { get; }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}


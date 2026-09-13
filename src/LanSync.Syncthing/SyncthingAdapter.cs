namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public SyncthingAdapter(SyncthingRestClient restClient)
    {
        RestClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
    }

    private SyncthingRestClient RestClient { get; }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}


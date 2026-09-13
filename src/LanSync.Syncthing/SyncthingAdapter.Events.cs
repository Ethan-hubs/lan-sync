using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task<IReadOnlyList<SyncthingEvent>> GetEventsAsync(
        long since = 0,
        TimeSpan? timeout = null,
        int limit = 100,
        IReadOnlyList<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutSeconds = Math.Max(0, (int)(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds);
        var query = new List<string>
        {
            $"since={since}",
            $"timeout={timeoutSeconds}",
            $"limit={limit}",
        };
        if (eventTypes is { Count: > 0 })
        {
            query.Add("events=" + string.Join(',', eventTypes.Select(Escape)));
        }

        var response = await RestClient.GetAsync("/rest/events?" + string.Join('&', query), cancellationToken).ConfigureAwait(false);
        if (response is not JsonArray events)
        {
            throw new InvalidDataException("Syncthing events returned a non-array JSON response.");
        }

        return events.OfType<JsonObject>().Select(ParseEvent).ToArray();
    }

    public async IAsyncEnumerable<SyncthingEvent> SubscribeEventsAsync(
        long since = 0,
        TimeSpan? longPollTimeout = null,
        int limit = 100,
        IReadOnlyList<string>? eventTypes = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = since;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await GetEventsAsync(cursor, longPollTimeout, limit, eventTypes, cancellationToken).ConfigureAwait(false);
            foreach (var syncthingEvent in events)
            {
                cursor = Math.Max(cursor, syncthingEvent.Id);
                yield return syncthingEvent;
            }

            if (events.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static SyncthingEvent ParseEvent(JsonObject item)
    {
        DateTimeOffset? timestamp = null;
        if (DateTimeOffset.TryParse(item["time"]?.GetValue<string>(), out var parsedTimestamp))
        {
            timestamp = parsedTimestamp;
        }

        return new SyncthingEvent(
            item["id"]?.GetValue<long>() ?? 0,
            timestamp,
            item["type"]?.GetValue<string>() ?? string.Empty,
            item["data"]?.DeepClone());
    }
}

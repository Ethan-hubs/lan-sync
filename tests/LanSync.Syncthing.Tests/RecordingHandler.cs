using System.Net;
using System.Text;

namespace LanSync.Syncthing.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null,
            body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"No response queued for {request.Method} {request.RequestUri}.");
        }

        return _responses.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? ApiKey, string? Body);

internal static class TestAdapter
{
    public static (SyncthingAdapter Adapter, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8384") };
        return (new SyncthingAdapter(new SyncthingRestClient(client, "test-key")), handler);
    }
}

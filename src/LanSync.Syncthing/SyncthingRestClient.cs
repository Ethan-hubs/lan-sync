using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace LanSync.Syncthing;

public sealed class SyncthingRestClient
{
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public SyncthingRestClient(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonNode?> PostAsync(string path, JsonNode? body = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, path, body, cancellationToken);

    public Task<JsonNode?> PutAsync(string path, JsonNode body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, path, body, cancellationToken);

    public Task<JsonNode?> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, path, null, cancellationToken);

    public async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken = default,
        int retries = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Syncthing REST paths must begin with '/'.", nameof(path));
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-API-Key", _apiKey);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (TransientStatusCodes.Contains(response.StatusCode) && attempt < retries)
                    {
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new SyncthingApiException(method, path, response.StatusCode, responseBody);
                }

                return string.IsNullOrWhiteSpace(responseBody) ? null : JsonNode.Parse(responseBody);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SyncthingApiException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (attempt < retries)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SyncthingApiException(method, path, null, exception.Message, exception);
            }
        }
    }

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
}

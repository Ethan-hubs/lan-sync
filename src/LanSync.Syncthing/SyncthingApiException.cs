using System.Net;

namespace LanSync.Syncthing;

public sealed class SyncthingApiException : Exception
{
    public SyncthingApiException(HttpMethod method, string path, HttpStatusCode? statusCode, string responseBody, Exception? innerException = null)
        : base(BuildMessage(method, path, statusCode, responseBody), innerException)
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpMethod Method { get; }

    public string Path { get; }

    public HttpStatusCode? StatusCode { get; }

    public string ResponseBody { get; }

    private static string BuildMessage(HttpMethod method, string path, HttpStatusCode? statusCode, string responseBody)
    {
        var status = statusCode is null ? "transport" : $"HTTP {(int)statusCode.Value}";
        var body = responseBody.Length <= 300 ? responseBody : responseBody[..300];
        return $"{method.Method} {path} -> {status}: {body}";
    }
}


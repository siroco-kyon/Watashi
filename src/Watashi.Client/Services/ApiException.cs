using System.Net;

namespace Watashi.Client.Services;

public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode code, string? message)
        : base(message ?? $"HTTP {(int)code}")
    {
        StatusCode = code;
    }
}

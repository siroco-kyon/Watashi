using System.Net;

namespace Watashi.Client.Services;

public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public ApiException(HttpStatusCode code, string? message, string? errorCode = null)
        : base(message ?? $"HTTP {(int)code}")
    {
        StatusCode = code;
        ErrorCode = errorCode;
    }
}

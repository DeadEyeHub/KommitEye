using System.Net;
using System.Text.RegularExpressions;

namespace KommitEye.Services;

public enum ReviewApiErrorKind { Unknown, ContextOverflow, Authentication, Configuration, RateLimit, Service, Network }

public sealed class ReviewApiException : HttpRequestException
{
    public ReviewApiErrorKind Kind { get; }
    public bool ContextOverflow => Kind == ReviewApiErrorKind.ContextOverflow;
    public bool IsTransient => Kind is ReviewApiErrorKind.RateLimit or ReviewApiErrorKind.Service or ReviewApiErrorKind.Network;
    public string? RequestId { get; }
    public string ResponseExcerpt { get; }
    public TimeSpan? RetryAfter { get; }

    public ReviewApiException(ReviewApiErrorKind kind, string message, HttpStatusCode? status = null,
        string? requestId = null, string excerpt = "", TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner, status)
    {
        Kind = kind;
        RequestId = requestId;
        ResponseExcerpt = excerpt;
        RetryAfter = retryAfter;
    }

    internal static string Sanitize(string text, string? secret = null, int limit = 2048)
    {
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = GitService.SanitizeDiagnostic(text);
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s""<>]+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?i)([""']?(?:api[_-]?key|access_token|password|authorization)[""']?\s*[:=]\s*[""']?)[^\s""',}]+", "$1[redacted]");
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length <= limit ? text : text[..limit] + "…";
    }
}

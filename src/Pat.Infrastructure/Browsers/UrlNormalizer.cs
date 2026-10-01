namespace Pat.Infrastructure.Browsers;

/// <summary>
/// Разбор URL из расширения: домен для агрегации хранится денормализованно в payload.
/// Не-web схемы (file://, chrome://, about:) не записываются вовсе — приватность и шум.
/// </summary>
public static class UrlNormalizer
{
    public static bool TryExtractDomain(string? url, out string domain)
    {
        domain = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0)
            return false;

        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        domain = host;
        return true;
    }
}

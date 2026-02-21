using System.Net;

namespace RedirectUrlInterceptor;

internal sealed class RedirectResolver : IDisposable
{
    private readonly HttpClient _httpClient;

    public RedirectResolver(int maxHops, int timeoutSeconds)
    {
        MaxHops = maxHops;
        TimeoutSeconds = timeoutSeconds;

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    public int MaxHops { get; }

    public int TimeoutSeconds { get; }

    public async Task<RedirectTrace> ResolveAsync(string url, CancellationToken cancellationToken)
    {
        var hops = new List<RedirectHop>();
        var current = url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri) ||
            (currentUri.Scheme != Uri.UriSchemeHttp && currentUri.Scheme != Uri.UriSchemeHttps))
        {
            return new RedirectTrace(url, url, hops, "Only HTTP(S) URLs are supported.");
        }

        for (var i = 1; i <= MaxHops; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, current);
                using var response = await SendWithFallbackAsync(req, current, cancellationToken);

                var statusCode = (int)response.StatusCode;
                if (!IsRedirect(response.StatusCode))
                {
                    hops.Add(new RedirectHop(i, current, statusCode, null, null));
                    return new RedirectTrace(url, current, hops, null);
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    hops.Add(new RedirectHop(i, current, statusCode, null, "Redirect without Location header."));
                    return new RedirectTrace(url, current, hops, "Redirect without Location header.");
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                var nextUrl = nextUri.ToString();
                hops.Add(new RedirectHop(i, current, statusCode, nextUrl, null));

                current = nextUrl;
                currentUri = nextUri;
            }
            catch (Exception ex)
            {
                hops.Add(new RedirectHop(i, current, null, null, ex.Message));
                return new RedirectTrace(url, current, hops, ex.Message);
            }
        }

        return new RedirectTrace(url, current, hops, $"Max hops ({MaxHops}) reached.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendWithFallbackAsync(
        HttpRequestMessage headRequest,
        string url,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed &&
            response.StatusCode != HttpStatusCode.NotImplemented)
        {
            return response;
        }

        response.Dispose();
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
        return await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 301 or 302 or 303 or 307 or 308;
    }
}

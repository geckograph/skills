// GeckoGraph widget relay for ASP.NET Core. Copy into the app (any namespace) and point the widget at it:
// data-base-url="/api/geckograph" data-discovery-url="/api/geckograph".
//
// Needed when the app signs users in with an HttpOnly cookie: the browser never hands that cookie to a
// cross-origin script, so this controller, on the app's own origin, receives it and passes it to GeckoGraph
// when a conversation starts. GeckoGraph then calls the app's API with the signed-in user's own session.
//
//   /api/geckograph/api/agent/...  -> the GeckoGraph agent
//   /api/geckograph/api/...        -> the rest of GeckoGraph (the widget's bootstrap)
//
// Configuration (appsettings, user-secrets or environment, e.g. GeckoGraph__AgentUrl):
//   GeckoGraph:AgentUrl       default https://app.geckograph.com
//   GeckoGraph:DiscoveryUrl   default https://app.geckograph.com
//   GeckoGraph:TargetOrigin   optional: the origin the agent calls the app's API at, when it is not this one
//   GeckoGraph:DevToken       on a developer's machine only: written by `geckograph widget_config` to the
//                             gitignored appsettings.Local.json (load it with reloadOnChange: true) and renewed by
//                             `geckograph dev`. Never set it in a deploy.
//
// Behind a proxy that terminates TLS, enable forwarded headers so Request.Scheme and Request.Host are the
// public origin; otherwise the same-origin check below refuses every POST.
// GeckoGraph's relearn never turns /api/geckograph/* into a tool, so it needs no geckograph.yml line.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

[ApiController]
[Route("api/geckograph")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class GeckoGraphRelayController : ControllerBase
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan, // the reply streams; the request's own abort ends it
    };

    /// <summary>The request headers the widget sends. Nothing else reaches GeckoGraph; the Cookie header never does.</summary>
    private static readonly string[] ForwardHeaders =
    {
        "Accept", "Origin", "Referer", "X-Agentify-Api-Key", "X-GeckoGraph-Dev-Token",
    };

    private static readonly HashSet<string> StripResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Encoding", "Content-Length", "Transfer-Encoding", "Connection", "Set-Cookie",
    };

    private static readonly System.Text.RegularExpressions.Regex DevTokenBody =
        new(@"^api/agent/conversations/(start|[^/]+/messages(/stream)?)$");

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public GeckoGraphRelayController(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>Headers only a proxy or tunnel in front of the app adds: present, the caller is not on this machine.</summary>
    private static readonly string[] ProxyHeaders =
    {
        "Forwarded", "Via", "X-Real-IP", "X-Forwarded-Server", "True-Client-IP", "CF-Connecting-IP", "CF-Ray",
    };

    /// <summary>
    /// The developer's dev token, for a page on their own machine in Development. The relay attaches it to each call,
    /// so the token is read fresh from the local config and never sits in a page or a build. Only for a request the
    /// browser sent from a loopback page straight to this server: never through a tunnel or proxy, whatever Host says.
    /// </summary>
    private string? LocalDevToken()
    {
        var token = _configuration["GeckoGraph:DevToken"]?.Trim();
        if (string.IsNullOrEmpty(token) || !_environment.IsDevelopment()) return null;
        // The connection itself comes from this machine, so another machine on the network never gets the token.
        var peer = HttpContext.Connection.RemoteIpAddress;
        if (peer is null || !IsLoopback(peer)) return null;
        if (ViaProxy()) return null;
        if (!Uri.TryCreate(BrowserOrigin(), UriKind.Absolute, out var uri)) return null;
        return uri.IsLoopback ? token : null;
    }

    /// <summary>
    /// The page's origin as the browser itself sent it: the Origin header, or, on a GET (browsers send no Origin on a
    /// same-origin GET), the origin of its Referer. Never an origin worked out from the Host header, which a tunnel can
    /// rewrite to localhost.
    /// </summary>
    private string? BrowserOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0) return origin;
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method)) return null;
        return Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            ? referer.GetLeftPart(UriPartial.Authority)
            : null;
    }

    /// <summary>
    /// Whether a proxy or tunnel passed the request on. X-Forwarded-For and X-Forwarded-Host (and the X-Original-
    /// copies the forwarded-headers middleware leaves) count only when they name a host or address off this machine: a
    /// local front server such as IIS out of process names this machine, while a tunnel that rewrites Host to localhost
    /// still names its public host or its caller.
    /// </summary>
    private bool ViaProxy()
    {
        if (ProxyHeaders.Any(Request.Headers.ContainsKey)) return true;
        foreach (var name in new[] { "X-Forwarded-Host", "X-Original-Host", "X-Forwarded-For", "X-Original-For" })
        {
            var value = Request.Headers[name].ToString();
            if (value.Length > 0 && value.Split(',').Any(a => !IsLoopbackAddress(a))) return true;
        }
        return false;
    }

    /// <summary>A host or address as a forwarding header writes it (<c>localhost:5000</c>, <c>127.0.0.1</c>, <c>[::1]:5000</c>) is loopback.</summary>
    private static bool IsLoopbackAddress(string entry)
    {
        var a = entry.Trim();
        if (a.StartsWith('['))
        {
            var end = a.IndexOf(']');
            a = end > 0 ? a[1..end] : a;
        }
        else if (a.Count(c => c == ':') == 1)
        {
            a = a[..a.IndexOf(':')];
        }
        if (string.Equals(a, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(a, out var ip) && IsLoopback(ip);
    }

    /// <summary>Loopback, also written as IPv6 (<c>::ffff:127.0.0.1</c>, which a dual-mode socket reports for IPv4).</summary>
    private static bool IsLoopback(IPAddress ip) =>
        IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    [HttpGet("{**path}")]
    [HttpPost("{**path}")]
    [HttpDelete("{**path}")]
    public async Task Relay(string path, CancellationToken cancellationToken)
    {
        var self = $"{Request.Scheme}://{Request.Host.Value}";
        if (!HttpMethods.IsGet(Request.Method))
        {
            // Same origin removes CORS, not CSRF: a state-changing call must come from this app's own pages.
            var origin = Request.Headers.Origin.ToString();
            var site = Request.Headers["Sec-Fetch-Site"].ToString();
            var foreign = origin.Length > 0
                ? !string.Equals(origin, self, StringComparison.OrdinalIgnoreCase)
                : site is "cross-site" or "cross-origin";
            if (foreign)
            {
                await Refuse(403, "Cross-origin request rejected", cancellationToken);
                return;
            }
            // A browser names where a state-changing call comes from; a call that says neither is not from this app's pages.
            if (origin.Length == 0 && site.Length == 0)
            {
                await Refuse(403, "Request without Origin or Sec-Fetch-Site rejected", cancellationToken);
                return;
            }
        }

        var segments = (path ?? string.Empty).Split('/');
        if (segments.Length < 2 || segments[0] != "api"
            || segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\\')))
        {
            await Refuse(404, "Unknown GeckoGraph route", cancellationToken);
            return;
        }
        var isAgent = segments[1] == "agent";
        var upstreamBase = (isAgent
            ? _configuration["GeckoGraph:AgentUrl"]
            : _configuration["GeckoGraph:DiscoveryUrl"]) ?? "https://app.geckograph.com";
        var baseUri = new Uri(upstreamBase.TrimEnd('/') + "/");
        var url = new Uri(baseUri, string.Join('/', segments.Select(Uri.EscapeDataString)) + Request.QueryString.Value);
        if (!string.Equals(url.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(400, "Bad GeckoGraph route", cancellationToken);
            return;
        }

        using var upstream = new HttpRequestMessage(new HttpMethod(Request.Method), url);
        foreach (var name in ForwardHeaders)
        {
            var value = Request.Headers[name].ToString();
            if (value.Length > 0) upstream.Headers.TryAddWithoutValidation(name, value);
        }
        // A same-origin GET often carries no Origin; GeckoGraph checks the page's origin, so name it.
        if (!upstream.Headers.Contains("Origin")) upstream.Headers.TryAddWithoutValidation("Origin", self);
        var devToken = LocalDevToken();
        if (devToken is not null)
        {
            upstream.Headers.Remove("X-GeckoGraph-Dev-Token");
            upstream.Headers.TryAddWithoutValidation("X-GeckoGraph-Dev-Token", devToken);
        }
        // The MCP panel's calls carry its widget session as a bearer token. Forward that one kind only: the app's own
        // tokens never reach GeckoGraph.
        var panelAuth = Request.Headers.Authorization.ToString();
        if (panelAuth.StartsWith("Bearer ggws_", StringComparison.Ordinal)) upstream.Headers.TryAddWithoutValidation("Authorization", panelAuth);

        if (!HttpMethods.IsGet(Request.Method))
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken);
            if (isAgent && string.Join('/', segments) == "api/agent/conversations/start")
            {
                JsonObject start;
                try
                {
                    start = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject ?? new JsonObject();
                }
                catch (JsonException)
                {
                    await Refuse(400, "Conversation start is not JSON", cancellationToken);
                    return;
                }
                var cookie = Request.Headers.Cookie.ToString();
                if (cookie.Length > 0)
                {
                    // Passed as is, so a signed session cookie still verifies byte for byte.
                    var auth = start["auth"] as JsonObject ?? new JsonObject();
                    auth["cookies"] = cookie;
                    start["auth"] = auth;
                }
                var target = _configuration["GeckoGraph:TargetOrigin"];
                if (!string.IsNullOrWhiteSpace(target)) start["targetOrigin"] = new Uri(target).GetLeftPart(UriPartial.Authority);
                if (devToken is not null) start["devWidgetToken"] = devToken;
                body = start.ToJsonString();
            }
            else if (isAgent && devToken is not null && DevTokenBody.IsMatch(string.Join('/', segments)))
            {
                try
                {
                    if (JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) is JsonObject message)
                    {
                        message["devWidgetToken"] = devToken;
                        body = message.ToJsonString();
                    }
                }
                catch (JsonException)
                {
                    // Not JSON: passed on as it came.
                }
            }
            upstream.Content = new StringContent(body, Encoding.UTF8);
            upstream.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                string.IsNullOrEmpty(Request.ContentType) ? "application/json" : Request.ContentType);
        }

        HttpResponseMessage answer;
        try
        {
            answer = await Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            await Refuse(502, "GeckoGraph unavailable", cancellationToken);
            return;
        }

        using (answer)
        {
            Response.StatusCode = (int)answer.StatusCode;
            foreach (var header in answer.Headers.Concat(answer.Content.Headers))
            {
                if (!StripResponseHeaders.Contains(header.Key)) Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (answer.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                // The reply streams: no proxy in front of the app may buffer it.
                Response.Headers.CacheControl = "no-cache, no-transform";
                Response.Headers["X-Accel-Buffering"] = "no";
            }
            await using var stream = await answer.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
    }

    private async Task Refuse(int status, string message, CancellationToken cancellationToken)
    {
        Response.StatusCode = status;
        await Response.WriteAsync(message, cancellationToken);
    }
}

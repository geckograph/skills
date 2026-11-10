// GeckoGraph widget relay for ASP.NET on .NET Framework (Web Forms, MVC 5, Web API 2). C# 5.
//
// Needed when the app signs users in with an HttpOnly cookie: the browser never hands that cookie to a
// cross-origin script, so this handler, on the app's own origin, receives it and passes it to GeckoGraph
// when a conversation starts. GeckoGraph then calls the app's API with the signed-in user's own session.
//
//   /api/geckograph/api/agent/...  -> the GeckoGraph agent
//   /api/geckograph/api/...        -> the rest of GeckoGraph (the widget's bootstrap)
//
// Setup:
// 1. Add this file to the web project (a <Compile Include> entry in the .csproj, or App_Code for a Web Site).
// 2. Register it in Web.config for everything under api/geckograph. A handler path of "api/geckograph/*" does not
//    match the widget's deeper URLs (IIS falls through to the static file handler and answers 404), so map "*" inside
//    a <location> for the folder. When the app requires sign-in for every page (forms authentication), allow the
//    relay too: the widget's calls carry the app's own token, and GeckoGraph checks them.
//      <location path="api/geckograph">
//        <system.web><authorization><allow users="*" /></authorization></system.web>
//        <system.webServer><handlers>
//          <add name="GeckoGraphRelay" path="*" verb="GET,POST" type="GeckoGraphRelayHandler" preCondition="integratedMode" />
//        </handlers></system.webServer>
//      </location>
//    If the app's route table would claim the path first, add routes.Ignore("api/geckograph/{*path}") to it.
// 3. Point the widget at it: data-base-url="/api/geckograph" data-discovery-url="/api/geckograph".
// 4. appSettings (all optional):
//      GeckoGraphAgentUrl       default https://app.geckograph.com
//      GeckoGraphDiscoveryUrl   default https://app.geckograph.com
//      GeckoGraphTargetOrigin   the origin the agent calls the app's API at, when it is not this one
//
// GeckoGraph's relearn never turns /api/geckograph/* into a tool, so it needs no geckograph.yml line.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

public class GeckoGraphRelayHandler : IHttpHandler
{
    const string Prefix = "/api/geckograph/";
    const string DefaultUpstream = "https://app.geckograph.com";

    // The request headers the widget sends. Nothing else reaches GeckoGraph; the Cookie header never does.
    static readonly string[] ForwardHeaders = { "Content-Type", "Accept", "Origin", "Referer", "X-Agentify-Api-Key", "X-GeckoGraph-Dev-Token" };

    static readonly HashSet<string> StripResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Encoding", "Content-Length", "Transfer-Encoding", "Connection", "Set-Cookie", "Content-Type",
    };

    public bool IsReusable { get { return true; } }

    public void ProcessRequest(HttpContext context)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = context.Request;
        var res = context.Response;
        var self = req.Url.GetLeftPart(UriPartial.Authority);

        if (req.HttpMethod != "GET")
        {
            // Same origin removes CORS, not CSRF: a state-changing call must come from this app's own pages.
            var origin = req.Headers["Origin"];
            var site = req.Headers["Sec-Fetch-Site"];
            var foreign = !string.IsNullOrEmpty(origin)
                ? !string.Equals(origin, self, StringComparison.OrdinalIgnoreCase)
                : site == "cross-site" || site == "cross-origin";
            if (foreign)
            {
                Refuse(res, 403, "Cross-origin request rejected");
                return;
            }
        }

        var local = req.Url.AbsolutePath;
        var applicationPath = req.ApplicationPath ?? "/";
        if (applicationPath.Length > 1 && local.StartsWith(applicationPath, StringComparison.OrdinalIgnoreCase))
        {
            local = local.Substring(applicationPath.Length);
            if (!local.StartsWith("/")) local = "/" + local;
        }
        if (!local.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(res, 404, "Unknown GeckoGraph route");
            return;
        }
        var sub = local.Substring(Prefix.Length - 1); // keeps the leading slash: /api/...
        var segments = sub.Substring(1).Split('/');
        if (segments.Length < 2 || segments[0] != "api")
        {
            Refuse(res, 404, "Unknown GeckoGraph route");
            return;
        }
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOf('\\') >= 0)
            {
                Refuse(res, 404, "Unknown GeckoGraph route");
                return;
            }
        }
        var isAgent = segments[1] == "agent";
        var upstreamBase = Setting(isAgent ? "GeckoGraphAgentUrl" : "GeckoGraphDiscoveryUrl") ?? DefaultUpstream;
        var baseUri = new Uri(upstreamBase.TrimEnd('/') + "/");
        var url = new Uri(baseUri, sub.Substring(1) + req.Url.Query);
        if (!string.Equals(url.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            Refuse(res, 400, "Bad GeckoGraph route");
            return;
        }

        var upstream = (HttpWebRequest)WebRequest.Create(url);
        upstream.Method = req.HttpMethod;
        upstream.AllowAutoRedirect = false;
        upstream.Timeout = 300000;
        upstream.ReadWriteTimeout = 300000;
        foreach (var name in ForwardHeaders)
        {
            var value = req.Headers[name];
            if (string.IsNullOrEmpty(value)) continue;
            if (name == "Content-Type") upstream.ContentType = value;
            else if (name == "Accept") upstream.Accept = value;
            else if (name == "Referer") upstream.Referer = value;
            else upstream.Headers[name] = value;
        }
        // A same-origin GET often carries no Origin; GeckoGraph checks the page's origin, so name it.
        if (string.IsNullOrEmpty(upstream.Headers["Origin"])) upstream.Headers["Origin"] = self;

        if (req.HttpMethod != "GET")
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            if (isAgent && sub == "/api/agent/conversations/start")
            {
                body = WithSession(body, req.Headers["Cookie"]);
                if (body == null)
                {
                    Refuse(res, 400, "Conversation start is not JSON");
                    return;
                }
                if (string.IsNullOrEmpty(upstream.ContentType)) upstream.ContentType = "application/json";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            upstream.ContentLength = bytes.Length;
            using (var stream = upstream.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }

        HttpWebResponse answer;
        try
        {
            answer = (HttpWebResponse)upstream.GetResponse();
        }
        catch (WebException e)
        {
            answer = e.Response as HttpWebResponse;
            if (answer == null)
            {
                Refuse(res, 502, "GeckoGraph unavailable");
                return;
            }
        }

        using (answer)
        {
            res.StatusCode = (int)answer.StatusCode;
            res.ContentType = answer.ContentType;
            foreach (string name in answer.Headers.AllKeys)
            {
                if (!StripResponseHeaders.Contains(name)) res.Headers[name] = answer.Headers[name];
            }
            if ((answer.ContentType ?? string.Empty).StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // The reply streams: no proxy in front of the app may buffer it.
                res.Headers["Cache-Control"] = "no-cache, no-transform";
                res.Headers["X-Accel-Buffering"] = "no";
            }
            res.BufferOutput = false;
            using (var stream = answer.GetResponseStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (!res.IsClientConnected) break;
                    res.OutputStream.Write(buffer, 0, read);
                    res.Flush();
                }
            }
        }
    }

    // The conversation start, with the page user's cookie as auth.cookies (passed as is, so a signed session
    // cookie still verifies byte for byte) and the configured target origin. Null when the body is not JSON.
    static string WithSession(string body, string cookie)
    {
        var json = new JavaScriptSerializer();
        Dictionary<string, object> start;
        try
        {
            start = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(body);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (start == null) return null;
        if (!string.IsNullOrEmpty(cookie))
        {
            object existing;
            var auth = start.TryGetValue("auth", out existing) ? existing as Dictionary<string, object> : null;
            if (auth == null) auth = new Dictionary<string, object>();
            auth["cookies"] = cookie;
            start["auth"] = auth;
        }
        var target = Setting("GeckoGraphTargetOrigin");
        if (target != null) start["targetOrigin"] = new Uri(target).GetLeftPart(UriPartial.Authority);
        return json.Serialize(start);
    }

    static string Setting(string name)
    {
        var value = ConfigurationManager.AppSettings[name];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static void Refuse(HttpResponse res, int status, string message)
    {
        res.StatusCode = status;
        res.ContentType = "text/plain";
        res.Write(message);
    }
}

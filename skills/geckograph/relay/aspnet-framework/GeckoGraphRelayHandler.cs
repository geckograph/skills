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
//          <add name="GeckoGraphRelay" path="*" verb="GET,POST,DELETE" type="GeckoGraphRelayHandler" preCondition="integratedMode" />
//        </handlers></system.webServer>
//      </location>
//    If the app's route table would claim the path first, add routes.Ignore("api/geckograph/{*path}") to it.
// 3. Point the widget at it: data-base-url="/api/geckograph" data-discovery-url="/api/geckograph".
// 4. appSettings (all optional):
//      GeckoGraphAgentUrl       default https://app.geckograph.com
//      GeckoGraphDiscoveryUrl   default https://app.geckograph.com
//      GeckoGraphTargetOrigin   the origin the agent calls the app's API at, when it is not this one
//      GeckoGraphDevToken       on a developer's machine only: written by `geckograph widget_config` to the gitignored
//                               Web.Local.config and renewed by `geckograph dev`. Never set it in a deploy.
//
// GeckoGraph's relearn never turns /api/geckograph/* into a tool, so it needs no geckograph.yml line.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
#if !GECKOGRAPH_RELAY_TESTS
using System.Configuration;
using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Hosting;
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

    // The agent calls that carry the dev token in their body: a chat's start and its messages.
    static readonly Regex DevTokenBody = new Regex("^/api/agent/conversations/(start|[^/]+/messages(/stream)?)$");

    public bool IsReusable { get { return true; } }

    public void ProcessRequest(HttpContext context)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = context.Request;
        var res = context.Response;
        var self = req.Url.GetLeftPart(UriPartial.Authority);

        // Same origin removes CORS, not CSRF: a state-changing call must come from this app's own pages.
        var refusal = GeckoGraphRelayGuard.SameOriginRefusal(req.HttpMethod, req.Headers["Origin"], req.Headers["Sec-Fetch-Site"], self);
        if (refusal != null)
        {
            Refuse(res, 403, refusal);
            return;
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
        var devToken = LocalDevToken(context);
        if (devToken != null) upstream.Headers["X-GeckoGraph-Dev-Token"] = devToken;
        // The MCP panel's calls carry its widget session as a bearer token. Forward that one kind only: the app's own
        // tokens never reach GeckoGraph.
        var panelAuth = req.Headers["Authorization"];
        if (panelAuth != null && panelAuth.StartsWith("Bearer ggws_", StringComparison.Ordinal)) upstream.Headers["Authorization"] = panelAuth;

        if (req.HttpMethod != "GET")
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            if (isAgent && sub == "/api/agent/conversations/start")
            {
                body = WithSession(body, req.Headers["Cookie"], devToken);
                if (body == null)
                {
                    Refuse(res, 400, "Conversation start is not JSON");
                    return;
                }
                if (string.IsNullOrEmpty(upstream.ContentType)) upstream.ContentType = "application/json";
            }
            else if (isAgent && devToken != null && DevTokenBody.IsMatch(sub))
            {
                body = WithDevToken(body, devToken);
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
    static string WithSession(string body, string cookie, string devToken)
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
        if (devToken != null) start["devWidgetToken"] = devToken;
        return json.Serialize(start);
    }

    // A chat message with the dev token added; the body as it came when it is not a JSON object.
    static string WithDevToken(string body, string devToken)
    {
        var json = new JavaScriptSerializer();
        try
        {
            var message = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(body);
            if (message == null) return body;
            message["devWidgetToken"] = devToken;
            return json.Serialize(message);
        }
        catch (ArgumentException)
        {
            return body;
        }
        catch (InvalidOperationException)
        {
            return body;
        }
    }

    // The developer's dev token, for a page on their own machine in a debug build. The relay attaches it to each call,
    // so it never sits in a page or a build. Only for a request the browser sent from a loopback page straight to this
    // server (GeckoGraphRelayGuard.MayAttachDevToken): never through a tunnel or proxy, whatever Host says.
    //
    // READ FROM THE FILE ITSELF. `geckograph dev` renews the token every few hours and rewrites Web.Local.config, which
    // Web.config names in <appSettings file="Web.Local.config">. ASP.NET does not restart for a change to that file, and
    // ConfigurationManager.RefreshSection("appSettings") does not re-read it either (checked under IIS Express: a replaced
    // token kept answering with the old one), so the relay kept the token it started with until the server restarted.
    // The relay now reads the file named there, again whenever its modification time changes.
    static readonly GeckoGraphFileCache LocalFiles = new GeckoGraphFileCache(
        delegate(string path) { return File.Exists(path) ? (DateTime?)File.GetLastWriteTimeUtc(path) : null; },
        File.ReadAllText);

    static string LocalDevToken(HttpContext context)
    {
        var req = context.Request;
        // Request.IsLocal: the connection itself comes from this machine, so another machine on the network never gets it.
        if (!context.IsDebuggingEnabled || !GeckoGraphRelayGuard.MayAttachDevToken(req.HttpMethod, req.IsLocal, delegate(string name) { return req.Headers[name]; })) return null;
        var webConfig = HostingEnvironment.MapPath("~/Web.config");
        var fromFile = webConfig == null ? null
            : GeckoGraphLocalSettings.FromAppSettingsFile(LocalFiles, webConfig, "GeckoGraphDevToken");
        // Set in Web.config itself instead: a change there restarts the app, so the loaded value is current.
        return fromFile ?? Setting("GeckoGraphDevToken");
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
#endif

// Pure: which calls the relay refuses, and which may carry the developer's dev token. No System.Web.
public static class GeckoGraphRelayGuard
{
    // Headers only a proxy or tunnel in front of the app adds: present, the caller is not on this machine.
    static readonly string[] ProxyHeaders = { "Forwarded", "Via", "X-Real-IP", "X-Forwarded-Server", "True-Client-IP", "CF-Connecting-IP", "CF-Ray" };

    static bool IsSafe(string method)
    {
        return method == "GET" || method == "HEAD";
    }

    // Null when a call may go on; else why it is refused (403). A state-changing call must come from this app's own
    // pages: an Origin, when present, is this origin; without one, Sec-Fetch-Site is not cross-site; a call with
    // neither is not from a browser on this app's pages.
    public static string SameOriginRefusal(string method, string origin, string fetchSite, string self)
    {
        if (IsSafe(method)) return null;
        if (!string.IsNullOrEmpty(origin))
            return string.Equals(origin, self, StringComparison.OrdinalIgnoreCase) ? null : "Cross-origin request rejected";
        if (fetchSite == "cross-site" || fetchSite == "cross-origin") return "Cross-origin request rejected";
        if (string.IsNullOrEmpty(fetchSite)) return "Request without Origin or Sec-Fetch-Site rejected";
        return null;
    }

    // Whether the dev token may go with this call: the connection comes from this machine (fromThisMachine, the handler's
    // Request.IsLocal), the browser's own page origin is loopback, and no proxy or tunnel passed the call on. The page
    // origin is the Origin header or, on a GET (browsers send no Origin on a same-origin GET), its Referer's; never an
    // origin worked out from the Host header, which a tunnel can rewrite to localhost.
    public static bool MayAttachDevToken(string method, bool fromThisMachine, Func<string, string> header)
    {
        if (!fromThisMachine || ViaProxy(header)) return false;
        var origin = header("Origin");
        if (string.IsNullOrEmpty(origin) && IsSafe(method)) origin = header("Referer");
        Uri uri;
        return !string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out uri) && uri.IsLoopback;
    }

    // X-Forwarded-For and X-Forwarded-Host (and the X-Original- copies a forwarded-headers module leaves) count only when
    // they name a host or address off this machine: a local front server names this machine, while a tunnel that
    // rewrites Host to localhost still names its public host or its caller.
    static bool ViaProxy(Func<string, string> header)
    {
        foreach (var name in ProxyHeaders)
        {
            if (!string.IsNullOrEmpty(header(name))) return true;
        }
        foreach (var name in new[] { "X-Forwarded-Host", "X-Original-Host", "X-Forwarded-For", "X-Original-For" })
        {
            var value = header(name);
            if (string.IsNullOrEmpty(value)) continue;
            foreach (var entry in value.Split(','))
            {
                if (!IsLoopbackAddress(entry)) return true;
            }
        }
        return false;
    }

    // A host or address as a forwarding header writes it (localhost:5000, 127.0.0.1, [::1]:5000, ::1) is loopback.
    static bool IsLoopbackAddress(string entry)
    {
        var a = entry.Trim();
        if (a.StartsWith("["))
        {
            var end = a.IndexOf(']');
            if (end > 0) a = a.Substring(1, end - 1);
        }
        else if (a.IndexOf(':') >= 0 && a.IndexOf(':') == a.LastIndexOf(':'))
        {
            a = a.Substring(0, a.IndexOf(':'));
        }
        if (string.Equals(a, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        System.Net.IPAddress ip;
        if (!System.Net.IPAddress.TryParse(a, out ip)) return false;
        // ::ffff:127.0.0.1 is IPv4 loopback written as IPv6; IsLoopback says false for it on .NET Framework.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(ip);
    }
}

// The file a setting lives in, read again whenever its modification time changes. Thread-safe; no System.Web.
public sealed class GeckoGraphFileCache
{
    readonly Func<string, DateTime?> modified;
    readonly Func<string, string> read;
    readonly Dictionary<string, KeyValuePair<DateTime, string>> files =
        new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new object();

    public GeckoGraphFileCache(Func<string, DateTime?> modified, Func<string, string> read)
    {
        this.modified = modified;
        this.read = read;
    }

    // The file's text, or null when it does not exist or cannot be read, or the platform refuses its path (bad characters,
    // a bad format): never an error.
    public string Text(string path)
    {
        DateTime? stamp;
        try { stamp = modified(path); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; }
        if (stamp == null) return null;
        lock (gate)
        {
            KeyValuePair<DateTime, string> cached;
            if (files.TryGetValue(path, out cached) && cached.Key == stamp.Value) return cached.Value;
            string text;
            try { text = read(path); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; }
            files[path] = new KeyValuePair<DateTime, string>(stamp.Value, text);
            return text;
        }
    }
}

// Pure: the relay's reads of Web.config and the appSettings file it names.
public static class GeckoGraphLocalSettings
{
    // A setting from the file Web.config names in <appSettings file="...">, resolved beside Web.config; null when there
    // is no such file, or it does not hold the setting.
    public static string FromAppSettingsFile(GeckoGraphFileCache files, string webConfigPath, string key)
    {
        var file = AppSettingsFile(files.Text(webConfigPath));
        if (file == null) return null;
        string path;
        try
        {
            var dir = Path.GetDirectoryName(webConfigPath) ?? "";
            path = Path.GetFullPath(Path.Combine(dir, file));
        }
        // A file attribute that is no path (invalid characters, a bad format): nothing from the file, so the caller falls back
        // to Web.config's own setting instead of failing the request.
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
        return AppSetting(files.Text(path), key);
    }

    // The file attribute of Web.config's <configuration><appSettings>, or null.
    public static string AppSettingsFile(string webConfigXml)
    {
        var root = Load(webConfigXml);
        if (root == null || root.Name != "configuration") return null;
        var section = root["appSettings"];
        var file = section == null ? null : section.GetAttribute("file");
        return string.IsNullOrWhiteSpace(file) ? null : file.Trim();
    }

    // The value of <add key="..." value="..." /> in an appSettings document (an appSettings file's root, or a Web.config's
    // <appSettings> section), the last one winning as in ASP.NET, or null. A <remove> or <clear /> after it removes it.
    public static string AppSetting(string xml, string key)
    {
        var root = Load(xml);
        if (root == null) return null;
        var section = root.Name == "appSettings" ? root : root["appSettings"];
        if (section == null) return null;
        string value = null;
        foreach (XmlNode node in section.ChildNodes)
        {
            var element = node as XmlElement;
            if (element == null) continue;
            if (element.Name == "clear") value = null;
            else if (element.Name == "remove" && element.GetAttribute("key") == key) value = null;
            else if (element.Name == "add" && element.GetAttribute("key") == key) value = element.GetAttribute("value");
        }
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static XmlElement Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings)) doc.Load(reader);
            return doc.DocumentElement;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

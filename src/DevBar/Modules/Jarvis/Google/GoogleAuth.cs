using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Google;

/// <summary>
/// Google sign-in for Calendar + Gmail using the user's own OAuth "Desktop app"
/// client (so no third party sits in between). Standard installed-app flow:
/// PKCE, browser consent, a one-shot loopback listener on 127.0.0.1, then a
/// refresh token kept DPAPI-encrypted in SecretStore. Access tokens live in
/// memory only and are refreshed on demand.
/// </summary>
internal static class GoogleAuth
{
    public const string ClientIdKey = "google_client_id";
    public const string ClientSecretKey = "google_client_secret";
    private const string RefreshKey = "google_refresh_token";
    private const string AccountKey = "google_account";

    // calendar.events: read + create events. gmail.readonly: search/read.
    // gmail.compose: create drafts and send (sending is always confirmed by voice/click).
    private static readonly string[] Scopes =
    {
        "openid", "email",
        "https://www.googleapis.com/auth/calendar.events",
        "https://www.googleapis.com/auth/gmail.readonly",
        "https://www.googleapis.com/auth/gmail.compose",
    };

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string? _accessToken;
    private static DateTime _accessExpires;
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public static bool HasClient => SecretStore.Has(ClientIdKey) && SecretStore.Has(ClientSecretKey);
    public static bool IsConnected => SecretStore.Has(RefreshKey);
    public static string? Account => SecretStore.Get(AccountKey);

    /// <summary>Opens the browser for consent and waits (up to 3 minutes) for Google to redirect back.</summary>
    public static async Task<string> ConnectAsync(CancellationToken ct)
    {
        var clientId = SecretStore.Get(ClientIdKey) ?? throw new InvalidOperationException("Add the Google client ID first.");
        var clientSecret = SecretStore.Get(ClientSecretKey) ?? throw new InvalidOperationException("Add the Google client secret first.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        int port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var url = "https://accounts.google.com/o/oauth2/v2/auth"
                  + $"?client_id={Uri.EscapeDataString(clientId)}"
                  + $"&redirect_uri={Uri.EscapeDataString(redirect)}"
                  + "&response_type=code"
                  + $"&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}"
                  + $"&code_challenge={challenge}&code_challenge_method=S256"
                  + $"&state={state}&access_type=offline&prompt=consent";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ctxTask = listener.GetContextAsync();
        if (await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, timeout.Token)) != ctxTask)
            throw new TimeoutException("Sign-in wasn't completed in the browser.");
        var ctx = await ctxTask;

        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
        bool ok = q["state"] == state && q["code"] is { Length: > 0 };
        var page = ok
            ? "<html><body style='font-family:Segoe UI;background:#0B0E14;color:#DCE3EC;padding:40px'><h2>Jarvis is connected to Google.</h2><p>You can close this tab.</p></body></html>"
            : $"<html><body style='font-family:Segoe UI;padding:40px'><h2>Sign-in failed</h2><p>{WebUtility.HtmlEncode(q["error"] ?? "state mismatch")}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException("Google sign-in failed: " + (q["error"] ?? "state mismatch"));

        using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = q["code"]!,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        }), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Token exchange failed: " + json);

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("refresh_token", out var refresh))
            throw new InvalidOperationException("Google didn't return a refresh token — remove Jarvis's access at myaccount.google.com/permissions and connect again.");
        SecretStore.Set(RefreshKey, refresh.GetString());
        _accessToken = r.GetProperty("access_token").GetString();
        _accessExpires = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32() - 60);

        var email = EmailFromIdToken(r.TryGetProperty("id_token", out var idt) ? idt.GetString() : null);
        if (email != null) SecretStore.Set(AccountKey, email);
        return email ?? "your Google account";
    }

    public static void Disconnect()
    {
        var refresh = SecretStore.Get(RefreshKey);
        if (refresh != null)
            _ = Http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }));
        SecretStore.Set(RefreshKey, null);
        SecretStore.Set(AccountKey, null);
        _accessToken = null;
    }

    /// <summary>A valid access token, refreshing if needed. Throws a speakable message when not connected.</summary>
    public static async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
        await RefreshGate.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
            var refresh = SecretStore.Get(RefreshKey) ?? throw new InvalidOperationException("Google isn't connected — connect it in Jarvis settings.");
            using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = SecretStore.Get(ClientIdKey) ?? "",
                ["client_secret"] = SecretStore.Get(ClientSecretKey) ?? "",
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            }), ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_grant"))
                {
                    SecretStore.Set(RefreshKey, null);
                    throw new InvalidOperationException("Google access expired or was revoked — reconnect it in Jarvis settings. (If your OAuth app is in 'Testing', Google expires access weekly; publish it to 'In production'.)");
                }
                throw new InvalidOperationException("Google token refresh failed: " + json);
            }
            using var doc = JsonDocument.Parse(json);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            return _accessToken!;
        }
        finally { RefreshGate.Release(); }
    }

    /// <summary>Authorized JSON call to a Google API; returns the parsed body.</summary>
    public static async Task<JsonDocument> CallAsync(HttpMethod method, string url, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = json.Contains("SERVICE_DISABLED") || json.Contains("has not been used in project")
                ? " — enable the Gmail API / Google Calendar API in your Google Cloud project." : "";
            throw new InvalidOperationException($"Google API {(int)resp.StatusCode}{hint}: {(json.Length > 240 ? json[..240] : json)}");
        }
        return JsonDocument.Parse(json.Length == 0 ? "{}" : json);
    }

    private static string? EmailFromIdToken(string? idToken)
    {
        try
        {
            var payload = idToken!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.GetProperty("email").GetString();
        }
        catch { return null; }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

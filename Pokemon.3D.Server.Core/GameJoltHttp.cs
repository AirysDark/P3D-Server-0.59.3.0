#pragma warning disable 1591
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Web.Script.Serialization; // add reference to System.Web.Extensions
using System.Linq;

namespace Pokemon_3D_Server_Core
{
    /*
        Website:
          GET  /                -> Home (links)
          GET  /register        -> registration form
          POST /register        -> create/update user (username + password + token)
          GET  /login           -> website login (username + password)
          POST /login           -> perform login (sets cookie with username+token)
          GET  /token           -> view/set token (user chooses their client token)
          POST /token           -> update token
          GET  /dashboard       -> simple dashboard (friends/trophies/scores counts)
          GET  /logout          -> clear cookie

        API (keypair/plain compatible):
          GET  /api/game/v1_1/
          GET  /api/game/v1_1/users/register?game_id=...&username=...&user_password=...&user_token=...[&user_id=...]
          GET  /api/game/v1_1/users/auth?game_id=...&username=...&user_token=...
          (plus Sessions, Data-store, Trophies, Scores, Friends stubs)
    */

    public static class GameJoltHttp
    {
        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;

        // must match your client?s expected values
        private const string RequiredGameId = "123";
        private const string GameKey = "local-test-key"; // reserved (not used here)

        // assets
        private static readonly string AssetsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
        private static readonly string TexturesDir = Path.Combine(AssetsRoot, "textures");
        private static readonly string EmblemsDir = Path.Combine(AssetsRoot, "emblems");
        private static readonly string AvatarsDir = Path.Combine(AssetsRoot, "avatars");

        // ===== data =====
        // Users: username -> (password for website, token for client, id)
        private static readonly Dictionary<string, (string password, string token, string id)> Users =
            new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                // example seed: username admin / password: adminpw / token: devtoken
                { "admin", ("adminpw", "devtoken", "12345") }
            };

        // Sessions: (username, token) -> lastSeenUtc
        private static readonly Dictionary<Tuple<string, string>, DateTime> Sessions =
            new Dictionary<Tuple<string, string>, DateTime>();

        // KV store (global / public for now)
        private static readonly Dictionary<string, string> KV = new Dictionary<string, string>();

        private static readonly Dictionary<int, (string title, string desc)> Trophies =
            new Dictionary<int, (string, string)>
            {
                { 1, ("Getting Started", "Logged in for the first time") },
                { 2, ("Explorer", "Played for 60 minutes") }
            };

        private static readonly Dictionary<string, HashSet<int>> AchievedByUser =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, List<(string user, int sort, string scoreText)>> ScoreTables =
            new Dictionary<string, List<(string, int, string)>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, HashSet<string>> Friends =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object UsersLock = new object();
        private static readonly object KvLock = new object();
        private static readonly object SessionsLock = new object();
        private static readonly object TrophiesLock = new object();
        private static readonly object ScoresLock = new object();
        private static readonly object FriendsLock = new object();

        public static int Port { get; private set; } = 8080;

        private static readonly string UsersFile = "users.json";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // ===== lifecycle =====
        public static void Start(int port = 8080)
        {
            if (_running) { Console.WriteLine("[GameJoltHttp] Already running."); return; }

            Port = port;
            LoadUsers();

            lock (KvLock)
                if (!KV.ContainsKey("ONLINEVERSION")) KV["ONLINEVERSION"] = "0.59.3.0";

            Directory.CreateDirectory(TexturesDir);
            Directory.CreateDirectory(EmblemsDir);
            Directory.CreateDirectory(AvatarsDir);

            _running = true;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Prefixes.Add($"http://+:{port}/api/game/v1_1/");
            try { _listener.Start(); }
            catch (HttpListenerException ex) { Console.WriteLine("[GameJoltHttp] Start failed: " + ex.Message); _running = false; return; }

            _thread = new Thread(ListenLoop) { IsBackground = true };
            _thread.Start();
            Console.WriteLine($"[GameJoltHttp] Listening on http://localhost:{port}/");
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _thread = null;
            Console.WriteLine("[GameJoltHttp] Stopped.");
        }

        private static void ListenLoop()
        {
            while (_running)
            {
                try { var ctx = _listener.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(ctx)); }
                catch (HttpListenerException) { if (!_running) break; }
                catch (Exception ex) { Console.WriteLine("[GameJoltHttp] Listener error: " + ex.Message); }
            }
        }

        // ===== helpers: responses =====
        private static void AddCors(HttpListenerResponse res)
        {
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static void WriteHtml(HttpListenerContext ctx, string html, int status = 200)
        {
            AddCors(ctx.Response);
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void WriteJson(HttpListenerContext ctx, object obj, int status = 200)
        {
            AddCors(ctx.Response);
            var json = Json.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void WritePlain(HttpListenerContext ctx, IDictionary<string, string> pairs, int status = 200)
        {
            AddCors(ctx.Response);
            var sb = new StringBuilder();
            foreach (var kv in pairs) sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static Dictionary<string, string> ParseForm(HttpListenerRequest req)
        {
            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!req.HasEntityBody) return form;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
            {
                var body = sr.ReadToEnd();
                foreach (var part in body.Split('&'))
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    var kv = part.Split(new[] { '=' }, 2);
                    var key = WebUtility.UrlDecode(kv[0] ?? "");
                    var val = kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : "";
                    if (!string.IsNullOrEmpty(key)) form[key] = val;
                }
            }
            return form;
        }

        private static string NewId() => Guid.NewGuid().ToString("N");

        private static bool CheckGameId(NameValueCollection q, HttpListenerContext ctx)
        {
            var gid = q["game_id"];
            if (string.IsNullOrEmpty(gid) || !gid.Equals(RequiredGameId, StringComparison.Ordinal))
            {
                WriteJson(ctx, new { success = false, message = "Invalid game_id" }, 403);
                return false;
            }
            return true;
        }

        // ===== static files =====
        private static bool TryServeAsset(HttpListenerContext ctx, string path)
        {
            if (!path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)) return false;

            var rel = path.Replace('/', Path.DirectorySeparatorChar);
            if (rel.StartsWith(Path.DirectorySeparatorChar.ToString())) rel = rel.Substring(1);

            var full = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel));
            if (!full.StartsWith(AssetsRoot, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; ctx.Response.Close(); return true; }
            if (!File.Exists(full)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return true; }

            ServeFile(ctx, full);
            return true;
        }

        private static void ServeFile(HttpListenerContext ctx, string fullPath)
        {
            AddCors(ctx.Response);
            string ext = Path.GetExtension(fullPath)?.ToLowerInvariant();
            string mime =
                ext == ".png" ? "image/png" :
                ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" :
                ext == ".gif" ? "image/gif" :
                ext == ".webp" ? "image/webp" :
                ext == ".svg" ? "image/svg+xml" :
                ext == ".json" ? "application/json" :
                ext == ".txt" ? "text/plain; charset=utf-8" :
                "application/octet-stream";

            byte[] bytes = File.ReadAllBytes(fullPath);
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = bytes.LongLength;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // ===== website layout =====
        private static string HtmlLayout(string title, string body) => $@"<!doctype html>
<html>
<head>
<meta charset='utf-8'/>
<title>{WebUtility.HtmlEncode(title)}</title>
<style>
:root{{--bg:#0f1220;--panel:#1a1f35;--text:#e8ecff;--muted:#a9b1d6;--accent:#5b7cfa;--border:#2b335a}}
*{{box-sizing:border-box}} body{{margin:0;font-family:Segoe UI,Arial,sans-serif;background:var(--bg);color:var(--text)}}
a{{color:#a5b8ff}} .wrap{{max-width:720px;margin:5vh auto;padding:24px}}
.card{{background:#171b31;border:1px solid var(--border);border-radius:12px;padding:18px;margin:14px 0}}
input{{width:100%;padding:10px;border-radius:10px;border:1px solid var(--border);background:#0e1227;color:var(--text)}}
button{{padding:10px 14px;border:0;border-radius:10px;background:var(--accent);color:#fff;cursor:pointer}}
label{{font-size:12px;color:var(--muted)}}
.grid{{display:grid;grid-template-columns:1fr 1fr;gap:12px}}
.small{{color:var(--muted);font-size:12px}}
nav a{{margin-right:12px}}
pre{{white-space:pre-wrap;background:#0b0f22;padding:12px;border-radius:8px;border:1px solid var(--border)}}
</style>
</head>
<body>
<div class='wrap'>
  <nav>
    <a href='/'>Home</a>
    <a href='/register'>Register</a>
    <a href='/login'>Login</a>
    <a href='/dashboard'>Dashboard</a>
    <a href='/token'>Token</a>
    <a href='/logout'>Logout</a>
  </nav>
  {body}
  <p class='small'>API game_id = {RequiredGameId}</p>
</div>
</body>
</html>";

        private static void WriteRedirect(HttpListenerContext ctx, string to) { ctx.Response.Redirect(to); ctx.Response.Close(); }

        // ===== auth cookie =====
        private const string CookieName = "gj_auth";
        private static void SetAuthCookie(HttpListenerResponse res, string username, string token)
        {
            var cookie = new Cookie(CookieName, $"{username}|{token}")
            {
                HttpOnly = true,
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(30)
            };
            res.Cookies.Add(cookie);
        }
        private static void ClearAuthCookie(HttpListenerResponse res)
        {
            res.Cookies.Add(new Cookie(CookieName, "") { HttpOnly = true, Path = "/", Expires = DateTime.UtcNow.AddDays(-1) });
        }
        private static (string user, string token) GetAuthCookie(HttpListenerRequest req)
        {
            var c = req.Cookies[CookieName];
            if (c == null || string.IsNullOrWhiteSpace(c.Value)) return (null, null);
            var parts = c.Value.Split('|');
            return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
        }

        // self-call helpers (used by website dashboard)
        private static string BuildApi(string path, params (string k, string v)[] qs)
        {
            var baseUrl = $"http://127.0.0.1:{Port}/api/game/v1_1";
            var query = string.Join("&", qs.Select(p => p.k + "=" + Uri.EscapeDataString(p.v ?? "")));
            return $"{baseUrl}{path}?{query}";
        }
        private static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 8000;
            req.Proxy = null;
            req.ServicePoint.Expect100Continue = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }
        private static Dictionary<string, string> ParseKeypairs(string body)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var sr = new StringReader(body ?? ""))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var i = line.IndexOf(':');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (!string.IsNullOrEmpty(k)) dict[k] = v;
                }
            }
            return dict;
        }

        // ===== pages =====
        private static void HomePage(HttpListenerContext ctx)
        {
            var (u, _) = GetAuthCookie(ctx.Request);
            var note = u != null ? $"<p class='small'>Signed in as <b>{WebUtility.HtmlEncode(u)}</b></p>"
                                 : "<p class='small'>Not signed in.</p>";
            var body = $@"
<div class='card'>
  <h2>Welcome</h2>
  <p>Website + API for local GameJolt-style login.</p>
  {note}
  <p><a href='/register'>Register</a> · <a href='/login'>Login</a> · <a href='/dashboard'>Dashboard</a> · <a href='/token'>Token</a></p>
</div>";
            WriteHtml(ctx, HtmlLayout("Home", body));
        }

        private static void LoginForm(HttpListenerContext ctx)
        {
            // Website uses PASSWORD; client uses TOKEN.
            var body = @"
<div class='card'>
  <h2>Login (Website)</h2>
  <p class='small'>Use your <b>password</b> here. The game client uses your <b>token</b>.</p>
  <form method='POST' action='/login'>
    <label>Username</label>
    <input name='username' required>
    <label style='margin-top:8px;'>Password</label>
    <input name='password' type='password' required>
    <div style='margin-top:10px;'>
      <button type='submit'>Login</button>
    </div>
  </form>
</div>";
            WriteHtml(ctx, HtmlLayout("Login", body));
        }

        private static void DoLogin(HttpListenerContext ctx)
        {
            var form = ParseForm(ctx.Request);
            var username = (form.TryGetValue("username", out var uVal) ? uVal : string.Empty).Trim();
            var password = (form.TryGetValue("password", out var pVal) ? pVal : string.Empty).Trim();

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                WriteHtml(ctx, HtmlLayout("Login", "<div class='card'><p>Missing username or password.</p></div>"), 400);
                return;
            }

            // Validate password (website auth)
            string token = null;
            lock (UsersLock)
            {
                if (Users.TryGetValue(username, out var rec) && rec.password == password)
                {
                    token = rec.token ?? "";
                }
            }

            if (token == null)
            {
                WriteHtml(ctx, HtmlLayout("Login", "<div class='card'><p>Invalid username or password.</p></div>"), 401);
                return;
            }

            // Store username + TOKEN in cookie so dashboard/API calls work
            SetAuthCookie(ctx.Response, username, token);
            LogActivity($"Website login success: {username}", ctx.Request);
            WriteRedirect(ctx, "/dashboard");
        }

        private static void Dashboard(HttpListenerContext ctx)
        {
            var (u, t) = GetAuthCookie(ctx.Request);
            if (u == null) { WriteRedirect(ctx, "/login"); return; }

            try { _ = HttpGet(BuildApi("/sessions/open", ("game_id", RequiredGameId), ("username", u), ("user_token", t))); } catch { }

            string friendsBlock, trophiesBlock, scoresBlock;

            try
            {
                var resp = HttpGet(BuildApi("/friends", ("game_id", RequiredGameId), ("username", u), ("user_token", t), ("format", "keypair")));
                var p = ParseKeypairs(resp);
                var c = p.TryGetValue("count", out var cc) ? cc : "0";
                friendsBlock = $"<div class='card'><h3>Friends</h3><p>Friend count: <b>{c}</b></p></div>";
            }
            catch { friendsBlock = "<div class='card'><h3>Friends</h3><p>Unavailable.</p></div>"; }

            try
            {
                var resp = HttpGet(BuildApi("/trophies", ("game_id", RequiredGameId), ("username", u), ("user_token", t), ("format", "keypair")));
                var p = ParseKeypairs(resp);
                var c = p.TryGetValue("count", out var cc) ? cc : "0";
                trophiesBlock = $"<div class='card'><h3>Trophies</h3><p>Total visible: <b>{c}</b></p></div>";
            }
            catch { trophiesBlock = "<div class='card'><h3>Trophies</h3><p>Unavailable.</p></div>"; }

            try
            {
                var resp = HttpGet(BuildApi("/scores", ("game_id", RequiredGameId), ("table_id", "main"), ("limit", "10"), ("format", "keypair")));
                var p = ParseKeypairs(resp);
                var c = p.TryGetValue("count", out var cc) ? cc : "0";
                scoresBlock = $"<div class='card'><h3>Scores</h3><p>Entries: <b>{c}</b></p></div>";
            }
            catch { scoresBlock = "<div class='card'><h3>Scores</h3><p>Unavailable.</p></div>"; }

            var head = $@"
<div class='card'>
  <h2>Dashboard</h2>
  <p>Signed in as <b>{WebUtility.HtmlEncode(u)}</b></p>
  <div class='grid'>
    <div><a class='small' href='/logout'>Logout</a></div>
    <div class='small' style='text-align:right'>game_id: {RequiredGameId}</div>
  </div>
</div>";

            WriteHtml(ctx, HtmlLayout("Dashboard", head + friendsBlock + trophiesBlock + scoresBlock));
        }

        // ===== main handler =====
        private static void Handle(HttpListenerContext ctx)
        {
            string path = (ctx.Request.Url?.AbsolutePath ?? "").TrimEnd('/').ToLowerInvariant();
            var q = ctx.Request.QueryString;

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                AddCors(ctx.Response);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (TryServeAsset(ctx, path)) return;

            bool Need(params string[] names)
            {
                foreach (var n in names)
                {
                    if (string.IsNullOrEmpty(q[n]))
                    {
                        WriteJson(ctx, new { success = false, message = "Missing param: " + n }, 400);
                        return false;
                    }
                }
                return true;
            }

            try
            {
                switch (path)
                {
                    // ===== website =====
                    case "":
                    case "/":
                        HomePage(ctx); return;

                    case "/register":
                        if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                        {
                            var form = ParseForm(ctx.Request);
                            var user = form.TryGetValue("username", out var u) ? u?.Trim() : "";
                            var pass = form.TryGetValue("password", out var p) ? p?.Trim() : "";
                            var token = form.TryGetValue("token", out var t) ? t?.Trim() : "";

                            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass) || string.IsNullOrWhiteSpace(token))
                            {
                                WriteHtml(ctx, HtmlLayout("Register", "<div class='card'><p>Username, password, and token are required.</p></div>"), 400);
                                return;
                            }

                            RegisterUser(user, pass, token);
                            // NEW: ensure default save exists
                            if (TryGetUserId(user, out var uid))
                            {
                                try { if (EnsureSaveExists(uid, user)) LogActivity($"Default save created for {user} (uid={uid})"); } catch (Exception ex) { Console.WriteLine("[EnsureSaveExists(register web)] " + ex.Message); }
                            }

                            try { Pokemon_3D_Server_Core.GameJolt.GameJoltHttpServer.LogUserRegistered(user); } catch { }
                            WriteHtml(ctx, HtmlLayout("Register",
                                $"<div class='card'><h3>Account created!</h3><p>Username: <b>{WebUtility.HtmlEncode(user)}</b></p><p class='small'>Password = website login. Token = game login.</p></div>"));
                        }
                        else
                        {
                            const string html = @"<div class='card'>
  <h2>Create Account</h2>
  <p class='small'>Pick a username, a website password, and a <b>token</b> (the client uses this as its login credential).</p>
  <form method='POST' action='/register'>
    <label>Username</label>
    <input name='username' placeholder='Your name' required />
    <label style='margin-top:8px;'>Password (website)</label>
    <input name='password' type='password' placeholder='Your website password' required />
    <label style='margin-top:8px;'>User Token (client)</label>
    <input name='token' placeholder='Your chosen token (e.g. ""sonic"")' required />
    <button type='submit' style='margin-top:12px;'>Register</button>
  </form>
</div>";
                            WriteHtml(ctx, HtmlLayout("Register", html));
                        }
                        return;

                    case "/login":
                        if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)) { DoLogin(ctx); return; }
                        LoginForm(ctx); return;

                    case "/logout":
                        ClearAuthCookie(ctx.Response); WriteRedirect(ctx, "/"); return;

                    case "/dashboard":
                        Dashboard(ctx); return;

                    // TOKEN page ? user provides their own token
                    case "/token":
                        {
                            var (u, t) = GetAuthCookie(ctx.Request);
                            if (u == null) { WriteRedirect(ctx, "/login"); return; }

                            string currentToken = null;
                            lock (UsersLock)
                                if (Users.TryGetValue(u, out var rec)) currentToken = rec.token;

                            if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                            {
                                var form = ParseForm(ctx.Request);
                                if (!form.TryGetValue("new_token", out var newTok) || string.IsNullOrWhiteSpace(newTok))
                                {
                                    WriteHtml(ctx, HtmlLayout("Token", "<div class='card'><p>No token entered.</p></div>"), 400);
                                    return;
                                }

                                newTok = newTok.Trim();
                                if (newTok.Length < 3 || newTok.Length > 128)
                                {
                                    WriteHtml(ctx, HtmlLayout("Token", "<div class='card'><p>Token must be between 3 and 128 characters.</p></div>"), 400);
                                    return;
                                }

                                string oldToken = null;
                                lock (UsersLock)
                                {
                                    if (Users.TryGetValue(u, out var rec))
                                    {
                                        oldToken = rec.token;
                                        Users[u] = (rec.password, newTok, rec.id);
                                    }
                                }

                                SaveUsers();

                                // invalidate any sessions using the old token
                                if (!string.IsNullOrEmpty(oldToken) && !string.Equals(oldToken, newTok, StringComparison.Ordinal))
                                {
                                    RemoveAllSessionsForUser(u);
                                }

                                // refresh cookie to keep them signed in with the new token
                                SetAuthCookie(ctx.Response, u, newTok);

                                currentToken = newTok;
                            }

                            // GET ? show token with show/hide + copy
                            var safeTok = WebUtility.HtmlEncode(currentToken ?? "");
                            var body = $@"
<div class='card'>
  <h2>Token Management</h2>
  <p>Signed in as <b>{WebUtility.HtmlEncode(u)}</b></p>
  <h3>Your current token</h3>
  <div style='display:grid;grid-template-columns:1fr auto auto;gap:8px;align-items:center;'>
    <input id='tok' type='password' value='{safeTok}' readonly style='padding:10px;border-radius:10px;border:1px solid var(--border);background:#0e1227;color:var(--text);width:100%;'/>
    <button id='toggle'>Show</button>
    <button id='copy'>Copy</button>
  </div>
  <p class='small'>Use this token in the game client (not your password).</p>
</div>
<div class='card'>
  <h3>Set a new token</h3>
  <form method='POST' action='/token'>
    <label>New Token</label>
    <input name='new_token' required />
    <button type='submit' style='margin-top:10px;'>Save Token</button>
  </form>
</div>
<script>
(function(){{
  var tok=document.getElementById('tok');
  var toggle=document.getElementById('toggle');
  var copy=document.getElementById('copy');
  toggle.onclick=function(e){{e.preventDefault();if(tok.type==='password'){{tok.type='text';toggle.textContent='Hide';}}else{{tok.type='password';toggle.textContent='Show';}}}};
  copy.onclick=async function(e){{e.preventDefault();var was=tok.type;tok.type='text';tok.select();try{{await navigator.clipboard.writeText(tok.value);copy.textContent='Copied!';setTimeout(function(){{copy.textContent='Copy';}},1200);}}catch(err){{document.execCommand('copy');}}finally{{tok.type=was;}}}};
}})();
</script>";
                            WriteHtml(ctx, HtmlLayout("Token", body));
                            return;
                        }

                    case "/health": WriteJson(ctx, new { ok = true }); return;

                    case "/users":
                        lock (UsersLock)
                        {
                            var dump = new Dictionary<string, object>();
                            foreach (var kv in Users) dump[kv.Key] = new { password = kv.Value.password, token = kv.Value.token, id = kv.Value.id };
                            WriteJson(ctx, dump);
                        }
                        return;

                    // ===== API base =====
                    case "/api/game/v1_1":
                        WriteJson(ctx, new { ok = true, version = "v1_1", message = "API online" }); return;

                    // ===== API: Users/Auth =====
                    case "/api/game/v1_1/users/register":
                        if (!Need("game_id", "username", "user_password", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        RegisterUser(q["username"], q["user_password"], q["user_token"], q["user_id"]);

                        // NEW: ensure default save exists (API register)
                        if (TryGetUserId(q["username"], out var regUid))
                        {
                            try { if (EnsureSaveExists(regUid, q["username"])) LogActivity($"Default save created for {q["username"]} (uid={regUid})", ctx.Request); } catch (Exception ex) { Console.WriteLine("[EnsureSaveExists(register api)] " + ex.Message); }
                        }

                        LogActivity($"New user registered (API): {q["username"]}", ctx.Request);
                        WriteJson(ctx, new { success = true, message = "Registered", username = q["username"], user_id = Users[q["username"]].id });
                        return;

                    case "/api/game/v1_1/users/auth":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out var uidAuth))
                        {
                            LogActivity($"User token auth failed: {q["username"]}", ctx.Request);
                            WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Invalid username or token" } }, 200);
                            return;
                        }
                        LogActivity($"User token auth success: {q["username"]}", ctx.Request);
                        WritePlain(ctx, new Dictionary<string, string> {
                            { "success","true" }, { "username", q["username"] }, { "user_token", q["user_token"] }, { "user_id", uidAuth }
                        });
                        return;

                    case "/api/game/v1_1/users":
                        if (!Need("game_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        string resUser = null, resId = null;
                        lock (UsersLock)
                        {
                            if (!string.IsNullOrEmpty(q["username"]))
                            {
                                if (Users.TryGetValue(q["username"], out var rec)) { resUser = q["username"]; resId = rec.id; }
                            }
                            else if (!string.IsNullOrEmpty(q["user_id"]))
                            {
                                foreach (var kv in Users)
                                    if (string.Equals(kv.Value.id, q["user_id"], StringComparison.Ordinal)) { resUser = kv.Key; resId = kv.Value.id; break; }
                            }
                        }
                        if (resUser == null) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "User not found" } }, 404); return; }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "username", resUser }, { "id", resId } }); return;

                    // ===== Sessions =====
                    case "/api/game/v1_1/sessions/open":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        AddSession(q["username"], q["user_token"]);

                        // NEW: safety net ? ensure a default save exists for this user
                        if (AuthUserByToken(q["username"], q["user_token"], out var sessUid))
                        {
                            try { if (EnsureSaveExists(sessUid, q["username"])) LogActivity($"Default save created on session for {q["username"]} (uid={sessUid})", ctx.Request); } catch (Exception ex) { Console.WriteLine("[EnsureSaveExists(session open)] " + ex.Message); }
                        }

                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Session opened" } }); return;

                    case "/api/game/v1_1/sessions/ping":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!PingSession(q["username"], q["user_token"])) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "No active session" } }, 404); return; }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Pong" } }); return;

                    case "/api/game/v1_1/sessions/close":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        RemoveSession(q["username"], q["user_token"]);
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Session closed" } }); return;

                    // ===== Data-store =====
                    case "/api/game/v1_1/data-store/get":
                        if (!Need("game_id", "key")) return;
                        if (!CheckGameId(q, ctx)) return;
                        lock (KvLock)
                        {
                            if (!KV.TryGetValue(q["key"], out var val))
                            { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Key not found" } }, 404); return; }
                            WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "key", q["key"] }, { "value", val } });
                        }
                        return;

                    case "/api/game/v1_1/data-store/set":
                        if (!Need("game_id", "key", "data")) return;
                        if (!CheckGameId(q, ctx)) return;
                        lock (KvLock) KV[q["key"]] = q["data"];
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Stored" }, { "key", q["key"] } }); return;

                    case "/api/game/v1_1/data-store/update":
                        if (!Need("game_id", "key", "value")) return;
                        if (!CheckGameId(q, ctx)) return;
                        lock (KvLock) KV[q["key"]] = q["value"];
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Updated" }, { "key", q["key"] } }); return;

                    case "/api/game/v1_1/data-store/get-keys":
                        if (!Need("game_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        lock (KvLock)
                        {
                            WritePlain(ctx, new Dictionary<string, string> {
                                { "success","true" }, { "message","Stub get-keys" }, { "count", KV.Count.ToString() }
                            });
                        }
                        return;

                    case "/api/game/v1_1/data-store/remove":
                        if (!Need("game_id", "key")) return;
                        if (!CheckGameId(q, ctx)) return;
                        lock (KvLock) KV.Remove(q["key"]);
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Removed" }, { "key", q["key"] } }); return;

                    // ===== Trophies =====
                    case "/api/game/v1_1/trophies":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        int countOut = 0;
                        lock (TrophiesLock)
                        {
                            if (!string.IsNullOrEmpty(q["trophy_id"]))
                            {
                                if (int.TryParse(q["trophy_id"], out var tid) && Trophies.ContainsKey(tid)) countOut = 1;
                            }
                            else if (string.Equals(q["achieved"], "true", StringComparison.OrdinalIgnoreCase))
                            {
                                if (AchievedByUser.TryGetValue(q["username"], out var set)) countOut = set.Count;
                            }
                            else countOut = Trophies.Count;
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Trophies stub" }, { "count", countOut.ToString() } }); return;

                    case "/api/game/v1_1/trophies/add-achieved":
                        if (!Need("game_id", "username", "user_token", "trophy_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        if (!int.TryParse(q["trophy_id"], out var addTid)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Invalid trophy_id" } }, 400); return; }
                        lock (TrophiesLock)
                        {
                            if (!Trophies.ContainsKey(addTid)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Unknown trophy" } }, 404); return; }
                            if (!AchievedByUser.TryGetValue(q["username"], out var set)) AchievedByUser[q["username"]] = set = new HashSet<int>();
                            set.Add(addTid);
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Trophy marked achieved" } }); return;

                    case "/api/game/v1_1/trophies/remove-achieved":
                        if (!Need("game_id", "username", "user_token", "trophy_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        if (!int.TryParse(q["trophy_id"], out var remTid)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Invalid trophy_id" } }, 400); return; }
                        lock (TrophiesLock)
                        {
                            if (AchievedByUser.TryGetValue(q["username"], out var set)) set.Remove(remTid);
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Trophy unmarked" } }); return;

                    // ===== Scores =====
                    case "/api/game/v1_1/scores":
                        if (!Need("game_id", "table_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        int limit; int.TryParse(q["limit"], out limit); if (limit <= 0) limit = 10;
                        int actual = 0;
                        lock (ScoresLock)
                            if (ScoreTables.TryGetValue(q["table_id"], out var list)) actual = Math.Min(limit, list.Count);
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Scores stub" }, { "count", actual.ToString() } }); return;

                    case "/api/game/v1_1/scores/get-rank":
                        if (!Need("game_id", "table_id")) return;
                        if (!CheckGameId(q, ctx)) return;
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Rank stub" }, { "rank", "1" } }); return;

                    case "/api/game/v1_1/scores/add":
                        if (!Need("game_id", "username", "user_token", "table_id", "score", "sort")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        if (!int.TryParse(q["sort"], out var sortVal)) sortVal = 0;
                        lock (ScoresLock)
                        {
                            if (!ScoreTables.TryGetValue(q["table_id"], out var list)) ScoreTables[q["table_id"]] = list = new List<(string, int, string)>();
                            list.Add((q["username"], sortVal, q["score"] ?? ""));
                            list.Sort((a, b) => b.sort.CompareTo(a.sort));
                            if (list.Count > 100) list.RemoveRange(100, list.Count - 100);
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Score added" } }); return;

                    // ===== Friends =====
                    case "/api/game/v1_1/friends":
                        if (!Need("game_id", "username", "user_token")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        int friendCount = 0;
                        lock (FriendsLock)
                        {
                            if (!Friends.TryGetValue(q["username"], out var set)) Friends[q["username"]] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            friendCount = set.Count;
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Friends stub" }, { "count", friendCount.ToString() } }); return;

                    case "/api/game/v1_1/friends/add":
                        if (!Need("game_id", "username", "user_token", "friend_username")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        var me = q["username"]; var other = q["friend_username"];
                        lock (UsersLock)
                            if (!Users.ContainsKey(other)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Unknown friend_username" } }, 404); return; }
                        lock (FriendsLock)
                        {
                            if (!Friends.TryGetValue(me, out var mySet)) Friends[me] = mySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            mySet.Add(other);
                            if (!Friends.TryGetValue(other, out var otherSet)) Friends[other] = otherSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            otherSet.Add(me);
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Friend added" }, { "friend_username", other } }); return;

                    case "/api/game/v1_1/friends/remove":
                        if (!Need("game_id", "username", "user_token", "friend_username")) return;
                        if (!CheckGameId(q, ctx)) return;
                        if (!AuthUserByToken(q["username"], q["user_token"], out _)) { WritePlain(ctx, new Dictionary<string, string> { { "success", "false" }, { "message", "Auth required" } }, 403); return; }
                        me = q["username"]; other = q["friend_username"];
                        lock (FriendsLock)
                        {
                            if (Friends.TryGetValue(me, out var mySet)) mySet.Remove(other);
                            if (Friends.TryGetValue(other, out var otherSet)) otherSet.Remove(me);
                        }
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Friend removed" }, { "friend_username", other } }); return;

                    // Batch
                    case "/api/game/v1_1/batch":
                        WritePlain(ctx, new Dictionary<string, string> { { "success", "true" }, { "message", "Batch stub" } }); return;

                    default:
                        ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
                }
            }
            catch (Exception ex)
            {
                try { WriteJson(ctx, new { success = false, error = ex.Message }, 500); } catch { }
            }
        }

        // ===== logging helper =====
        private static void LogActivity(string message, HttpListenerRequest req = null)
        {
            try
            {
                var ip = req?.RemoteEndPoint?.Address?.ToString();
                if (!string.IsNullOrWhiteSpace(ip)) message = $"{message} | ip={ip}";
                try
                {
                    var t = Pokemon_3D_Server_Core.Server_Client_Listener.Loggers.Logger.LogTypes.Info;
                    Pokemon_3D_Server_Core.Core.Logger?.Log(message, t);
                }
                catch { Console.WriteLine("[Activity] " + message); }
            }
            catch { }
        }

        // ===== data helpers =====
        private static void RegisterUser(string username, string password, string token, string suppliedId = null)
        {
            lock (UsersLock)
            {
                if (Users.TryGetValue(username, out var rec))
                {
                    Users[username] = (password, token, rec.id);
                }
                else
                {
                    Users[username] = (password, token, suppliedId ?? NewId());
                }
                SaveUsers();
            }
        }

        private static bool AuthUserByToken(string username, string token, out string id)
        {
            lock (UsersLock)
            {
                if (Users.TryGetValue(username, out var rec) && rec.token == token)
                { id = rec.id; return true; }
            }
            id = null; return false;
        }

        private static bool TryGetUserId(string username, out string id)
        {
            lock (UsersLock)
            {
                if (Users.TryGetValue(username, out var rec)) { id = rec.id; return true; }
            }
            id = null; return false;
        }

        private static void AddSession(string username, string token)
        {
            var key = Tuple.Create(username, token);
            lock (SessionsLock) Sessions[key] = DateTime.UtcNow;
        }
        private static bool PingSession(string username, string token)
        {
            var key = Tuple.Create(username, token);
            lock (SessionsLock)
            {
                if (!Sessions.ContainsKey(key)) return false;
                Sessions[key] = DateTime.UtcNow; return true;
            }
        }
        private static void RemoveSession(string username, string token)
        {
            var key = Tuple.Create(username, token);
            lock (SessionsLock) Sessions.Remove(key);
        }

        // remove all sessions for a user (used when token changes)
        private static void RemoveAllSessionsForUser(string username)
        {
            lock (SessionsLock)
            {
                var toRemove = Sessions.Keys.Where(k => string.Equals(k.Item1, username, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in toRemove) Sessions.Remove(k);
            }
        }

        private static void SaveUsers()
        {
            try
            {
                var dict = new Dictionary<string, object>();
                lock (UsersLock)
                {
                    foreach (var kv in Users)
                        dict[kv.Key] = new { password = kv.Value.password, token = kv.Value.token, id = kv.Value.id };
                }
                File.WriteAllText(UsersFile, Json.Serialize(dict));
            }
            catch (Exception ex) { Console.WriteLine("[GameJoltHttp] Save error: " + ex.Message); }
        }

        private static void LoadUsers()
        {
            try
            {
                if (!File.Exists(UsersFile)) return;
                var data = Json.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(UsersFile));
                lock (UsersLock)
                {
                    Users.Clear();
                    foreach (var kv in data)
                    {
                        var pw = kv.Value.ContainsKey("password") ? kv.Value["password"] : "";
                        var tk = kv.Value.ContainsKey("token") ? kv.Value["token"] : "";
                        var id = kv.Value.ContainsKey("id") ? kv.Value["id"] : NewId();
                        Users[kv.Key] = (pw, tk, id);
                    }
                }
                Console.WriteLine($"[GameJoltHttp] Loaded {Users.Count} users.");
            }
            catch (Exception ex) { Console.WriteLine("[GameJoltHttp] Load error: " + ex.Message); }
        }

        // ===== NEW: default save creation =====
        private static bool EnsureSaveExists(string userId, string username)
        {
            var key = $"SAVE:{userId}";
            lock (KvLock)
            {
                if (KV.ContainsKey(key)) return false; // already exists
                KV[key] = Json.Serialize(DefaultSave(userId, username));
                return true;
            }
        }

        private static object DefaultSave(string userId, string username)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new
            {
                version = "1",
                user_id = userId,
                created_utc = now,
                last_update_utc = now,
                player = new
                {
                    name = string.IsNullOrWhiteSpace(username) ? "New Trainer" : username,
                    money = 3000,
                    position = new { map = "newb_town.map", x = 10, y = 8, dir = "Down" },
                    badges = new int[] { },
                    inventory = new object[] { },
                    party = new object[] { },
                    flags = new { tutorial_seen = false }
                },
                net = new { rank = 0, wins = 0, losses = 0 }
            };
        }
        // ===============================
    }
}
#pragma warning restore 1591
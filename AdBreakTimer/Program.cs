// ============================================================
// Ad Break Timer — Lightweight Overlay Web Server
//
// Hosts two OBS browser-source overlays straight out of the exe
// (no external files needed) and drives them with simple URL
// commands, built for Streamer.bot.
//
//   http://localhost:<port>/bar/      — bottom progress bar
//   http://localhost:<port>/radial/   — circular progress ring
//
// See config/README.txt (created next to the exe on first run)
// for the full command list.
//
// Made by Kaydee.Codes (https://kaydee.codes/)
// Free to use, no data collected, ever.
// ============================================================

using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Paths ─────────────────────────────────────────────────────────────────

string exeDir      = AppContext.BaseDirectory;
string configDir    = Path.Combine(exeDir, "config");
string settingsFile = Path.Combine(configDir, "settings.json");
string barFile      = Path.Combine(configDir, "bar.json");
string radialFile   = Path.Combine(configDir, "radial.json");
string readmeFile   = Path.Combine(configDir, "README.txt");

Directory.CreateDirectory(configDir);
bool firstRun = !File.Exists(settingsFile);

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};

// ── Settings (port) ──────────────────────────────────────────────────────

AppSettings settings = LoadJson<AppSettings>(settingsFile, jsonOpts) ?? new AppSettings();
settings.DebugLevel = Math.Clamp(settings.DebugLevel, 1, 3);

// Optional one-off override: AdBreakTimer.exe --debug 3
// (does NOT get saved to settings.json — it's just for this run)
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--debug" || args[i] == "-d") && i + 1 < args.Length && int.TryParse(args[i + 1], out int argLevel))
        settings.DebugLevel = Math.Clamp(argLevel, 1, 3);
}

if (firstRun)
{
    File.WriteAllText(readmeFile, ReadmeText.Content);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("First run — created a 'config' folder next to this exe.");
    Console.WriteLine($"  Full setup + command guide: {readmeFile}");
    Console.ResetColor();
    Console.WriteLine();
}

// ── Find a free port, starting from the saved/default one ──────────────────

var (listener, boundPort) = StartListenerAutoPort(settings.Port);
if (boundPort != settings.Port)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[PORT] {settings.Port} was unavailable — using {boundPort} instead.");
    Console.WriteLine("       Update your OBS Browser Source URLs to match.");
    Console.ResetColor();
    settings.Port = boundPort;
    SaveJson(settings, settingsFile, jsonOpts);
}

string baseUrl = $"http://localhost:{boundPort}";

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("========================================================");
Console.WriteLine("   Ad Break Timer — running");
Console.WriteLine("========================================================");
Console.ResetColor();
Console.WriteLine($"  Bar overlay     : {baseUrl}/bar/");
Console.WriteLine($"  Radial overlay  : {baseUrl}/radial/");
Console.WriteLine();
Console.WriteLine($"  Bar API         : {baseUrl}/bar/api?cmd=...");
Console.WriteLine($"  Radial API      : {baseUrl}/radial/api?cmd=...");
Console.WriteLine();
Console.WriteLine($"  Config folder   : {configDir}");
Console.WriteLine($"  Debug level     : {settings.DebugLevel}  (1=normal, 2=full diagnostics, 3=everything incl. polling)");
Console.WriteLine($"                    change anytime: {baseUrl}/debug/set?level=1|2|3");
Console.WriteLine("  Press Ctrl+C to stop.");
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("  Made by Kaydee.Codes (https://kaydee.codes/) - Free to use, no data collected, ever.");
Console.ResetColor();
Console.WriteLine();

// ── Request loop ─────────────────────────────────────────────────────────

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = HandleRequest(ctx);
}

async Task HandleRequest(HttpListenerContext ctx)
{
    var req  = ctx.Request;
    var res  = ctx.Response;
    string path = req.Url?.AbsolutePath ?? "/";

    // Level 3 only: literally every request, including the status polling.
    Log("[HTTP]", $"{req.HttpMethod} {path}{req.Url?.Query}", ConsoleColor.DarkGray, 3);

    try
    {
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");

        if (path == "/" || path == "")
        {
            await SendText(res, 200, "text/html; charset=utf-8", IndexHtml(baseUrl));
            return;
        }

        if (path.Equals("/debug/set", StringComparison.OrdinalIgnoreCase))
        {
            string lvlStr = req.QueryString["level"] ?? "";
            if (int.TryParse(lvlStr, out int lvl) && lvl is >= 1 and <= 3)
            {
                settings.DebugLevel = lvl;
                SaveJson(settings, settingsFile, jsonOpts);
                Log("[DEBUG]", $"Debug level changed to {lvl}", ConsoleColor.Cyan, 1);
                await SendText(res, 200, "text/plain", $"Debug level set to {lvl}");
            }
            else
            {
                await SendText(res, 400, "text/plain", "Use /debug/set?level=1, 2, or 3");
            }
            return;
        }

        if (path.StartsWith("/bar/api", StringComparison.OrdinalIgnoreCase))
        {
            string result = HandleBarCommand(ParseCommand(path, "/bar/api", req), barFile, jsonOpts);
            await SendJson(res, result);
            return;
        }

        if (path.StartsWith("/radial/api", StringComparison.OrdinalIgnoreCase))
        {
            string result = HandleRadialCommand(ParseCommand(path, "/radial/api", req), radialFile, jsonOpts);
            await SendJson(res, result);
            return;
        }

        if (path == "/bar" || path == "/bar/")
        {
            Log("[BAR]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan, 1);
            await SendEmbedded(res, "bar.html");
            return;
        }

        if (path == "/radial" || path == "/radial/")
        {
            Log("[RADIAL]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan, 1);
            await SendEmbedded(res, "radial.html");
            return;
        }

        await SendText(res, 404, "text/plain", $"404 Not Found: {path}");
    }
    catch (Exception ex)
    {
        // Level 1: short message. Level 2+: full exception detail including stack trace.
        string detail = settings.DebugLevel >= 2 ? ex.ToString() : ex.Message;
        Log("[ERROR]", detail, ConsoleColor.Red, 1);
        try { await SendText(res, 500, "text/plain", ex.Message); } catch { }
    }
}

// ── Console logging ───────────────────────────────────────────────────────
// level: 1 = normal (default), 2 = full diagnostics, 3 = everything incl. polling.
// Only prints if settings.DebugLevel is at least that level.

void Log(string tag, string message, ConsoleColor color, int level = 1)
{
    if (settings.DebugLevel < level) return;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = color;
    Console.Write($"{tag} ");
    Console.ResetColor();
    Console.WriteLine(message);
}

// ── Command parsing (shared by both overlays) ───────────────────────────────
// Supports both:
//   ?cmd=go&t=01:00:00&color=%2300ff00
//   /go_01:00:00   (path-info style, single value only)

static (string cmd, NameValueCollection qs) ParseCommand(string path, string apiPrefix, HttpListenerRequest req)
{
    var qs = req.QueryString;
    string cmd = (qs["cmd"] ?? "").ToLowerInvariant().Trim();

    if (string.IsNullOrEmpty(cmd) && path.Length > apiPrefix.Length)
    {
        string segment = path[apiPrefix.Length..].TrimStart('/');
        if (!string.IsNullOrEmpty(segment))
        {
            int sep = segment.IndexOf('_');
            cmd = (sep >= 0 ? segment[..sep] : segment).ToLowerInvariant();
        }
    }
    return (cmd, qs);
}

// ── Port binding with auto-increment ────────────────────────────────────────

static (HttpListener, int) StartListenerAutoPort(int startPort)
{
    int port = startPort > 0 ? startPort : 8085;
    for (int attempt = 0; attempt < 50; attempt++)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
            return (listener, port);
        }
        catch (HttpListenerException)
        {
            listener.Close();
            port++;
        }
    }
    throw new Exception("Could not find a free port after 50 attempts.");
}

// ── JSON file helpers ────────────────────────────────────────────────────────

static T? LoadJson<T>(string file, JsonSerializerOptions opts) where T : class
{
    if (!File.Exists(file)) return null;
    try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), opts); }
    catch { return null; }
}

static void SaveJson<T>(T obj, string file, JsonSerializerOptions opts)
{
    string? dir = Path.GetDirectoryName(file);
    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(file, JsonSerializer.Serialize(obj, opts));
}

// ── HTTP helpers ──────────────────────────────────────────────────────────

static async Task SendJson(HttpListenerResponse res, string json)
{
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    res.StatusCode = 200;
    res.ContentType = "application/json; charset=utf-8";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

static async Task SendText(HttpListenerResponse res, int code, string mime, string body)
{
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    res.StatusCode = code;
    res.ContentType = mime;
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

static async Task SendEmbedded(HttpListenerResponse res, string fileName)
{
    var asm = Assembly.GetExecutingAssembly();
    string resourceName = $"AdBreakTimer.Web.{fileName}";
    using var stream = asm.GetManifestResourceStream(resourceName);
    if (stream == null)
    {
        await SendText(res, 500, "text/plain", $"Embedded resource not found: {resourceName}");
        return;
    }
    using var reader = new StreamReader(stream);
    string html = await reader.ReadToEndAsync();
    await SendText(res, 200, "text/html; charset=utf-8", html);
}

static string IndexHtml(string baseUrl) => $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Ad Break Timer</title>
<style>body{font-family:sans-serif;background:#111;color:#eee;padding:2rem;}
a{color:#7dd3fc;} code{background:#222;padding:2px 6px;border-radius:4px;}</style>
</head><body>
<h2>Ad Break Timer is running</h2>
<p>Add these as OBS Browser Sources:</p>
<ul>
<li><a href="{{baseUrl}}/bar/">{{baseUrl}}/bar/</a></li>
<li><a href="{{baseUrl}}/radial/">{{baseUrl}}/radial/</a></li>
</ul>
<p>Control them with URL commands, e.g.<br>
<code>{{baseUrl}}/bar/api?cmd=go&amp;t=01:00:00&amp;color=%2300ff00&amp;dir=drain</code></p>
<p>Full command reference is in <code>config/README.txt</code> next to the exe.</p>
<hr style="border-color:#333;margin-top:2rem;">
<p style="color:#666;font-size:0.85rem;">Made by <a href="https://kaydee.codes/" style="color:#7dd3fc;">Kaydee.Codes</a> — free to use, no data collected, ever.</p>
</body></html>
""";

// ── Value parsing helpers ────────────────────────────────────────────────

static int HmsToSecs(string v)
{
    v = v.Trim();
    var parts = v.Split(':');
    if (parts.Length == 3) return Math.Abs(int.Parse(parts[0])) * 3600 + Math.Abs(int.Parse(parts[1])) * 60 + Math.Abs(int.Parse(parts[2]));
    if (parts.Length == 2) return Math.Abs(int.Parse(parts[0])) * 60 + Math.Abs(int.Parse(parts[1]));
    return int.TryParse(v, out int s) ? Math.Abs(s) : 0;
}

static string SecsToHms(int s)
{
    s = Math.Max(0, s);
    return $"{s / 3600:D2}:{s % 3600 / 60:D2}:{s % 60:D2}";
}

static string FormatQuery(NameValueCollection qs)
{
    if (qs.Count == 0) return "(none)";
    return string.Join("&", qs.AllKeys.Where(k => k != null).Select(k => $"{k}={qs[k]}"));
}

static string? ParseColor(string v)
{
    v = Uri.UnescapeDataString(v ?? "").Trim();
    if (v is "" or "null") return null;
    if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "transparent";
    if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^#[0-9a-fA-F]{3,8}$")) return v;
    if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^[a-zA-Z]{2,30}$")) return v;
    if (v.StartsWith("rgb(") || v.StartsWith("rgba(") || v.StartsWith("hsl(")) return v;
    return null;
}

static bool ParseBool(string v, bool fallback) => v?.ToLowerInvariant() switch
{
    "on" or "1" or "true" or "yes" => true,
    "off" or "0" or "false" or "no" => false,
    _ => fallback
};

// ── Shared timer tick logic ──────────────────────────────────────────────

static void Tick(OverlayState s)
{
    if (s.Status == "running" && s.LastTick != null)
    {
        int elapsed = (int)Math.Floor((DateTime.UtcNow - s.LastTick.Value).TotalSeconds);
        if (elapsed <= 0) return;
        s.Remaining -= elapsed;
        if (s.Remaining <= 0)
        {
            s.Remaining = 0;
            s.Status = "finished";
            s.LastTick = null;
            s.FinishedAt = DateTime.UtcNow;
        }
        else
        {
            s.LastTick = DateTime.UtcNow;
        }
        return;
    }

    // Once finished, flash for FlashDuration seconds, then quietly go
    // idle (bar/ring disappears) so it's never stuck lit up forever if
    // nothing sends a new command in time.
    if (s.Status == "finished" && s.FinishedAt != null)
    {
        double secsSinceFinish = (DateTime.UtcNow - s.FinishedAt.Value).TotalSeconds;
        if (secsSinceFinish >= s.FlashDuration)
        {
            s.Status = "idle";
            s.FinishedAt = null;
        }
    }
}

// Applies the commands common to both overlays. Returns true if handled.
static bool HandleCommon(string cmd, NameValueCollection qs, OverlayState s, out string? error)
{
    error = null;
    string v(string key) => qs[key] ?? "";

    switch (cmd)
    {
        case "go":
            {
                string t = v("t");
                if (string.IsNullOrEmpty(t)) { error = "go requires t= (e.g. t=01:00:00)"; return true; }
                int secs = HmsToSecs(t);
                if (secs <= 0) { error = "Time must be greater than zero."; return true; }

                if (!string.IsNullOrEmpty(v("color")))
                {
                    var c = ParseColor(v("color"));
                    if (c != null) s.Color = c;
                }
                if (!string.IsNullOrEmpty(v("finish")))
                {
                    var c = ParseColor(v("finish"));
                    if (c != null) s.FinishColor = c;
                }
                if (!string.IsNullOrEmpty(v("dir"))) s.Direction = v("dir").ToLowerInvariant();
                if (!string.IsNullOrEmpty(v("flash"))) s.FlashOnFinish = ParseBool(v("flash"), s.FlashOnFinish);
                if (!string.IsNullOrEmpty(v("flashfor")) && int.TryParse(v("flashfor"), out int fd) && fd >= 0) s.FlashDuration = fd;

                s.Remaining = secs;
                s.InitialTime = secs;
                s.Status = "running";
                s.LastTick = DateTime.UtcNow;
                s.FinishedAt = null;
                return true;
            }

        case "start":
            if (s.Remaining <= 0) { error = "No time set — use setTime or go first."; return true; }
            if (s.InitialTime <= 0) s.InitialTime = s.Remaining;
            s.Status = "running";
            s.LastTick = DateTime.UtcNow;
            s.FinishedAt = null;
            return true;

        case "pause":
            if (s.Status == "running") { s.Status = "paused"; s.LastTick = null; }
            return true;

        case "stop":
            s.Status = "idle"; s.Remaining = 0; s.LastTick = null; s.FinishedAt = null;
            return true;

        case "reset":
            s.Status = "idle"; s.Remaining = s.InitialTime; s.LastTick = null; s.FinishedAt = null;
            return true;

        case "settime":
            {
                string t = v("t");
                if (string.IsNullOrEmpty(t)) { error = "Missing t= value (e.g. t=01:30:00)."; return true; }
                int secs = HmsToSecs(t);
                if (secs <= 0) { error = "Time must be greater than zero."; return true; }
                s.Remaining = secs; s.InitialTime = secs;
                s.Status = "idle"; s.LastTick = null; s.FinishedAt = null;
                return true;
            }

        case "addtime":
            {
                if (!int.TryParse(v("s"), out int add)) { error = "Missing s= value."; return true; }
                s.Remaining += Math.Abs(add);
                if (s.InitialTime <= 0) s.InitialTime = s.Remaining;
                if (s.Status == "finished" && s.Remaining > 0) s.Status = "paused";
                return true;
            }

        case "subtime":
            {
                if (!int.TryParse(v("s"), out int sub)) { error = "Missing s= value."; return true; }
                s.Remaining = Math.Max(0, s.Remaining - Math.Abs(sub));
                if (s.Remaining == 0 && s.Status == "running") { s.Status = "finished"; s.LastTick = null; }
                return true;
            }

        case "setcolor":
            {
                var c = ParseColor(v("v")); if (c == null) { error = "Invalid colour."; return true; }
                s.Color = c; return true;
            }

        case "setfinishcolor":
            {
                var c = ParseColor(v("v")); if (c == null) { error = "Invalid colour."; return true; }
                s.FinishColor = c; return true;
            }

        case "setbgcolor":
            {
                var c = ParseColor(v("v")); if (c == null) { error = "Invalid colour."; return true; }
                s.BgColor = c; return true;
            }

        case "setflash":
            s.FlashOnFinish = ParseBool(v("v"), s.FlashOnFinish);
            return true;

        case "setflashduration":
            if (!int.TryParse(v("v"), out int flashDur) || flashDur < 0) { error = "Missing/invalid v= (seconds)."; return true; }
            s.FlashDuration = flashDur;
            return true;

        case "status":
        case "":
            return true; // no-op, just fall through to returning current state

        default:
            return false; // not a common command — let the caller check overlay-specific ones
    }
}

// ── Bar-specific handling ────────────────────────────────────────────────

string HandleBarCommand((string cmd, NameValueCollection qs) input, string file, JsonSerializerOptions opts)
{
    var (cmd, qs) = input;

    BarState? loaded = LoadJson<BarState>(file, opts);
    if (loaded == null && File.Exists(file))
        Log("[BAR]", $"Could not parse {Path.GetFileName(file)} — using defaults", ConsoleColor.DarkRed, 2);
    BarState s = loaded ?? new BarState();

    string prevStatus = s.Status;
    Tick(s);
    if (prevStatus == "running" && s.Status == "finished")
        Log("[BAR]", $"Time's up — holding at finish colour {s.FinishColor}", ConsoleColor.Red, 1);

    if (cmd is not ("status" or ""))
        Log("[BAR]", $"cmd={cmd} query={FormatQuery(qs)}", ConsoleColor.DarkGray, 2);

    bool handled = HandleCommon(cmd, qs, s, out string? error);

    if (!handled)
    {
        string v(string key) => qs[key] ?? "";
        switch (cmd)
        {
            case "setdirection":
                {
                    string d = v("v").ToLowerInvariant();
                    if (d != "drain" && d != "fill") { error = "Direction must be drain or fill."; break; }
                    s.Direction = d;
                    break;
                }
            case "setbarheight":
                s.BarHeight = int.TryParse(v("v"), out int bh) && bh > 0 ? bh : s.BarHeight;
                break;
            case "setbarwidth":
                {
                    string bw = v("v").Trim();
                    if (bw is "" ) { error = "Missing v= value."; break; }
                    s.BarWidth = bw;
                    break;
                }
            default:
                error = $"Unknown command: \"{cmd}\"";
                break;
        }
    }

    SaveJson(s, file, opts);
    LogCommand("[BAR]", cmd, s, error);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state = s });
}

// ── Radial-specific handling ─────────────────────────────────────────────

string HandleRadialCommand((string cmd, NameValueCollection qs) input, string file, JsonSerializerOptions opts)
{
    var (cmd, qs) = input;

    RadialState? loaded = LoadJson<RadialState>(file, opts);
    if (loaded == null && File.Exists(file))
        Log("[RADIAL]", $"Could not parse {Path.GetFileName(file)} — using defaults", ConsoleColor.DarkRed, 2);
    RadialState s = loaded ?? new RadialState();

    // Guards against a leftover config file from before size/thickness
    // became percentages (they used to be raw pixel values like 300/20).
    s.Size = Math.Clamp(s.Size, 5, 100);
    s.Thickness = Math.Clamp(s.Thickness, 1, 50);

    string prevStatus = s.Status;
    Tick(s);
    if (prevStatus == "running" && s.Status == "finished")
        Log("[RADIAL]", $"Time's up — holding at finish colour {s.FinishColor}", ConsoleColor.Red, 1);

    if (cmd is not ("status" or ""))
        Log("[RADIAL]", $"cmd={cmd} query={FormatQuery(qs)}", ConsoleColor.DarkGray, 2);

    bool handled = HandleCommon(cmd, qs, s, out string? error);

    if (!handled)
    {
        string v(string key) => qs[key] ?? "";
        switch (cmd)
        {
            case "setdirection":
                {
                    string d = v("v").ToLowerInvariant();
                    if (d != "cw" && d != "ccw") { error = "Direction must be cw or ccw."; break; }
                    s.Direction = d;
                    break;
                }
            case "setsize":
                s.Size = int.TryParse(v("v"), out int sz) && sz is >= 5 and <= 100 ? sz : s.Size;
                break;
            case "setthickness":
                s.Thickness = int.TryParse(v("v"), out int th) && th is >= 1 and <= 50 ? th : s.Thickness;
                break;
            case "settrackcolor":
                {
                    var c = ParseColor(v("v")); if (c == null) { error = "Invalid colour."; break; }
                    s.TrackColor = c;
                    break;
                }
            default:
                error = $"Unknown command: \"{cmd}\"";
                break;
        }
    }

    SaveJson(s, file, opts);
    LogCommand("[RADIAL]", cmd, s, error);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state = s });
}

// ── Friendly, colour-coded event log ─────────────────────────────────────
// Level 1: curated one-line summaries for real events only (never polling).
// Level 2 adds a raw state dump underneath each one (see HandleBarCommand /
// HandleRadialCommand above for the raw query-string line).

void LogCommand(string tag, string cmd, OverlayState s, string? error)
{
    if (cmd is "status" or "") return;

    if (error != null)
    {
        Log(tag, $"✗ {cmd} failed — {error}", ConsoleColor.Red, 1);
        return;
    }

    string time = SecsToHms(s.Remaining);
    string extra = s switch
    {
        BarState b    => $"height {b.BarHeight}px, width {b.BarWidth}",
        RadialState r => $"size {r.Size}%, thickness {r.Thickness}%",
        _ => ""
    };

    switch (cmd)
    {
        case "go":
            Log(tag, $"▶ Started — {time}, colour {s.Color} → {s.FinishColor} on finish, dir {s.Direction}", ConsoleColor.Green, 1);
            break;
        case "start":
            Log(tag, $"▶ Started — {time} remaining", ConsoleColor.Green, 1);
            break;
        case "pause":
            Log(tag, $"⏸ Paused — {time} remaining", ConsoleColor.Yellow, 1);
            break;
        case "stop":
            Log(tag, "■ Stopped", ConsoleColor.Red, 1);
            break;
        case "reset":
            Log(tag, $"↺ Reset — {time}", ConsoleColor.DarkYellow, 1);
            break;
        case "settime":
            Log(tag, $"Time set to {time}", ConsoleColor.Cyan, 1);
            break;
        case "addtime":
            Log(tag, $"+ Time added — now {time}", ConsoleColor.Cyan, 1);
            break;
        case "subtime":
            Log(tag, $"− Time subtracted — now {time}", ConsoleColor.Cyan, 1);
            break;
        case "setcolor":
            Log(tag, $"Colour set to {s.Color}", ConsoleColor.Magenta, 1);
            break;
        case "setfinishcolor":
            Log(tag, $"Finish colour set to {s.FinishColor}", ConsoleColor.Magenta, 1);
            break;
        case "setbgcolor":
            Log(tag, $"Background colour set to {s.BgColor}", ConsoleColor.Magenta, 1);
            break;
        case "setflash":
            Log(tag, $"Flash on finish: {(s.FlashOnFinish ? "on" : "off")}", ConsoleColor.Magenta, 1);
            break;
        case "setflashduration":
            Log(tag, $"Flash duration set to {s.FlashDuration}s", ConsoleColor.Magenta, 1);
            break;
        case "setdirection":
            Log(tag, $"Direction set to {s.Direction}", ConsoleColor.Magenta, 1);
            break;
        default:
            Log(tag, $"{cmd} — {extra}", ConsoleColor.Magenta, 1);
            break;
    }

    // Level 2: full resulting state after every real command.
    Log(tag, $"   state: {JsonSerializer.Serialize(s)}", ConsoleColor.DarkGray, 2);
}

// ── State models ──────────────────────────────────────────────────────────

class AppSettings
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8085;

    // 1 = normal operation (what you ship to your friend) — only real events: start/stop/pause/config changes/errors.
    // 2 = adds full diagnostics for every real event: raw query strings, resulting state, load/save issues, full exceptions.
    // 3 = adds literally every HTTP request, including the 5x/sec status polling from the overlay pages.
    [JsonPropertyName("debugLevel")]
    public int DebugLevel { get; set; } = 1;
}

class OverlayState
{
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; } = 0;

    [JsonPropertyName("initialTime")]
    public int InitialTime { get; set; } = 0;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("lastTick")]
    public DateTime? LastTick { get; set; } = null;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#00ff00";

    [JsonPropertyName("finishColor")]
    public string FinishColor { get; set; } = "#ff0000";

    [JsonPropertyName("bgColor")]
    public string BgColor { get; set; } = "transparent";

    [JsonPropertyName("flashOnFinish")]
    public bool FlashOnFinish { get; set; } = true;

    // How long (seconds) the overlay flashes full-red before quietly
    // reverting to idle if nothing else tells it what to do next.
    [JsonPropertyName("flashDuration")]
    public int FlashDuration { get; set; } = 30;

    // Set the instant Status becomes "finished"; used to time the flash.
    [JsonPropertyName("finishedAt")]
    public DateTime? FinishedAt { get; set; } = null;

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "drain";
}

class BarState : OverlayState
{
    [JsonPropertyName("barHeight")]
    public int BarHeight { get; set; } = 5;

    [JsonPropertyName("barWidth")]
    public string BarWidth { get; set; } = "100%";

    public BarState() { Direction = "drain"; }
}

class RadialState : OverlayState
{
    // Percentage of the viewport's smaller dimension (vmin) used as the
    // ring's diameter — this is what makes it scale as the OBS Browser
    // Source is resized, instead of staying a fixed pixel size.
    [JsonPropertyName("size")]
    public int Size { get; set; } = 60;

    // Percentage of the ring's diameter used as the stroke width, so
    // the line thickness scales proportionally too.
    [JsonPropertyName("thickness")]
    public int Thickness { get; set; } = 7;

    [JsonPropertyName("trackColor")]
    public string TrackColor { get; set; } = "rgba(255,255,255,0.15)";

    public RadialState()
    {
        Direction = "cw";
    }
}

// ── README content written on first run ──────────────────────────────────

static class ReadmeText
{
    public const string Content = """
    ============================================================
     Ad Break Timer — Setup & Command Guide

     Made by Kaydee.Codes (https://kaydee.codes/)
     Free to use, no data collected, ever.
    ============================================================

    WHAT THIS IS
    ------------
    A tiny local web server (this .exe) that hosts two OBS overlay
    pages and lets you control them with simple web requests —
    perfect for Streamer.bot's "Web Request" or "Execute HTTP
    Request" action.

    FIRST TIME SETUP
    -----------------
    1. Run the exe once. It will print two URLs to the console, e.g.
           http://localhost:8085/bar/
           http://localhost:8085/radial/
    2. In OBS, add a Browser Source, paste in whichever URL you want
       (bar, radial, or both as separate sources), and set the Browser
       Source's Width/Height to whatever you like — both overlays are
       fully responsive and just fill whatever size you give them.
       There's no fixed resolution to match.
    3. Tick "Shutdown source when not visible" OFF so the timer
       keeps running in the background between ad breaks.

    ABOUT THE PORT
    ---------------
    The port is saved in config/settings.json. Every time you start
    the exe it tries that saved port first. If something else is
    already using it, it automatically tries the next port up and
    tells you in the console AND re-saves the new port — so it will
    keep using that same port from then on unless it becomes busy
    again. If you ever change the port yourself, just edit
    config/settings.json and restart.

    CONSOLE DEBUG LEVEL
    ---------------------
    Also saved in config/settings.json as "debugLevel". Controls how
    much the console window shows you:

      1  Normal operation (default — this is what you ship to a
         friend). Shows only real events: timer started/paused/
         stopped, config changes, errors, and when a countdown
         naturally hits zero. The 5x/sec status polling from the
         overlay pages is completely silent.

      2  Full diagnostics. Everything in level 1, plus the raw
         command + query string behind every real event, the full
         resulting state after each change, config file load/parse
         failures, and full exception stack traces on errors.

      3  Everything. Everything in level 2, plus every single HTTP
         request the server receives — including the constant status
         polling from the overlay pages. Very noisy; only useful if
         you're debugging the polling itself.

    Two ways to change it:
      - Edit config/settings.json ("debugLevel": 1, 2, or 3) and
        restart, OR
      - While it's running, visit (or GET from Streamer.bot):
            http://localhost:8085/debug/set?level=2
        This takes effect immediately and is also saved for next time.

    You can also start it just once at a higher level without saving
    that choice, e.g. from a terminal:
        AdBreakTimer.exe --debug 3

    THE TWO OVERLAYS
    -----------------
    /bar/     A bar pinned to the very bottom of whatever size OBS
              Browser Source you give it, spanning the full width.
              Fills or drains left-to-right as time passes. Height is
              configurable (default 5px) via cmd=setbarheight.
    /radial/  A circular progress ring, centred in whatever size OBS
              Browser Source you give it. Sweeps clockwise or
              anticlockwise as time passes. Diameter is a percentage
              of the viewport's smaller side (default 60%), so it
              genuinely grows and shrinks as you resize the source —
              configurable via cmd=setsize.

    Both pages are fully responsive — there's no fixed canvas size to
    match. Resize the OBS Browser Source to whatever you want and the
    overlay just fills it.

    Each has its OWN config file (config/bar.json / config/radial.json)
    and its OWN API, so you can run totally different timers on each
    if you want, or just use whichever style you like in OBS and
    ignore the other.

    THE EASY WAY — ONE-SHOT "go" COMMAND
    --------------------------------------
    This is the one you'll use from Streamer.bot most of the time.
    It sets whatever you give it and starts the countdown immediately:

        /bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

        /radial/api?cmd=go&t=00:03:00&color=%23ff0000&dir=cw

    Parameters (all optional except t):
        t         time, hh:mm:ss or mm:ss or raw seconds  (required)
        color     the running colour, e.g. %2300ff00 (that's #00ff00 URL-encoded)
        finish    the colour it turns when the countdown hits zero
        dir       bar: drain | fill        radial: cw | ccw
        flash     on | off — whether to flash when finished
        flashfor  seconds to flash before auto-clearing (default 30)

    WHAT HAPPENS WHEN A COUNTDOWN HITS ZERO
    ------------------------------------------
    The bar/ring immediately snaps to FULL (whole bar / whole ring) in
    the "finish" colour and flashes for flashDuration seconds (30 by
    default). After that, if nothing else has told it what to do next,
    it automatically clears itself back to idle (invisible) — so it's
    never left stuck lit up forever if Streamer.bot is a little late
    sending the next command. Change the duration any time with:
        cmd=setflashduration&v=45

    EXAMPLE STREAMER.BOT SETUP FOR YOUR AD-BREAK FLOW
    ----------------------------------------------------
    When a Twitch ad break finishes (next one in ~1 hour):
        GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

    When Streamer.bot detects a 3-minute ad break starting:
        GET http://localhost:8085/bar/api?cmd=go&t=00:03:00&color=%23ff0000&dir=drain

    When that ad break's Streamer.bot timer ends (back to normal):
        GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

    If Streamer.bot's own countdown finishes before you send the next
    "go" (e.g. you're mid ad-break and haven't re-armed the timer
    yet), the bar/ring will flash full and red for flashDuration
    seconds and then clear itself automatically — so it's never left
    looking broken or stuck half-finished.

    ALL COMMANDS
    ------------
    Every command below works on both /bar/api and /radial/api,
    unless marked otherwise. Replace ... with your value.

      General:
        cmd=go&t=...&color=...&finish=...&dir=...&flash=on|off&flashfor=30   (see above)
        cmd=start
        cmd=pause
        cmd=stop                     stops and clears time
        cmd=reset                    goes back to initial time, stays idle
        cmd=status                   just returns current state (used by the overlay page itself)
        cmd=settime&t=00:05:00
        cmd=addtime&s=30
        cmd=subtime&s=30
        cmd=setcolor&v=%2300ff00
        cmd=setfinishcolor&v=%23ff0000
        cmd=setbgcolor&v=%23000000   (or v=transparent)
        cmd=setflash&v=on|off
        cmd=setflashduration&v=30    seconds to flash before auto-clearing to idle

      Bar only:
        cmd=setdirection&v=drain|fill
        cmd=setbarheight&v=5
        cmd=setbarwidth&v=100%

      Radial only:
        cmd=setdirection&v=cw|ccw
        cmd=setsize&v=60             diameter as % of the viewport's smaller side (5-100, default 60)
        cmd=setthickness&v=7         ring stroke width as % of the diameter (1-50, default 7)
        cmd=settrackcolor&v=%23333333   the "unfilled" background ring colour

    Colours must be URL-encoded if using #: %23 = #, e.g. #ff0000 -> %23ff0000

    TROUBLESHOOTING
    -----------------
    - Nothing shows in OBS: check the console window is still open
      and showing "running" — and double check the URL/port match.
    - Bar/ring never seems to move: make sure you called cmd=go or
      cmd=settime + cmd=start — status alone won't start it.
    - Want to hand-edit defaults: just edit config/bar.json or
      config/radial.json while the exe is NOT running, then start it.

    ============================================================
    Made by Kaydee.Codes — https://kaydee.codes/
    Free to use, no data collected, ever.
    ============================================================
    """;
}

// ============================================================
// Ad Break Timer - Lightweight Overlay Web Server
//
// This is my own rebuild of a bigger timer/overlay tool I made a
// while back. I stripped it down to just the part I actually use
// day to day: a bar and a radial ring that I drive from Streamer.bot
// during Twitch ad breaks, plus enough config that a friend can run
// it without me holding their hand.
//
// It's one exe. No install, no external files to lose, no database.
// Everything it needs (the two HTML overlay pages) is baked in as
// embedded resources at build time, and the only things it writes to
// disk are small JSON files in a "config" folder next to itself.
//
//   http://localhost:<port>/bar/      -> bottom progress bar overlay
//   http://localhost:<port>/radial/   -> circular progress ring overlay
//
// Both pages poll their own /api endpoint 5 times a second and just
// redraw themselves from whatever state comes back. That polling
// model is deliberate: it means OBS can load/unload the browser
// source at any point and it'll just pick back up from wherever the
// timer actually is, no reconnect logic needed on either side.
//
// config/README.txt gets written next to the exe on first run and
// has the full command list, so I don't need to keep this comment
// block in sync with every feature I add later.
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

// ------------------------------------------------------------
// Paths and config folder
// ------------------------------------------------------------
// Everything lives next to the exe, not in AppData or Program Files
// somewhere. That's on purpose: if I ever want to wipe a test setup
// or hand someone a fresh copy, I just delete the folder. No hidden
// state anywhere else on the machine.

string exeDir = AppContext.BaseDirectory;
string configDir = Path.Combine(exeDir, "config");
string settingsFile = Path.Combine(configDir, "settings.json");
string barFile = Path.Combine(configDir, "bar.json");
string radialFile = Path.Combine(configDir, "radial.json");
string readmeFile = Path.Combine(configDir, "README.txt");

Directory.CreateDirectory(configDir);
bool isFirstRun = !File.Exists(settingsFile);

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};

// ------------------------------------------------------------
// Load settings (port + debug level)
// ------------------------------------------------------------

AppSettings settings = LoadJson<AppSettings>(settingsFile, jsonOpts) ?? new AppSettings();
settings.DebugLevel = Math.Clamp(settings.DebugLevel, 1, 3);

// I let myself override the debug level for a single run without
// touching the saved config, e.g. AdBreakTimer.exe --debug 3
// Useful when I just want to see everything once without leaving
// verbose logging on for next time.
for (int i = 0; i < args.Length; i++)
{
    bool isDebugFlag = args[i] == "--debug" || args[i] == "-d";
    if (isDebugFlag && i + 1 < args.Length && int.TryParse(args[i + 1], out int overrideLevel))
        settings.DebugLevel = Math.Clamp(overrideLevel, 1, 3);
}

if (isFirstRun)
{
    File.WriteAllText(readmeFile, ReadmeText.Content);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("First run, I just created a 'config' folder next to this exe.");
    Console.WriteLine($"  Full setup and command guide: {readmeFile}");
    Console.ResetColor();
    Console.WriteLine();
}

// ------------------------------------------------------------
// Bind to a port
// ------------------------------------------------------------
// I try the port I used last time first. If something else has
// grabbed it, I walk upward until one binds and then save that new
// port back to settings.json, so next launch tries the working one
// first instead of colliding again.

var (listener, boundPort) = StartListenerAutoPort(settings.Port);
if (boundPort != settings.Port)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[PORT] {settings.Port} was unavailable, using {boundPort} instead.");
    Console.WriteLine("       Update your OBS Browser Source URLs to match.");
    Console.ResetColor();
    settings.Port = boundPort;
    SaveJson(settings, settingsFile, jsonOpts);
}

string baseUrl = $"http://localhost:{boundPort}";

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("========================================================");
Console.WriteLine("   Ad Break Timer, running");
Console.WriteLine("========================================================");
Console.ResetColor();
Console.WriteLine($"  Bar overlay     : {baseUrl}/bar/");
Console.WriteLine($"  Radial overlay  : {baseUrl}/radial/");
Console.WriteLine();
Console.WriteLine($"  Bar API         : {baseUrl}/bar/api?cmd=...");
Console.WriteLine($"  Radial API      : {baseUrl}/radial/api?cmd=...");
Console.WriteLine();
Console.WriteLine($"  Config folder   : {configDir}");
Console.WriteLine($"  Debug level     : {settings.DebugLevel}  (1 = normal, 2 = full diagnostics, 3 = everything incl. polling)");
Console.WriteLine($"                    change anytime: {baseUrl}/debug/set?level=1|2|3");
Console.WriteLine("  Press Ctrl+C to stop.");
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("  Made by Kaydee.Codes (https://kaydee.codes/), free to use, no data collected, ever.");
Console.ResetColor();
Console.WriteLine();

// ------------------------------------------------------------
// Main request loop
// ------------------------------------------------------------
// HttpListener hands me one request context at a time from
// GetContextAsync. I fire off HandleRequest without awaiting it so
// a slow request (or a browser tab left open polling) never blocks
// the next one from being picked up. Each request is independent
// anyway since state lives in the JSON files, not in memory.

while (true)
{
    HttpListenerContext ctx = await listener.GetContextAsync();
    _ = HandleRequest(ctx);
}

async Task HandleRequest(HttpListenerContext ctx)
{
    HttpListenerRequest req = ctx.Request;
    HttpListenerResponse res = ctx.Response;
    string path = req.Url?.AbsolutePath ?? "/";

    // Level 3 only. This is deliberately the noisiest possible log line,
    // every single request including the 5x/sec status polling. I only
    // want this on when I'm chasing something in the polling itself.
    Log("[HTTP]", $"{req.HttpMethod} {path}{req.Url?.Query}", ConsoleColor.DarkGray, 3);

    try
    {
        // CORS wide open and no caching. This only ever binds to
        // localhost so I'm not worried about exposing it to randoms,
        // and OBS/browsers need fresh state on every poll anyway.
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");

        if (path is "/" or "")
        {
            await SendText(res, 200, "text/html; charset=utf-8", IndexHtml(baseUrl));
            return;
        }

        if (path.Equals("/debug/set", StringComparison.OrdinalIgnoreCase))
        {
            string levelParam = req.QueryString["level"] ?? "";
            if (int.TryParse(levelParam, out int newLevel) && newLevel is >= 1 and <= 3)
            {
                settings.DebugLevel = newLevel;
                SaveJson(settings, settingsFile, jsonOpts);
                Log("[DEBUG]", $"Debug level changed to {newLevel}", ConsoleColor.Cyan, 1);
                await SendText(res, 200, "text/plain", $"Debug level set to {newLevel}");
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

        if (path is "/bar" or "/bar/")
        {
            Log("[BAR]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan, 1);
            await SendEmbedded(res, "bar.html");
            return;
        }

        if (path is "/radial" or "/radial/")
        {
            Log("[RADIAL]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan, 1);
            await SendEmbedded(res, "radial.html");
            return;
        }

        await SendText(res, 404, "text/plain", $"404 Not Found: {path}");
    }
    catch (Exception ex)
    {
        // Level 1 just gets the message, level 2+ gets the full stack
        // trace. I always show something at level 1 because a silent
        // 500 with nothing in the console is the worst debugging
        // experience I can think of.
        string detail = settings.DebugLevel >= 2 ? ex.ToString() : ex.Message;
        Log("[ERROR]", detail, ConsoleColor.Red, 1);
        try { await SendText(res, 500, "text/plain", ex.Message); } catch { /* connection's probably already gone, nothing to do */ }
    }
}

// ------------------------------------------------------------
// Console logging
// ------------------------------------------------------------
// level 1 = normal (default), 2 = full diagnostics, 3 = everything
// including polling. Only prints if settings.DebugLevel is at least
// that level. This is a local function (not a static one) on
// purpose so it can read the live settings.DebugLevel value without
// me having to thread it through every call site.

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

// ------------------------------------------------------------
// Command parsing
// ------------------------------------------------------------
// I support two styles of hitting the API:
//   ?cmd=go&t=01:00:00&color=%2300ff00      (normal query string, use this one)
//   /go_01:00:00                            (path style, single value only, mostly for quick manual testing)
// The query string style is what Streamer.bot and the README examples
// use. The path style is just a leftover from the original tool and
// I'm keeping it since it doesn't cost anything to support.

static (string cmd, NameValueCollection qs) ParseCommand(string path, string apiPrefix, HttpListenerRequest req)
{
    NameValueCollection qs = req.QueryString;
    string cmd = (qs["cmd"] ?? "").ToLowerInvariant().Trim();

    if (string.IsNullOrEmpty(cmd) && path.Length > apiPrefix.Length)
    {
        string segment = path[apiPrefix.Length..].TrimStart('/');
        if (!string.IsNullOrEmpty(segment))
        {
            int underscoreIndex = segment.IndexOf('_');
            cmd = (underscoreIndex >= 0 ? segment[..underscoreIndex] : segment).ToLowerInvariant();
        }
    }
    return (cmd, qs);
}

// ------------------------------------------------------------
// Port binding with auto increment
// ------------------------------------------------------------
// This is the bit that stops me (or my friend) from having to think
// about ports at all. Try the saved one, and if it's taken, just try
// the next one up until something works. 50 attempts is way more
// than I'll ever need, it's just a sane upper bound so this can't
// loop forever on a genuinely broken machine.

static (HttpListener listener, int port) StartListenerAutoPort(int startPort)
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
    throw new Exception("Could not find a free port after 50 attempts. That's almost certainly not a port problem, something else is wrong.");
}

// ------------------------------------------------------------
// JSON file helpers
// ------------------------------------------------------------

static T? LoadJson<T>(string file, JsonSerializerOptions opts) where T : class
{
    if (!File.Exists(file)) return null;
    try
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), opts);
    }
    catch
    {
        // Corrupt or hand-edited-badly JSON just falls back to defaults
        // rather than crashing the whole server. The caller logs this
        // at debug level 2 so it's not silent, just not fatal.
        return null;
    }
}

static void SaveJson<T>(T obj, string file, JsonSerializerOptions opts)
{
    string? dir = Path.GetDirectoryName(file);
    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(file, JsonSerializer.Serialize(obj, opts));
}

// ------------------------------------------------------------
// HTTP response helpers
// ------------------------------------------------------------

static async Task SendJson(HttpListenerResponse res, string json)
{
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    res.StatusCode = 200;
    res.ContentType = "application/json; charset=utf-8";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

static async Task SendText(HttpListenerResponse res, int statusCode, string mimeType, string body)
{
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    res.StatusCode = statusCode;
    res.ContentType = mimeType;
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

static async Task SendEmbedded(HttpListenerResponse res, string fileName)
{
    // The overlay HTML files get compiled into the exe as embedded
    // resources (see the csproj), so there's nothing on disk that a
    // friend could accidentally move, rename, or delete. The resource
    // name is just the project's root namespace plus the folder path
    // with dots instead of slashes, that's a .NET convention, not
    // something I chose.
    Assembly asm = Assembly.GetExecutingAssembly();
    string resourceName = $"AdBreakTimer.Web.{fileName}";
    using Stream? stream = asm.GetManifestResourceStream(resourceName);
    if (stream == null)
    {
        await SendText(res, 500, "text/plain", $"Embedded resource not found: {resourceName}. This means the exe wasn't built correctly.");
        return;
    }
    using var reader = new StreamReader(stream);
    string html = await reader.ReadToEndAsync();
    await SendText(res, 200, "text/html; charset=utf-8", html);
}

// This is just a tiny landing page so hitting the base URL in a
// browser shows something useful instead of a 404. Not meant to be
// pretty, just informative enough that I remember what the URLs are
// six months from now.
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
<p style="color:#666;font-size:0.85rem;">Made by <a href="https://kaydee.codes/" style="color:#7dd3fc;">Kaydee.Codes</a>, free to use, no data collected, ever.</p>
</body></html>
""";

// ------------------------------------------------------------
// Small value parsing helpers
// ------------------------------------------------------------

// Accepts hh:mm:ss, mm:ss, or a raw number of seconds. Streamer.bot
// and I both tend to type hh:mm:ss so that's the primary format, the
// others are just convenience.
static int HmsToSecs(string value)
{
    value = value.Trim();
    string[] parts = value.Split(':');
    if (parts.Length == 3)
        return Math.Abs(int.Parse(parts[0])) * 3600 + Math.Abs(int.Parse(parts[1])) * 60 + Math.Abs(int.Parse(parts[2]));
    if (parts.Length == 2)
        return Math.Abs(int.Parse(parts[0])) * 60 + Math.Abs(int.Parse(parts[1]));
    return int.TryParse(value, out int seconds) ? Math.Abs(seconds) : 0;
}

static string SecsToHms(int totalSeconds)
{
    totalSeconds = Math.Max(0, totalSeconds);
    return $"{totalSeconds / 3600:D2}:{totalSeconds % 3600 / 60:D2}:{totalSeconds % 60:D2}";
}

// Just for the level 2 debug log line, so I can see exactly what came
// in on a request without NameValueCollection's default ToString()
// giving me a useless type name instead of the actual values.
static string FormatQuery(NameValueCollection qs)
{
    if (qs.Count == 0) return "(none)";
    return string.Join("&", qs.AllKeys.Where(key => key != null).Select(key => $"{key}={qs[key]}"));
}

// Accepts hex (#rrggbb, #rgb, with or without alpha), a handful of
// CSS named colours, rgb()/rgba()/hsl(), or literally "transparent".
// I URL-decode first since %23ff0000 is how a # actually arrives over
// the wire.
static string? ParseColor(string value)
{
    value = Uri.UnescapeDataString(value ?? "").Trim();
    if (value is "" or "null") return null;
    if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "transparent";
    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9a-fA-F]{3,8}$")) return value;
    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z]{2,30}$")) return value;
    if (value.StartsWith("rgb(") || value.StartsWith("rgba(") || value.StartsWith("hsl(")) return value;
    return null;
}

static bool ParseBool(string value, bool fallback) => value?.ToLowerInvariant() switch
{
    "on" or "1" or "true" or "yes" => true,
    "off" or "0" or "false" or "no" => false,
    _ => fallback
};

// ------------------------------------------------------------
// Timer tick
// ------------------------------------------------------------
// I don't run a background timer or a tick loop anywhere. Instead
// every state file stores lastTick (when it was last updated) and I
// work out how much time has actually passed based on the wall clock
// whenever a request comes in. That means the countdown stays
// accurate even if OBS has the browser source unloaded for a while,
// or the exe itself gets suspended and resumed, since I'm never
// relying on a timer callback actually firing on schedule.

static void Tick(OverlayState s)
{
    if (s.Status == "running" && s.LastTick != null)
    {
        int elapsedSeconds = (int)Math.Floor((DateTime.UtcNow - s.LastTick.Value).TotalSeconds);
        if (elapsedSeconds <= 0) return;

        s.Remaining -= elapsedSeconds;
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

    // Once it's finished, I want it to flash for FlashDuration seconds
    // and then quietly go back to idle on its own, so the overlay never
    // gets stuck lit up forever if Streamer.bot is late sending the
    // next command. This check runs on every request (including plain
    // status polls) so the transition happens on schedule regardless
    // of whether anyone actually sends a new command.
    if (s.Status == "finished" && s.FinishedAt != null)
    {
        double secondsSinceFinish = (DateTime.UtcNow - s.FinishedAt.Value).TotalSeconds;
        if (secondsSinceFinish >= s.FlashDuration)
        {
            s.Status = "idle";
            s.FinishedAt = null;
        }
    }
}

// ------------------------------------------------------------
// Commands shared by both overlays
// ------------------------------------------------------------
// Returns true if the command was handled here (whether it succeeded
// or produced an error), false if it's not a command this function
// knows about, in which case the caller checks its own overlay
// specific commands (setbarheight, setsize, and so on).

static bool HandleCommon(string cmd, NameValueCollection qs, OverlayState s, out string? error)
{
    error = null;
    string Get(string key) => qs[key] ?? "";

    switch (cmd)
    {
        // This is the one I actually use from Streamer.bot day to day.
        // One request sets whatever's given and starts the countdown
        // immediately, instead of chaining four or five separate calls.
        case "go":
            {
                string timeValue = Get("t");
                if (string.IsNullOrEmpty(timeValue)) { error = "go requires t= (e.g. t=01:00:00)"; return true; }
                int seconds = HmsToSecs(timeValue);
                if (seconds <= 0) { error = "Time must be greater than zero."; return true; }

                if (!string.IsNullOrEmpty(Get("color")))
                {
                    string? color = ParseColor(Get("color"));
                    if (color != null) s.Color = color;
                }
                if (!string.IsNullOrEmpty(Get("finish")))
                {
                    string? finishColor = ParseColor(Get("finish"));
                    if (finishColor != null) s.FinishColor = finishColor;
                }
                if (!string.IsNullOrEmpty(Get("dir"))) s.Direction = Get("dir").ToLowerInvariant();
                if (!string.IsNullOrEmpty(Get("flash"))) s.FlashOnFinish = ParseBool(Get("flash"), s.FlashOnFinish);
                if (!string.IsNullOrEmpty(Get("flashfor")) && int.TryParse(Get("flashfor"), out int flashSeconds) && flashSeconds >= 0)
                    s.FlashDuration = flashSeconds;

                s.Remaining = seconds;
                s.InitialTime = seconds;
                s.Status = "running";
                s.LastTick = DateTime.UtcNow;
                s.FinishedAt = null;
                return true;
            }

        case "start":
            if (s.Remaining <= 0) { error = "No time set, use setTime or go first."; return true; }
            if (s.InitialTime <= 0) s.InitialTime = s.Remaining;
            s.Status = "running";
            s.LastTick = DateTime.UtcNow;
            s.FinishedAt = null;
            return true;

        case "pause":
            if (s.Status == "running") { s.Status = "paused"; s.LastTick = null; }
            return true;

        case "stop":
            s.Status = "idle";
            s.Remaining = 0;
            s.LastTick = null;
            s.FinishedAt = null;
            return true;

        case "reset":
            s.Status = "idle";
            s.Remaining = s.InitialTime;
            s.LastTick = null;
            s.FinishedAt = null;
            return true;

        case "settime":
            {
                string timeValue = Get("t");
                if (string.IsNullOrEmpty(timeValue)) { error = "Missing t= value (e.g. t=01:30:00)."; return true; }
                int seconds = HmsToSecs(timeValue);
                if (seconds <= 0) { error = "Time must be greater than zero."; return true; }
                s.Remaining = seconds;
                s.InitialTime = seconds;
                s.Status = "idle";
                s.LastTick = null;
                s.FinishedAt = null;
                return true;
            }

        case "addtime":
            {
                if (!int.TryParse(Get("s"), out int secondsToAdd)) { error = "Missing s= value."; return true; }
                s.Remaining += Math.Abs(secondsToAdd);
                if (s.InitialTime <= 0) s.InitialTime = s.Remaining;
                // If it had already finished and I'm topping up time,
                // bring it back to paused rather than leaving it stuck
                // in the finished/flash state with time left on it.
                if (s.Status == "finished" && s.Remaining > 0)
                {
                    s.Status = "paused";
                    s.FinishedAt = null;
                }
                return true;
            }

        case "subtime":
            {
                if (!int.TryParse(Get("s"), out int secondsToSubtract)) { error = "Missing s= value."; return true; }
                s.Remaining = Math.Max(0, s.Remaining - Math.Abs(secondsToSubtract));
                if (s.Remaining == 0 && s.Status == "running")
                {
                    s.Status = "finished";
                    s.LastTick = null;
                    s.FinishedAt = DateTime.UtcNow;
                }
                return true;
            }

        case "setcolor":
            {
                string? color = ParseColor(Get("v"));
                if (color == null) { error = "Invalid colour."; return true; }
                s.Color = color;
                return true;
            }

        case "setfinishcolor":
            {
                string? color = ParseColor(Get("v"));
                if (color == null) { error = "Invalid colour."; return true; }
                s.FinishColor = color;
                return true;
            }

        case "setbgcolor":
            {
                string? color = ParseColor(Get("v"));
                if (color == null) { error = "Invalid colour."; return true; }
                s.BgColor = color;
                return true;
            }

        case "setflash":
            s.FlashOnFinish = ParseBool(Get("v"), s.FlashOnFinish);
            return true;

        case "setflashduration":
            if (!int.TryParse(Get("v"), out int newFlashDuration) || newFlashDuration < 0)
            {
                error = "Missing or invalid v= (seconds).";
                return true;
            }
            s.FlashDuration = newFlashDuration;
            return true;

        case "status":
        case "":
            // No-op. This is the 5x/sec poll from the overlay pages,
            // it just wants the current state back, nothing to change.
            return true;

        default:
            // Not one of mine, let the bar/radial specific switch have a look.
            return false;
    }
}

// ------------------------------------------------------------
// Bar specific command handling
// ------------------------------------------------------------

string HandleBarCommand((string cmd, NameValueCollection qs) input, string file, JsonSerializerOptions opts)
{
    (string cmd, NameValueCollection qs) = input;

    BarState? loaded = LoadJson<BarState>(file, opts);
    if (loaded == null && File.Exists(file))
        Log("[BAR]", $"Could not parse {Path.GetFileName(file)}, using defaults", ConsoleColor.DarkRed, 2);
    BarState state = loaded ?? new BarState();

    string statusBeforeTick = state.Status;
    Tick(state);
    if (statusBeforeTick == "running" && state.Status == "finished")
        Log("[BAR]", $"Time's up, flashing finish colour {state.FinishColor}", ConsoleColor.Red, 1);

    if (cmd is not ("status" or ""))
        Log("[BAR]", $"cmd={cmd} query={FormatQuery(qs)}", ConsoleColor.DarkGray, 2);

    bool handled = HandleCommon(cmd, qs, state, out string? error);

    if (!handled)
    {
        string Get(string key) => qs[key] ?? "";
        switch (cmd)
        {
            case "setdirection":
                {
                    string direction = Get("v").ToLowerInvariant();
                    if (direction != "drain" && direction != "fill") { error = "Direction must be drain or fill."; break; }
                    state.Direction = direction;
                    break;
                }
            case "setbarheight":
                state.BarHeight = int.TryParse(Get("v"), out int height) && height > 0 ? height : state.BarHeight;
                break;
            case "setbarwidth":
                {
                    string width = Get("v").Trim();
                    if (width == "") { error = "Missing v= value."; break; }
                    state.BarWidth = width;
                    break;
                }
            default:
                error = $"Unknown command: \"{cmd}\"";
                break;
        }
    }

    SaveJson(state, file, opts);
    LogCommand("[BAR]", cmd, state, error);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state });
}

// ------------------------------------------------------------
// Radial specific command handling
// ------------------------------------------------------------

string HandleRadialCommand((string cmd, NameValueCollection qs) input, string file, JsonSerializerOptions opts)
{
    (string cmd, NameValueCollection qs) = input;

    RadialState? loaded = LoadJson<RadialState>(file, opts);
    if (loaded == null && File.Exists(file))
        Log("[RADIAL]", $"Could not parse {Path.GetFileName(file)}, using defaults", ConsoleColor.DarkRed, 2);
    RadialState state = loaded ?? new RadialState();

    // Size and thickness used to be raw pixel values in an earlier
    // version before I made them percentages of the viewport so the
    // ring actually scales with the OBS source. Clamping here means
    // an old config file left over from that version just gets pulled
    // back into range instead of rendering a giant broken ring.
    state.Size = Math.Clamp(state.Size, 5, 100);
    state.Thickness = Math.Clamp(state.Thickness, 1, 50);

    string statusBeforeTick = state.Status;
    Tick(state);
    if (statusBeforeTick == "running" && state.Status == "finished")
        Log("[RADIAL]", $"Time's up, flashing finish colour {state.FinishColor}", ConsoleColor.Red, 1);

    if (cmd is not ("status" or ""))
        Log("[RADIAL]", $"cmd={cmd} query={FormatQuery(qs)}", ConsoleColor.DarkGray, 2);

    bool handled = HandleCommon(cmd, qs, state, out string? error);

    if (!handled)
    {
        string Get(string key) => qs[key] ?? "";
        switch (cmd)
        {
            case "setdirection":
                {
                    string direction = Get("v").ToLowerInvariant();
                    if (direction != "cw" && direction != "ccw") { error = "Direction must be cw or ccw."; break; }
                    state.Direction = direction;
                    break;
                }
            case "setsize":
                state.Size = int.TryParse(Get("v"), out int size) && size is >= 5 and <= 100 ? size : state.Size;
                break;
            case "setthickness":
                state.Thickness = int.TryParse(Get("v"), out int thickness) && thickness is >= 1 and <= 50 ? thickness : state.Thickness;
                break;
            case "settrackcolor":
                {
                    string? color = ParseColor(Get("v"));
                    if (color == null) { error = "Invalid colour."; break; }
                    state.TrackColor = color;
                    break;
                }
            default:
                error = $"Unknown command: \"{cmd}\"";
                break;
        }
    }

    SaveJson(state, file, opts);
    LogCommand("[RADIAL]", cmd, state, error);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state });
}

// ------------------------------------------------------------
// Friendly, colour coded event log
// ------------------------------------------------------------
// Level 1 shows a curated one-liner per real command, nothing for
// status polls. Level 2 adds a raw state dump underneath. I kept
// this as one big switch instead of a dictionary lookup because I
// wanted each message to be able to reference the resulting state
// (the time, the colour, and so on), and a switch is the easiest
// place to do that without extra ceremony.

void LogCommand(string tag, string cmd, OverlayState state, string? error)
{
    if (cmd is "status" or "") return;

    if (error != null)
    {
        Log(tag, $"FAILED {cmd}: {error}", ConsoleColor.Red, 1);
        return;
    }

    string time = SecsToHms(state.Remaining);

    switch (cmd)
    {
        case "go":
            Log(tag, $"Started, {time}, colour {state.Color} to {state.FinishColor} on finish, dir {state.Direction}", ConsoleColor.Green, 1);
            break;
        case "start":
            Log(tag, $"Started, {time} remaining", ConsoleColor.Green, 1);
            break;
        case "pause":
            Log(tag, $"Paused, {time} remaining", ConsoleColor.Yellow, 1);
            break;
        case "stop":
            Log(tag, "Stopped", ConsoleColor.Red, 1);
            break;
        case "reset":
            Log(tag, $"Reset to {time}", ConsoleColor.DarkYellow, 1);
            break;
        case "settime":
            Log(tag, $"Time set to {time}", ConsoleColor.Cyan, 1);
            break;
        case "addtime":
            Log(tag, $"Time added, now {time}", ConsoleColor.Cyan, 1);
            break;
        case "subtime":
            Log(tag, $"Time subtracted, now {time}", ConsoleColor.Cyan, 1);
            break;
        case "setcolor":
            Log(tag, $"Colour set to {state.Color}", ConsoleColor.Magenta, 1);
            break;
        case "setfinishcolor":
            Log(tag, $"Finish colour set to {state.FinishColor}", ConsoleColor.Magenta, 1);
            break;
        case "setbgcolor":
            Log(tag, $"Background colour set to {state.BgColor}", ConsoleColor.Magenta, 1);
            break;
        case "setflash":
            Log(tag, $"Flash on finish: {(state.FlashOnFinish ? "on" : "off")}", ConsoleColor.Magenta, 1);
            break;
        case "setflashduration":
            Log(tag, $"Flash duration set to {state.FlashDuration}s", ConsoleColor.Magenta, 1);
            break;
        case "setdirection":
            Log(tag, $"Direction set to {state.Direction}", ConsoleColor.Magenta, 1);
            break;
        default:
            // Covers the overlay specific commands I didn't give a
            // custom message to (setbarheight, setsize, and so on).
            // Good enough for level 1, level 2 shows the full state
            // right underneath anyway.
            Log(tag, cmd, ConsoleColor.Magenta, 1);
            break;
    }

    Log(tag, $"   state: {JsonSerializer.Serialize(state)}", ConsoleColor.DarkGray, 2);
}

// ============================================================
// State models
// ============================================================
// These map straight onto the JSON files in config/. I'm using
// JsonPropertyName everywhere to keep the JSON keys lowercase, which
// is what the overlay pages' JavaScript expects since I never bothered
// with a build step over there.

class AppSettings
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8085;

    // 1 = normal operation, this is what I actually ship to a friend.
    // 2 = full diagnostics: raw query strings, resulting state, config
    //     load or parse failures, full exception stack traces.
    // 3 = everything, including the 5x/sec status polling. Very noisy,
    //     only useful when I'm debugging the polling itself.
    [JsonPropertyName("debugLevel")]
    public int DebugLevel { get; set; } = 1;
}

// Shared by both overlay types. Bar and radial each add their own
// visual specific fields on top of this in their own subclass.
class OverlayState
{
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; } = 0;

    [JsonPropertyName("initialTime")]
    public int InitialTime { get; set; } = 0;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle"; // idle, running, paused, finished

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

    // How long, in seconds, the overlay flashes full and in the finish
    // colour before it quietly reverts to idle on its own.
    [JsonPropertyName("flashDuration")]
    public int FlashDuration { get; set; } = 30;

    // Set the instant Status flips to "finished", used by Tick() to
    // work out when FlashDuration has elapsed.
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

    public BarState()
    {
        Direction = "drain";
    }
}

class RadialState : OverlayState
{
    // Diameter as a percentage of the viewport's smaller dimension
    // (vmin). This is what makes the ring scale as the OBS Browser
    // Source gets resized, instead of staying a fixed pixel size.
    [JsonPropertyName("size")]
    public int Size { get; set; } = 60;

    // Stroke width as a percentage of the diameter, so the line
    // thickness scales proportionally along with the ring itself.
    [JsonPropertyName("thickness")]
    public int Thickness { get; set; } = 7;

    [JsonPropertyName("trackColor")]
    public string TrackColor { get; set; } = "rgba(255,255,255,0.15)";

    public RadialState()
    {
        Direction = "cw";
    }
}

// ------------------------------------------------------------
// README written to config/ on first run
// ------------------------------------------------------------
// I keep this here instead of a separate text file so there's only
// one thing to edit when I add a command. It gets written out once
// on first run and then left alone, so if I update this later, I'll
// need to delete an existing config/README.txt for the new copy to
// land (a stale README is a lot less annoying than silently
// overwriting something a friend has scribbled notes into).

static class ReadmeText
{
    public const string Content = """
    ============================================================
     Ad Break Timer, Setup and Command Guide

     Made by Kaydee.Codes (https://kaydee.codes/)
     Free to use, no data collected, ever.
    ============================================================

    WHAT THIS IS
    ------------
    A tiny local web server (this exe) that hosts two OBS overlay
    pages and lets me control them with simple web requests, built
    around Streamer.bot's "Web Request" or "Execute HTTP Request"
    action.

    FIRST TIME SETUP
    -----------------
    1. Run the exe once. It prints two URLs to the console, e.g.
           http://localhost:8085/bar/
           http://localhost:8085/radial/
    2. In OBS, add a Browser Source, paste in whichever URL I want
       (bar, radial, or both as separate sources), and set the Browser
       Source's Width/Height to whatever I like. Both overlays are
       fully responsive and just fill whatever size they're given.
       There's no fixed resolution to match.
    3. Tick "Shutdown source when not visible" OFF so the timer keeps
       running in the background between ad breaks.

    ABOUT THE PORT
    ---------------
    Saved in config/settings.json. Every launch tries that saved port
    first. If something else is already using it, it automatically
    tries the next port up, tells me in the console, and re-saves the
    new port, so it keeps using that same one from then on unless it
    becomes busy again. To change it manually, edit
    config/settings.json and restart.

    CONSOLE DEBUG LEVEL
    ---------------------
    Also saved in config/settings.json as "debugLevel". Controls how
    much the console window shows.

      1  Normal operation (default, this is what I ship to a friend).
         Shows only real events: timer started/paused/stopped, config
         changes, errors, and when a countdown naturally hits zero.
         The 5x/sec status polling from the overlay pages stays
         completely silent.

      2  Full diagnostics. Everything in level 1, plus the raw command
         and query string behind every real event, the full resulting
         state after each change, config file load/parse failures,
         and full exception stack traces on errors.

      3  Everything. Everything in level 2, plus every single HTTP
         request the server receives, including the constant status
         polling. Very noisy, only useful for debugging the polling
         itself.

    Three ways to change it:
      - Edit config/settings.json ("debugLevel": 1, 2, or 3) and restart.
      - While it's running, visit (or GET from Streamer.bot):
            http://localhost:8085/debug/set?level=2
        Takes effect immediately and is saved for next time too.
      - Start it once at a higher level without saving that choice:
            AdBreakTimer.exe --debug 3

    THE TWO OVERLAYS
    -----------------
    /bar/     A bar pinned to the very bottom of whatever size OBS
              Browser Source I give it, spanning the full width. Fills
              or drains left to right as time passes. Height is
              configurable (default 5px) via cmd=setbarheight.
    /radial/  A circular progress ring, centred in whatever size OBS
              Browser Source I give it. Sweeps clockwise or
              anticlockwise as time passes. Diameter is a percentage
              of the viewport's smaller side (default 60), so it
              genuinely grows and shrinks as the source is resized,
              configurable via cmd=setsize.

    Both pages are fully responsive, there's no fixed canvas size to
    match. Resize the OBS Browser Source to whatever I want and the
    overlay just fills it.

    Each has its OWN config file (config/bar.json / config/radial.json)
    and its OWN API, so I can run totally different timers on each, or
    just use whichever style I like in OBS and ignore the other.

    THE EASY WAY: ONE SHOT "go" COMMAND
    --------------------------------------
    This is the one I use from Streamer.bot most of the time. It sets
    whatever's given and starts the countdown immediately:

        /bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

        /radial/api?cmd=go&t=00:03:00&color=%23ff0000&dir=cw

    Parameters (all optional except t):
        t         time, hh:mm:ss or mm:ss or raw seconds (required)
        color     the running colour, e.g. %2300ff00 (that's #00ff00 URL-encoded)
        finish    the colour it turns when the countdown hits zero
        dir       bar: drain | fill        radial: cw | ccw
        flash     on | off, whether to flash when finished
        flashfor  seconds to flash before auto-clearing (default 30)

    WHAT HAPPENS WHEN A COUNTDOWN HITS ZERO
    ------------------------------------------
    The bar/ring immediately snaps to FULL (the whole bar or the whole
    ring) in the finish colour and flashes for flashDuration seconds
    (30 by default). After that, if nothing else has told it what to
    do next, it automatically clears itself back to idle (invisible),
    so it's never left stuck lit up if Streamer.bot is a little late
    sending the next command. Change the duration any time with:
        cmd=setflashduration&v=45

    EXAMPLE STREAMER.BOT SETUP FOR AN AD-BREAK FLOW
    ----------------------------------------------------
    When a Twitch ad break finishes (next one in about an hour):
        GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

    When Streamer.bot detects a 3 minute ad break starting:
        GET http://localhost:8085/bar/api?cmd=go&t=00:03:00&color=%23ff0000&dir=drain

    When that ad break's Streamer.bot timer ends (back to normal):
        GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

    If Streamer.bot's own countdown finishes before the next "go" goes
    out (mid ad-break and the timer hasn't been re-armed yet), the
    bar/ring flashes full and red for flashDuration seconds and then
    clears itself automatically, so it's never left looking broken or
    stuck half-finished.

    ALL COMMANDS
    ------------
    Every command below works on both /bar/api and /radial/api unless
    marked otherwise. Replace ... with an actual value.

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

    Colours need to be URL-encoded if using #: %23 = #, e.g. #ff0000 becomes %23ff0000

    TROUBLESHOOTING
    -----------------
    - Nothing shows in OBS: check the console window is still open and
      showing "running", and double check the URL/port match.
    - Bar/ring never seems to move: make sure cmd=go was called, or
      cmd=settime followed by cmd=start. Status alone won't start it.
    - Want to hand-edit defaults: edit config/bar.json or
      config/radial.json while the exe is NOT running, then start it.

    ============================================================
    Made by Kaydee.Codes, https://kaydee.codes/
    Free to use, no data collected, ever.
    ============================================================
    """;
}

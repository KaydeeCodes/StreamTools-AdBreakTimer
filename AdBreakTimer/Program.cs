// ============================================================
// Ad Break Timer: Lightweight Overlay Web Server
//
// This is my own rebuild of a bigger timer and overlay tool I made a
// while back. I stripped it down to just the part I actually use day
// to day: a bar and a radial ring that I drive from Streamer.bot
// during Twitch ad breaks, plus enough config that a friend can run
// it without me holding their hand.
//
// It's one exe. No install, no external files to lose, and no
// database. Everything it needs (the two HTML overlay pages) is
// baked in as embedded resources at build time, and the only things
// it writes to disk are small JSON files in a "config" folder next
// to itself.
//
//   http://localhost:<port>/bar/ -> bottom progress bar overlay
//   http://localhost:<port>/radial/ -> circular progress ring overlay
//
// Both pages poll their own /api endpoint 5 times a second and just
// redraw themselves from whatever state comes back. That polling
// model is deliberate: it means OBS can load or unload the browser
// source at any point, and it will just pick back up from wherever
// the timer actually is, with no reconnection needed on either
// side.
//
// config/README.txt gets written next to the exe on first run, and
// has the full command list, so I don't need to keep this comment
// block in sync with every feature I add later.
//
// Made by Kaydee.Codes (https://kaydee.codes/)
// Free to use, no data collected, ever.
// ============================================================

using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// The version lives in one place, the <Version> tag in the csproj,
// and gets read back out here at runtime. I used to have "v1.1.0"
// typed out by hand in four different spots (the log header, both
// console banners, the landing page), which is exactly the kind of
// thing that's guaranteed to go stale the next time I bump a
// version and forget one of them.
string appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

// ------------------------------------------------------------
// Paths and config folder
// ------------------------------------------------------------
// Everything lives next to the exe, not somewhere in AppData or
// Program Files. That's on purpose: if I ever want to wipe a test
// setup or hand someone a fresh copy, I just delete the folder. No
// hidden state anywhere else on the machine.

string exeDir = AppContext.BaseDirectory;
string configDir = Path.Combine(exeDir, "config");
string settingsFile = Path.Combine(configDir, "settings.json");
string barFile = Path.Combine(configDir, "bar.json");
string radialFile = Path.Combine(configDir, "radial.json");
string readmeFile = Path.Combine(configDir, "README.txt");
string streamerBotFile = Path.Combine(configDir, "streamerbot-setup.txt");
string logFile = Path.Combine(configDir, "latest.log");

Directory.CreateDirectory(configDir);

// Fresh log every run, overwritten each launch. If a friend hits an
// issue I can just ask for this one file instead of them trying to
// scroll back through a console window they've probably already
// closed. It captures more detail than the console shows by default,
// see Log() and LogSegments() further down, so it's still useful
// even if nobody thought to turn debugLevel up before the problem
// happened.
try
{
    File.WriteAllText(logFile, $"Ad Break Timer v{appVersion} log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{new string('=', 60)}{Environment.NewLine}");
}
catch
{
    // Can't write it, permissions or a full disk maybe. Not worth
    // crashing the whole app over, the console still works fine
    // either way.
}

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};

// ------------------------------------------------------------
// Load settings (port + debug level + setup wizard state)
// ------------------------------------------------------------

AppSettings settings = LoadJson<AppSettings>(settingsFile, jsonOpts) ?? new AppSettings();
settings.DebugLevel = Math.Clamp(settings.DebugLevel, 1, 3);

// I let myself override the debug level for a single run without
// touching the saved config, e.g., AdBreakTimer.exe --debug 3
// Useful when I just want to see everything once without leaving
// verbose logging on for next time.
for (int i = 0; i < args.Length; i++)
{
    bool isDebugFlag = args[i] is "--debug" or "-d";
    if (isDebugFlag && i + 1 < args.Length && int.TryParse(args[i + 1], out int overrideLevel))
        settings.DebugLevel = Math.Clamp(overrideLevel, 1, 3);
}

// The wizard runs automatically the first time this exe is ever run
// on a machine, and again any time it's launched with --setup. I
// keep this separate from "does settings.json exist" so a friend can
// redo the wizard later (e.g. Twitch changed their ad timing) without
// having to delete their whole config folder and lose the port
// they've already got wired up in OBS.
bool forceSetup = args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase));
bool runWizard = !settings.SetupComplete || forceSetup;

// config/README.txt is separate from the wizard entirely, if it's
// missing (fresh install, or someone deleted it) I just rewrite it.
if (!File.Exists(readmeFile))
    File.WriteAllText(readmeFile, ReadmeText.Content);

// ------------------------------------------------------------
// First-time setup wizard, part 1: pick a port
// ------------------------------------------------------------
// This runs before I bind the port so the wizard's answer can
// actually be used for the real bind attempt straight away, instead
// of binding once automatically and then having to rebind.

if (runWizard)
{
    try
    {
        RunWizardWelcomeAndPort();
    }
    catch (Exception ex)
    {
        PauseOnStartupError("Something went wrong during setup.", ex);
        return;
    }
}

void RunWizardWelcomeAndPort()
{
    try { Console.Clear(); } catch { /* output's probably redirected, nothing to clear */ }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("========================================================");
    Console.WriteLine($"   Welcome to Ad Break Timer  (v{appVersion})");
    Console.WriteLine("========================================================");
    Console.ResetColor();
    Console.WriteLine();
    if (settings.SetupComplete)
    {
        Console.WriteLine("Running setup again. This won't touch anything already");
        Console.WriteLine("running on your overlays, it'll just ask the same questions");
        Console.WriteLine("again in case anything's changed.");
    }
    else
    {
        Console.WriteLine("Hi! Let's get this set up together. It's four short steps and");
        Console.WriteLine("takes about two minutes. There's nothing to install and nothing");
        Console.WriteLine("to type unless you want to, pressing Enter always picks the");
        Console.WriteLine("normal, recommended answer.");
        Console.WriteLine();
        Console.WriteLine("Handy to have ready, though not required: OBS open, and Twitch's");
        Console.WriteLine("Creator Dashboard open in a browser tab if you want to check your");
        Console.WriteLine("exact ad break timing (Dashboard > Monetization > Ads Manager). If");
        Console.WriteLine("you don't know it offhand, that's completely fine, typical numbers");
        Console.WriteLine("work well and you can update them later by running this exe with");
        Console.WriteLine("--setup.");
    }
    Console.WriteLine();
    Console.WriteLine("STEP 1 of 4: Pick a port");
    Console.WriteLine("--------------------------");
    Console.WriteLine("A \"port\" is just a technical address this app uses on your own");
    Console.WriteLine("computer, nothing gets sent over the internet. Unless you already");
    Console.WriteLine("know you need a specific one, just press Enter here.");
    Console.Write("Press Enter to continue (recommended), or type a port number: ");
    string? input = Console.ReadLine();

    if (!string.IsNullOrWhiteSpace(input))
    {
        if (int.TryParse(input.Trim(), out int chosenPort) && chosenPort is > 0 and <= 65535)
        {
            settings.Port = chosenPort;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("That didn't look like a valid port number, no problem, I'll pick one automatically.");
            Console.ResetColor();
        }
    }
}

// ------------------------------------------------------------
// Bind to a port
// ------------------------------------------------------------
// I try the port I used last time (or the wizard's answer) first. If
// something else has grabbed it, I walk upward until one binds and
// then save that new port back to settings.json, so next launch
// tries the working one first instead of colliding again.

HttpListener listener;
int boundPort;
try
{
    (listener, boundPort) = StartListenerAutoPort(settings.Port);
}
catch (Exception ex)
{
    PauseOnStartupError("Couldn't start the web server.", ex);
    return;
}

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

// ------------------------------------------------------------
// Start serving requests, in the background
// ------------------------------------------------------------
// This has to start running before the wizard, not after it. The
// wizard opens the overlay URL in a browser during its "test it"
// step, and if nothing is actually listening for and answering
// requests yet, that request just sits there forever waiting for a
// response that never comes, which is exactly the "page loads
// forever" bug I hit the first time I wired this up. Running this as
// a background task means it keeps answering requests on its own
// thread pool thread the whole time the wizard is blocked on
// Console.ReadLine() further down.
Task serverTask = RunServerLoop();

async Task RunServerLoop()
{
    while (true)
    {
        HttpListenerContext ctx = await listener.GetContextAsync();
        _ = HandleRequest(ctx);
    }
}

// ------------------------------------------------------------
// First-time setup wizard, part 2: everything after the port is bound
// ------------------------------------------------------------

if (runWizard)
{
    try
    {
        RunWizardAfterBinding(baseUrl);
    }
    catch (Exception ex)
    {
        PauseOnStartupError("Something went wrong during setup.", ex);
        return;
    }
}

PrintRunningBanner();

void PrintRunningBanner()
{
    List<(string Name, string Url)> activeOverlays = settings.OverlayChoice switch
    {
        "radial" => [("Radial overlay", $"{baseUrl}/radial/")],
        "both" => [("Bar overlay", $"{baseUrl}/bar/"), ("Radial overlay", $"{baseUrl}/radial/")],
        _ => [("Bar overlay", $"{baseUrl}/bar/")]
    };

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("========================================================");
    Console.WriteLine($"   Ad Break Timer is running  (v{appVersion})");
    Console.WriteLine("========================================================");
    Console.ResetColor();
    foreach ((string name, string url) in activeOverlays)
        Console.WriteLine($"  {name}: {url}");
    Console.WriteLine();
    Console.WriteLine("  This window needs to stay open while you stream. To stop,");
    Console.WriteLine("  just close it.");
    Console.WriteLine();

    // Level 1 (the default, what a friend actually sees) stays short
    // on purpose, this is the exact wall of technical detail that
    // confused a non-technical beta tester the first time round.
    // Anyone who's turned debug logging up has already opted into
    // more detail, so they get the full picture instead.
    if (settings.DebugLevel >= 2)
    {
        Console.WriteLine($"  Bar API         : {baseUrl}/bar/api?cmd=...");
        Console.WriteLine($"  Radial API      : {baseUrl}/radial/api?cmd=...");
        Console.WriteLine($"  Config folder   : {configDir}");
        Console.WriteLine($"  Debug level     : {settings.DebugLevel}  (1 = normal, 2 = full diagnostics, 3 = everything incl. polling)");
        Console.WriteLine($"                    change anytime: {baseUrl}/debug/set?level=1|2|3");
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("  Need the full command list or advanced options? See");
        Console.WriteLine($"  {readmeFile}");
        Console.WriteLine();
    }

    Console.WriteLine("  Having an issue? Everything that happens is saved to:");
    Console.WriteLine($"  {logFile}");
    Console.WriteLine();
    Console.WriteLine("  Change your Twitch ad timing or redo setup any time:");
    Console.WriteLine("  AdBreakTimer.exe --setup");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Made by Kaydee.Codes (https://kaydee.codes/), free to use, no data collected, ever.");
    Console.ResetColor();
    Console.WriteLine();
}

// The actual request loop is already running in the background (see
// RunServerLoop above), this just keeps the main thread, and
// therefore the whole process, alive so it doesn't exit the moment
// startup finishes. serverTask never completes under normal
// operation since RunServerLoop loops forever, so this effectively
// waits until Ctrl+C kills the process.
await serverTask;

// ------------------------------------------------------------
// First-time setup wizard, part 2 (steps 2 through 4)
// ------------------------------------------------------------
// Part 1 (the port question) runs before the port is actually bound,
// see RunWizardWelcomeAndPort further up. Everything else needs a
// working baseUrl, so it happens here instead, right after binding.
//
// This is a local function (not static) since it needs to read and
// update settings and save it back out, same reasoning as Log and
// LogSegments further down.

void RunWizardAfterBinding(string baseUrl)
{
    Console.WriteLine();
    Console.WriteLine("STEP 2 of 4: Choose your overlay style");
    Console.WriteLine("-----------------------------------------");

    // Seed both overlays with a running hour so there's something to
    // actually look at the moment either one is added in OBS. An
    // empty transparent page looks identical whether it's working
    // perfectly or completely broken, this removes that doubt.
    SeedOneHourTimer<BarState>(barFile, jsonOpts);
    SeedOneHourTimer<RadialState>(radialFile, jsonOpts);

    Console.WriteLine("There are two visual styles to choose from:");
    Console.WriteLine("  1) Bar, a coloured bar along the bottom of the screen (recommended,");
    Console.WriteLine("     works well for most people, press Enter for this)");
    Console.WriteLine("  2) Radial, a circular ring instead");
    Console.WriteLine("  3) Both, if you want to try them side by side");
    Console.Write("Type 1, 2, or 3, or just press Enter for the bar: ");
    string overlayInput = (Console.ReadLine() ?? "").Trim();

    string overlayChoice = overlayInput switch
    {
        "2" => "radial",
        "3" => "both",
        _ => "bar"
    };
    settings.OverlayChoice = overlayChoice;

    List<(string Name, string Url)> overlays = overlayChoice switch
    {
        "radial" => [("Radial", $"{baseUrl}/radial/")],
        "both" => [("Bar", $"{baseUrl}/bar/"), ("Radial", $"{baseUrl}/radial/")],
        _ => [("Bar", $"{baseUrl}/bar/")]
    };

    Console.WriteLine();
    Console.WriteLine("Good, it's already running a test countdown so there's something");
    Console.WriteLine("to see straight away. Here's what you'll need:");
    foreach ((string name, string url) in overlays)
        Console.WriteLine($"  {name}: {url}");
    Console.WriteLine();
    Console.WriteLine("Now add it in OBS:");
    Console.WriteLine("  1. In OBS, click the + under Sources, choose \"Browser\".");
    Console.WriteLine("  2. Give it any name and click OK.");
    Console.WriteLine("  3. Paste the link above into the URL box.");
    Console.WriteLine("  4. Width/Height don't matter, it fills whatever size you give it.");
    Console.WriteLine("  5. Untick \"Shutdown source when not visible\", so it keeps running");
    Console.WriteLine("     even when this source isn't the one on screen.");
    Console.WriteLine("  6. Click OK.");

    Console.WriteLine();
    Console.WriteLine("STEP 3 of 4: Make sure it's working");
    Console.WriteLine("---------------------------------------");
    Console.Write("Press Enter and I'll open it in your web browser so you can check it's moving (or type skip): ");
    string testInput = (Console.ReadLine() ?? "").Trim();
    if (!testInput.Equals("skip", StringComparison.OrdinalIgnoreCase))
    {
        foreach ((_, string url) in overlays)
            OpenInBrowser(url);
        Console.WriteLine();
        Console.WriteLine("A new browser tab should have opened showing a green bar or ring");
        Console.WriteLine("slowly counting down. That confirms it's working, OBS will show");
        Console.WriteLine("exactly the same thing.");
        Console.Write("Press Enter once you've seen it: ");
        Console.ReadLine();
    }

    Console.WriteLine();
    Console.WriteLine("STEP 4 of 4: Set up Streamer.bot");
    Console.WriteLine("-----------------------------------");
    Console.WriteLine("Last step. I need two numbers about how ads work on your Twitch");
    Console.WriteLine("channel, then I'll write out the exact commands to paste into");
    Console.WriteLine("Streamer.bot myself, nothing to figure out by hand.");
    Console.WriteLine();
    Console.WriteLine("Don't know your exact numbers? That's completely fine, just press");
    Console.WriteLine("Enter twice and I'll use typical values that work for most");
    Console.WriteLine("streamers. You can always run this exe with --setup later once you");
    Console.WriteLine("know the real numbers.");
    Console.WriteLine();
    Console.WriteLine("Answers can be typed as minutes:seconds (like 3:00) or just a");
    Console.WriteLine("number of seconds, whichever's easier.");
    Console.WriteLine();

    int adBreakSeconds = AskDurationSeconds(
        "How long does one ad break usually last? (press Enter for 3:00): ", 180);
    int adFreeSeconds = AskDurationSeconds(
        "How long between ad breaks, your normal streaming time? (press Enter for 1:00:00): ", 3600);

    settings.AdBreakSeconds = adBreakSeconds;
    settings.AdFreeSeconds = adFreeSeconds;

    string streamerBotText = BuildStreamerBotCommands(baseUrl, adBreakSeconds, adFreeSeconds, overlayChoice);
    File.WriteAllText(streamerBotFile, streamerBotText);

    Console.WriteLine();
    Console.WriteLine("Here are your commands, ready to copy and paste:");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(streamerBotText);
    Console.ResetColor();

    settings.SetupComplete = true;
    SaveJson(settings, settingsFile, jsonOpts);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("All done! You don't need to remember any of that, it's saved here:");
    Console.ResetColor();
    Console.WriteLine($"  {streamerBotFile}");
    Console.WriteLine();
    Console.WriteLine("Open that file any time you're setting up Streamer.bot, and copy");
    Console.WriteLine("the web addresses in it into \"Fetch URL\" actions there. A full");
    Console.WriteLine("walkthrough with pictures is on the GitHub page for this project.");
    Console.WriteLine();
    Console.WriteLine("From here, just leave this window open while you stream. Closing");
    Console.WriteLine("it stops the overlay. To change any of these answers later, run");
    Console.WriteLine("this exe with --setup.");
    Console.WriteLine();
    Console.Write("Press Enter to finish and start the overlay properly...");
    Console.ReadLine();
}

// Prints a friendly message instead of a raw stack trace, and waits
// for the person to press Enter before the window closes. Without
// this, a startup error just flashes the console shut instantly,
// which is a genuinely bad experience for anyone who isn't expecting
// to read a crash log.
static void PauseOnStartupError(string message, Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine();
    Console.WriteLine(message);
    Console.WriteLine(ex.Message);
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("Press Enter to close this window.");
    Console.ReadLine();
}

// Loads (or creates) an overlay state and forces it into a running,
// green, 1 hour countdown. Used by the setup wizard so there's
// always something visible to test against straight away.
static void SeedOneHourTimer<T>(string file, JsonSerializerOptions opts) where T : OverlayState, new()
{
    T state = LoadJson<T>(file, opts) ?? new T();
    state.Remaining = 3600;
    state.InitialTime = 3600;
    state.Status = "running";
    state.Color = "#00ff00";
    state.FinishColor = "#ff0000";
    state.LastTick = DateTime.UtcNow;
    state.FinishedAt = null;
    SaveJson(state, file, opts);
}

// Best effort only. If this fails for whatever reason (no default
// browser configured, running under some restricted account, and so
// on) the person can still just click the URL themselves, so I don't
// want a failure here to take down the wizard.
static void OpenInBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // Not the end of the world, they can open it manually.
    }
}

// Keeps asking until it gets a valid duration, reusing the same
// mm:ss/seconds parsing the rest of the app already uses, so there's
// only one time format to learn across the whole tool.
static int AskDurationSeconds(string prompt, int defaultSeconds)
{
    while (true)
    {
        Console.Write(prompt);
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return defaultSeconds;

        int seconds = HmsToSecs(input.Trim());
        if (seconds > 0) return seconds;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("I couldn't read that, try mm:ss (like 3:00) or just a number of seconds.");
        Console.ResetColor();
    }
}

// Builds the ready to paste Streamer.bot setup based on the wizard's
// answers. This gets printed once and also saved to
// config/streamerbot-setup.txt so it's not lost the moment the
// console scrolls past it. overlayChoice is "bar", "radial", or
// "both", matching what they picked in step 2, so the commands are
// exactly right for their setup with nothing left to edit by hand.
static string BuildStreamerBotCommands(string baseUrl, int adBreakSeconds, int adFreeSeconds, string overlayChoice)
{
    string adBreakTime = SecsToHms(adBreakSeconds);
    string adFreeTime = SecsToHms(adFreeSeconds);
    int delayMs = (adBreakSeconds + 5) * 1000;

    string sections = overlayChoice switch
    {
        "radial" => BuildOverlaySection("radial", "Radial", baseUrl, adBreakTime, adFreeTime, delayMs),
        "both" => BuildOverlaySection("bar", "Bar", baseUrl, adBreakTime, adFreeTime, delayMs)
                  + "\n"
                  + BuildOverlaySection("radial", "Radial", baseUrl, adBreakTime, adFreeTime, delayMs),
        _ => BuildOverlaySection("bar", "Bar", baseUrl, adBreakTime, adFreeTime, delayMs)
    };

    return $$"""
    ============================================================
     Ready-made Streamer.bot commands
     Generated by the Ad Break Timer setup wizard
    ============================================================

    Based on what you told me: ad breaks last about {{adBreakTime}},
    with about {{adFreeTime}} of normal streaming in between.

    Paste the web addresses below into a "Fetch URL" action in
    Streamer.bot. No editing needed, they're ready to go as they are.

    {{sections}}
    A little drift between Streamer.bot's Delay timer and the actual
    countdown doesn't matter, the overlay flashes and clears itself
    automatically once it hits zero either way.

    Full guide with pictures: see the README on GitHub, or
    config/README.txt next to this exe for the full command list.
    ============================================================
    """;
}

// One overlay's worth of the two Streamer.bot actions (ad break
// starting, ad break ending). Pulled out into its own function since
// the "both" overlay choice needs this twice with different paths.
static string BuildOverlaySection(string apiPath, string label, string baseUrl, string adBreakTime, string adFreeTime, int delayMs) => $$"""
    {{label.ToUpperInvariant()}} OVERLAY
    -----------------
    ACTION 1: When an ad break STARTS
    Trigger: Twitch > Ads > Ad Run
    Fetch URL:
        {{baseUrl}}/{{apiPath}}/api?cmd=go&t={{adBreakTime}}&color=%23ff0000&dir=drain

    Then add a Delay action for {{delayMs}}ms (a few seconds longer than
    the ad break itself, as a buffer), followed by an Action step that
    calls ACTION 2 below.

    ACTION 2: When an ad break ENDS (chained from Action 1, it
    doesn't need a trigger of its own)
    Fetch URL:
        {{baseUrl}}/{{apiPath}}/api?cmd=go&t={{adFreeTime}}&color=%2300ff00&finish=%23ff0000&dir=drain

    """;

async Task HandleRequest(HttpListenerContext ctx)
{
    HttpListenerRequest req = ctx.Request;
    HttpListenerResponse res = ctx.Response;
    string path = req.Url?.AbsolutePath ?? "/";

    // Level 3 only. This is deliberately the noisiest possible log
    // line: every single request, including the 5x/sec status
    // polling. I only want this on when I'm chasing something in the
    // polling itself.
    Log("[HTTP]", $"{req.HttpMethod} {path}{req.Url?.Query}", ConsoleColor.DarkGray, 3);

    try
    {
        // CORS is wide open and there's no caching. This only ever binds to
        // localhost, so I'm not worried about exposing it to randoms,
        // and OBS/browsers need fresh state on every poll anyway.
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");

        if (path is "/" or "")
        {
            await SendText(res, 200, "text/html; charset=utf-8", IndexHtml(baseUrl, appVersion));
            return;
        }

        if (path.Equals("/debug/set", StringComparison.OrdinalIgnoreCase))
        {
            string levelParam = req.QueryString["level"] ?? "";
            if (int.TryParse(levelParam, out int newLevel) && newLevel is >= 1 and <= 3)
            {
                settings.DebugLevel = newLevel;
                SaveJson(settings, settingsFile, jsonOpts);
                Log("[DEBUG]", $"Debug level changed to {newLevel}", ConsoleColor.Cyan);
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
            Log("[BAR]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan);
            await SendEmbedded(res, "bar.html");
            return;
        }

        if (path is "/radial" or "/radial/")
        {
            Log("[RADIAL]", "Overlay connected (OBS browser source loaded the page)", ConsoleColor.DarkCyan);
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
        Log("[ERROR]", detail, ConsoleColor.Red);
        try { await SendText(res, 500, "text/plain", ex.Message); } catch { /* connection's probably already gone, nothing to do */ }
    }
}

// ------------------------------------------------------------
// Console logging (and the log file)
// ------------------------------------------------------------
// level 1 = normal (default), 2 = full diagnostics, 3 = everything,
// including polling. Console output only shows if settings.DebugLevel
// is at least that level. This is a local function (not a static
// one) on purpose, so it can read the live settings.DebugLevel value
// without me having to thread it through every call site.
//
// The log file is separate from that and always captures level 1 and
// 2 detail regardless of what the console is set to, but never level
// 3, the constant status polling would make a file someone emails me
// enormous for no real benefit. That way the file is always useful
// for troubleshooting even if nobody thought to turn debugLevel up
// before the problem happened.

void Log(string tag, string message, ConsoleColor color, int level = 1)
{
    WriteToLogFile(tag, message, level);
    if (settings.DebugLevel < level) return;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = color;
    Console.Write($"{tag} ");
    Console.ResetColor();
    Console.WriteLine(message);
}

// Same idea as Log, but for a line built out of several pieces that
// each need their own color, mainly so a color value like "#ff0000"
// can actually print in red instead of sitting there as plain text.
// A segment with color == null just uses baseColor, same as normal.
void LogSegments(string tag, ConsoleColor baseColor, int level, params (string text, ConsoleColor? color)[] segments)
{
    WriteToLogFile(tag, string.Concat(segments.Select(s => s.text)), level);
    if (settings.DebugLevel < level) return;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = baseColor;
    Console.Write($"{tag} ");
    foreach ((string text, ConsoleColor? color) in segments)
    {
        Console.ForegroundColor = color ?? baseColor;
        Console.Write(text);
    }
    Console.ResetColor();
    Console.WriteLine();
}

void WriteToLogFile(string tag, string message, int level)
{
    if (level > 2) return;
    try
    {
        File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] {tag} {message}{Environment.NewLine}");
    }
    catch
    {
        // Same reasoning as the initial log file write, never worth
        // crashing over.
    }
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
// than I'll ever need, it's just a sane upper bound, so this can't
// loop forever on a genuinely broken machine.

static (HttpListener listener, int port) StartListenerAutoPort(int startPort)
{
    int port = startPort > 0 ? startPort : 37000;
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
    throw new Exception("Could not find a free port after 50 attempts. That's almost certainly not a port problem; something else is wrong.");
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
    // with dots instead of slashes; that's a .NET convention, not
    // something I chose.
    Assembly asm = Assembly.GetExecutingAssembly();
    string resourceName = $"AdBreakTimer.Web.{fileName}";
    await using Stream? stream = asm.GetManifestResourceStream(resourceName);
    if (stream == null)
    {
        await SendText(res, 500, "text/plain", $"Embedded resource not found: {resourceName}. This means the exe wasn't built correctly.");
        return;
    }
    using var reader = new StreamReader(stream);
    string html = await reader.ReadToEndAsync();
    await SendText(res, 200, "text/html; charset=utf-8", html);
}

// This is just a tiny landing page, so hitting the base URL in a
// browser shows something useful instead of a 404. Not meant to be
// pretty, just informative enough that I remember what the URLs are
// six months from now.
static string IndexHtml(string baseUrl, string appVersion) => $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Ad Break Timer</title>
<style>body{font-family:sans-serif;background:#111;color:#eee;padding:2rem;}
a{color:#7dd3fc;} code{background:#222;padding:2px 6px;border-radius:4px;}</style>
</head><body>
<h2>Ad Break Timer is running (v{{appVersion}})</h2>
<p>Add these as OBS Browser Sources:</p>
<ul>
<li><a href="{{baseUrl}}/bar/">{{baseUrl}}/bar/</a></li>
<li><a href="{{baseUrl}}/radial/">{{baseUrl}}/radial/</a></li>
</ul>
<p>Control them with URL commands, e.g.,<br>
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
// and I both tend to type hh:mm:ss, so that's the primary format;
// the others are just convenience.
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

// Accepts hex (#rrggbb, #rgb, with or without alpha, or even without
// the leading # since %23 in a URL is easy to forget), a chunk of CSS
// named colours, rgb()/rgba()/hsl()/hsla(), or literally "transparent".
// I URL-decode first since %23ff0000 is how a # actually arrives over
// the wire.
static string? ParseColor(string value)
{
    value = Uri.UnescapeDataString(value).Trim();
    if (value is "" or "null") return null;
    if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "transparent";

    // Someone typed ff0000 instead of %23ff0000. I just add the # back
    // in rather than making them get the encoding exactly right.
    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[0-9a-fA-F]{3,8}$"))
        value = "#" + value;

    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9a-fA-F]{3,8}$")) return value;

    // Not validated against a real list of CSS color names, if it's
    // not a real one the browser just ignores it. Good enough for me.
    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z]{2,30}$")) return value;

    if (value.StartsWith("rgb(") || value.StartsWith("rgba(") || value.StartsWith("hsl(") || value.StartsWith("hsla("))
        return value;

    return null;
}

static bool ParseBool(string value, bool fallback) => value.ToLowerInvariant() switch
{
    "on" or "1" or "true" or "yes" => true,
    "off" or "0" or "false" or "no" => false,
    _ => fallback
};

// ------------------------------------------------------------
// Colour to console colour mapping
// ------------------------------------------------------------
// This whole block exists for one reason: when the console logs
// "colour set to #ff0000", I want that #ff0000 to actually print in
// red, not just sit there as plain text. The console only has 16
// colours to work with though, so I parse whatever CSS color came
// in down to RGB and then pick whichever of the 16 is closest.
//
// The actual colour tables (ColorTables.NamedColors and
// ColorTables.ConsolePalette) live down with the other classes near
// the bottom of the file, not here. "static readonly" only works on
// real class fields, not on local variables inside top-level
// statements. C# doesn't allow that combination, even though
// nothing stops me writing it, and it just silently breaks parsing
// further down the file in a very confusing way. Learned that one
// the hard way.

// Converts whatever ParseColor accepted into actual RGB bytes so I
// can compare it against the console palette. Returns null for
// anything I can't figure out (including "transparent", there's no
// RGB for that), and the caller just falls back to a plain colour.
static (byte R, byte G, byte B)? ParseCssColorToRgb(string value)
{
    value = value.Trim();

    if (ColorTables.NamedColors.TryGetValue(value, out (byte R, byte G, byte B) named)) return named;

    if (value.StartsWith('#'))
    {
        string hex = value[1..];
        // Expand shorthand #rgb / #rgba to full length by doubling each digit.
        if (hex.Length is 3 or 4)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));

        if (hex.Length >= 6)
        {
            try
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return (r, g, b);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    System.Text.RegularExpressions.Match rgbMatch = System.Text.RegularExpressions.Regex.Match(
        value, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
    if (rgbMatch.Success)
    {
        byte r = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[1].Value), 0, 255);
        byte g = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[2].Value), 0, 255);
        byte b = (byte)Math.Clamp(int.Parse(rgbMatch.Groups[3].Value), 0, 255);
        return (r, g, b);
    }

    System.Text.RegularExpressions.Match hslMatch = System.Text.RegularExpressions.Regex.Match(
        value, @"hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%");
    if (hslMatch.Success)
    {
        double h = double.Parse(hslMatch.Groups[1].Value);
        double s = double.Parse(hslMatch.Groups[2].Value) / 100.0;
        double l = double.Parse(hslMatch.Groups[3].Value) / 100.0;
        return HslToRgb(h, s, l);
    }

    return null;
}

static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
{
    h = ((h % 360) + 360) % 360;
    double c = (1 - Math.Abs(2 * l - 1)) * s;
    double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
    double m = l - c / 2;

    (double r1, double g1, double b1) = h switch
    {
        < 60 => (c, x, 0.0),
        < 120 => (x, c, 0.0),
        < 180 => (0.0, c, x),
        < 240 => (0.0, x, c),
        < 300 => (x, 0.0, c),
        _ => (c, 0.0, x)
    };

    return (
        (byte)Math.Round((r1 + m) * 255),
        (byte)Math.Round((g1 + m) * 255),
        (byte)Math.Round((b1 + m) * 255)
    );
}

// Returns null if I can't work out an RGB value for this colour
// (unrecognised name, "transparent", or just malformed), in which
// case the caller falls back to whatever colour it was already using.
static ConsoleColor? NearestConsoleColor(string cssColor)
{
    (byte R, byte G, byte B)? rgb = ParseCssColorToRgb(cssColor);
    if (rgb == null) return null;
    (byte r, byte g, byte b) = rgb.Value;

    ConsoleColor closest = ConsoleColor.White;
    int closestDistance = int.MaxValue;
    foreach ((ConsoleColor color, byte pr, byte pg, byte pb) in ColorTables.ConsolePalette)
    {
        int dr = r - pr, dg = g - pg, db = b - pb;
        int distance = dr * dr + dg * dg + db * db;
        if (distance < closestDistance)
        {
            closestDistance = distance;
            closest = color;
        }
    }
    return closest;
}

// Little shorthand for building a coloured segment to hand to
// LogSegments, so a call site can just write ColorSegment(state.Color)
// instead of repeating the "text plus its own nearest console colour"
// pair every time.
static (string text, ConsoleColor? color) ColorSegment(string cssColor) => (cssColor, NearestConsoleColor(cssColor));

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
    if (s is { Status: "running", LastTick: not null })
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
            // This used to just set LastTick = DateTime.UtcNow here,
            // which is a real bug: Math.Floor above always throws away
            // some fraction of a second (up to just under 1), and
            // resetting the reference point to "now" instead of "the
            // last whole second I actually counted" means that
            // fraction is gone for good, every single cycle. On a
            // long countdown, with polling happening 5 times a
            // second, that adds up to a real, noticeable overrun. By
            // advancing from the old LastTick by exactly the whole
            // seconds I just subtracted, the leftover fraction stays
            // in play for the next check instead of being silently
            // dropped, so there's nothing left to compound.
            s.LastTick = s.LastTick.Value.AddSeconds(elapsedSeconds);
        }
        return;
    }

    // Once it's finished, I want it to flash for FlashDuration seconds
    // and then quietly go back to idle on its own, so the overlay never
    // gets stuck lit up forever if Streamer.bot is late sending the
    // next command. This check runs on every request (including plain
    // status polls), so the transition happens on schedule regardless
    // of whether anyone actually sends a new command.
    if (s is { Status: "finished", FinishedAt: not null })
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
// knows about, in which case the caller checks its own
// overlay-specific commands (setbarheight, setsize, and so on).

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
                if (string.IsNullOrEmpty(timeValue)) { error = "go requires t= (e.g., t=01:00:00)"; return true; }
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
                if (string.IsNullOrEmpty(timeValue)) { error = "Missing t= value (e.g., t=01:30:00)."; return true; }
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
                if (s is { Status: "finished", Remaining: > 0 })
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
                if (s is { Remaining: 0, Status: "running" })
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
            // Not one of mine, let the bar/radial-specific switch have a look.
            return false;
    }
}

// ------------------------------------------------------------
// Bar-specific command handling
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
        LogSegments("[BAR]", ConsoleColor.Red, 1,
            ("Time's up, flashing finish colour ", null),
            ColorSegment(state.FinishColor));

    // What was actually true right before this command touches
    // anything, after Tick() has brought it up to date. LogCommand
    // uses this to flag it if a "go" interrupts a countdown that
    // still had real time left on it, exactly the kind of thing that
    // makes "why didn't this finish when I expected" hard to
    // untangle without it.
    int remainingBeforeCommand = state.Remaining;
    string statusBeforeCommand = state.Status;

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
                    if (direction is not ("drain" or "fill")) { error = "Direction must be drain or fill."; break; }
                    state.Direction = direction;
                    break;
                }
            case "setbarheight":
                {
                    if (!int.TryParse(Get("v"), out int height) || height <= 0)
                    {
                        error = "Height must be a positive number of pixels.";
                        break;
                    }
                    state.BarHeight = height;
                    break;
                }
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
    LogCommand("[BAR]", cmd, state, error, remainingBeforeCommand, statusBeforeCommand);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state });
}

// ------------------------------------------------------------
// Radial-specific command handling
// ------------------------------------------------------------

string HandleRadialCommand((string cmd, NameValueCollection qs) input, string file, JsonSerializerOptions opts)
{
    (string cmd, NameValueCollection qs) = input;

    RadialState? loaded = LoadJson<RadialState>(file, opts);
    if (loaded == null && File.Exists(file))
        Log("[RADIAL]", $"Could not parse {Path.GetFileName(file)}, using defaults", ConsoleColor.DarkRed, 2);
    RadialState state = loaded ?? new RadialState();

    // Size and thickness used to be raw pixel values in an earlier
    // version before I made them percentages of the viewport, so the
    // ring actually scales with the OBS source. Clamping here means
    // an old config file left over from that version just gets pulled
    // back into range instead of rendering a giant broken ring.
    state.Size = Math.Clamp(state.Size, 5, 100);
    state.Thickness = Math.Clamp(state.Thickness, 1, 50);

    string statusBeforeTick = state.Status;
    Tick(state);
    if (statusBeforeTick == "running" && state.Status == "finished")
        LogSegments("[RADIAL]", ConsoleColor.Red, 1,
            ("Time's up, flashing finish colour ", null),
            ColorSegment(state.FinishColor));

    int remainingBeforeCommand = state.Remaining;
    string statusBeforeCommand = state.Status;

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
                    if (direction is not ("cw" or "ccw")) { error = "Direction must be cw or ccw."; break; }
                    state.Direction = direction;
                    break;
                }
            case "setsize":
                {
                    if (!int.TryParse(Get("v"), out int size) || size is < 5 or > 100)
                    {
                        error = "Size must be a number from 5 to 100 (percent of the smaller viewport side).";
                        break;
                    }
                    state.Size = size;
                    break;
                }
            case "setthickness":
                {
                    if (!int.TryParse(Get("v"), out int thickness) || thickness is < 1 or > 50)
                    {
                        error = "Thickness must be a number from 1 to 50 (percent of the diameter).";
                        break;
                    }
                    state.Thickness = thickness;
                    break;
                }
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
    LogCommand("[RADIAL]", cmd, state, error, remainingBeforeCommand, statusBeforeCommand);

    if (error != null) return JsonSerializer.Serialize(new { ok = false, error });
    return JsonSerializer.Serialize(new { ok = true, cmd, state });
}

// ------------------------------------------------------------
// Friendly, colour-coded event log
// ------------------------------------------------------------
// Level 1 shows a curated one-liner per real command, nothing for
// status polls. Level 2 adds a raw state dump underneath. I kept
// this as one big switch instead of a dictionary lookup because I
// wanted each message to be able to reference the resulting state
// (the time, the colour, and so on), and a switch is the easiest
// place to do that without extra ceremony.

void LogCommand(string tag, string cmd, OverlayState state, string? error, int remainingBefore, string statusBefore)
{
    if (cmd is "status" or "") return;

    if (error != null)
    {
        Log(tag, $"FAILED {cmd}: {error}", ConsoleColor.Red);
        return;
    }

    string time = SecsToHms(state.Remaining);

    // What clock time a running countdown is actually due to hit
    // zero. This is the main thing that makes "why didn't this
    // finish when I expected" easy to check myself later: compare
    // this line's finish time against the "Time's up" line's own
    // timestamp when it actually happens.
    string finishClock = DateTime.Now.AddSeconds(state.Remaining).ToString("HH:mm:ss");

    // If a "go" lands while something was already running with real
    // time left on it, that's exactly the kind of silent overlap
    // that makes a countdown look like it "ran long" without an
    // obvious cause. Flag it loudly rather than letting it slide by
    // as a normal start.
    string interruptedNote = statusBefore == "running" && remainingBefore > 0
        ? $" (was still running with {SecsToHms(remainingBefore)} left before this)"
        : "";

    switch (cmd)
    {
        case "go":
            LogSegments(tag, ConsoleColor.Green, 1,
                ("Started, ", null),
                (time, null),
                (", colour ", null),
                ColorSegment(state.Color),
                (" to ", null),
                ColorSegment(state.FinishColor),
                (" on finish, dir ", null),
                (state.Direction, null),
                ($", finishes around {finishClock}{interruptedNote}", null));
            break;
        case "start":
            Log(tag, $"Started, {time} remaining, finishes around {finishClock}", ConsoleColor.Green);
            break;
        case "pause":
            Log(tag, $"Paused, {time} remaining", ConsoleColor.Yellow);
            break;
        case "stop":
            Log(tag, "Stopped", ConsoleColor.Red);
            break;
        case "reset":
            Log(tag, $"Reset to {time}", ConsoleColor.DarkYellow);
            break;
        case "settime":
            Log(tag, $"Time set to {time}", ConsoleColor.Cyan);
            break;
        case "addtime":
            Log(tag, state.Status == "running"
                ? $"Time added, now {time} remaining, finishes around {finishClock}"
                : $"Time added, now {time}", ConsoleColor.Cyan);
            break;
        case "subtime":
            Log(tag, state.Status == "running"
                ? $"Time subtracted, now {time} remaining, finishes around {finishClock}"
                : $"Time subtracted, now {time}", ConsoleColor.Cyan);
            break;
        case "setcolor":
            LogSegments(tag, ConsoleColor.Magenta, 1, ("Colour set to ", null), ColorSegment(state.Color));
            break;
        case "setfinishcolor":
            LogSegments(tag, ConsoleColor.Magenta, 1, ("Finish colour set to ", null), ColorSegment(state.FinishColor));
            break;
        case "setbgcolor":
            LogSegments(tag, ConsoleColor.Magenta, 1, ("Background colour set to ", null), ColorSegment(state.BgColor));
            break;
        case "setflash":
            Log(tag, $"Flash on finish: {(state.FlashOnFinish ? "on" : "off")}", ConsoleColor.Magenta);
            break;
        case "setflashduration":
            Log(tag, $"Flash duration set to {state.FlashDuration}s", ConsoleColor.Magenta);
            break;
        case "setdirection":
            Log(tag, $"Direction set to {state.Direction}", ConsoleColor.Magenta);
            break;

        // These four are bar/radial specific, so I check the actual
        // type before reading their fields. state is guaranteed to be
        // the right type in practice (a bar command only ever runs
        // against a BarState), the "is" check is just so the compiler
        // is happy without me having to add a second overload.
        case "setbarheight" when state is BarState bar:
            Log(tag, $"Bar height set to {bar.BarHeight}px", ConsoleColor.Magenta);
            break;
        case "setbarwidth" when state is BarState barWidth:
            Log(tag, $"Bar width set to {barWidth.BarWidth}", ConsoleColor.Magenta);
            break;
        case "setsize" when state is RadialState radialSize:
            Log(tag, $"Radial size set to {radialSize.Size}% of the smaller viewport side", ConsoleColor.Magenta);
            break;
        case "setthickness" when state is RadialState radialThickness:
            Log(tag, $"Radial thickness set to {radialThickness.Thickness}% of the diameter", ConsoleColor.Magenta);
            break;
        case "settrackcolor" when state is RadialState radialTrack:
            LogSegments(tag, ConsoleColor.Magenta, 1, ("Track colour set to ", null), ColorSegment(radialTrack.TrackColor));
            break;

        default:
            // Anything left over. Level 2 shows the full state right
            // underneath anyway, so this is just a fallback.
            Log(tag, cmd, ConsoleColor.Magenta);
            break;
    }

    Log(tag, $"   state: {JsonSerializer.Serialize(state)}", ConsoleColor.DarkGray, 2);
}

// ============================================================
// Colour lookup tables
// ============================================================
// These have to live inside a real class, not as top-level
// statements, "static readonly" is only valid on actual class
// fields. I found that out by putting them up near ParseColor
// originally, and watching the compiler produce a wall of unrelated
// "cannot resolve symbol" errors for everything further down the
// file. The invalid declaration confuses where the top-level
// statements region actually ends.

static class ColorTables
{
    // Not an exhaustive list of every CSS color name, just the
    // common ones I'm likely to actually type. Add more here if I
    // ever need one that's missing.
    public static readonly Dictionary<string, (byte R, byte G, byte B)> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = (0, 0, 0),
        ["white"] = (255, 255, 255),
        ["red"] = (255, 0, 0),
        ["green"] = (0, 128, 0),
        ["blue"] = (0, 0, 255),
        ["yellow"] = (255, 255, 0),
        ["cyan"] = (0, 255, 255),
        ["magenta"] = (255, 0, 255),
        ["orange"] = (255, 165, 0),
        ["purple"] = (128, 0, 128),
        ["pink"] = (255, 192, 203),
        ["brown"] = (165, 42, 42),
        ["gray"] = (128, 128, 128),
        ["grey"] = (128, 128, 128),
        ["lime"] = (0, 255, 0),
        ["navy"] = (0, 0, 128),
        ["teal"] = (0, 128, 128),
        ["maroon"] = (128, 0, 0),
        ["olive"] = (128, 128, 0),
        ["silver"] = (192, 192, 192),
        ["gold"] = (255, 215, 0),
        ["indigo"] = (75, 0, 130),
        ["violet"] = (238, 130, 238),
        ["turquoise"] = (64, 224, 208),
        ["salmon"] = (250, 128, 114),
        ["coral"] = (255, 127, 80),
        ["khaki"] = (240, 230, 140),
        ["crimson"] = (220, 20, 60),
        ["orchid"] = (218, 112, 214),
        ["plum"] = (221, 160, 221),
        ["tan"] = (210, 180, 140),
        ["beige"] = (245, 245, 220),
        ["ivory"] = (255, 255, 240),
        ["lavender"] = (230, 230, 250),
        ["chocolate"] = (210, 105, 30),
        ["tomato"] = (255, 99, 71),
        ["skyblue"] = (135, 206, 235),
        ["steelblue"] = (70, 130, 180),
        ["slategray"] = (112, 128, 144),
        ["slategrey"] = (112, 128, 144),
        ["hotpink"] = (255, 105, 180),
        ["deeppink"] = (255, 20, 147),
        ["forestgreen"] = (34, 139, 34),
        ["seagreen"] = (46, 139, 87),
        ["darkgreen"] = (0, 100, 0),
        ["darkred"] = (139, 0, 0),
        ["darkblue"] = (0, 0, 139),
        ["darkorange"] = (255, 140, 0),
        ["darkviolet"] = (148, 0, 211),
        ["firebrick"] = (178, 34, 34),
        ["chartreuse"] = (127, 255, 0),
        ["springgreen"] = (0, 255, 127),
    };

    // The classic 16 colour Win32 console palette. These are the
    // standard approximate RGB values for each ConsoleColor, used to
    // find the closest match to whatever colour actually came in.
    public static readonly (ConsoleColor Color, byte R, byte G, byte B)[] ConsolePalette =
    [
        (ConsoleColor.Black, 0, 0, 0),
        (ConsoleColor.DarkBlue, 0, 0, 128),
        (ConsoleColor.DarkGreen, 0, 128, 0),
        (ConsoleColor.DarkCyan, 0, 128, 128),
        (ConsoleColor.DarkRed, 128, 0, 0),
        (ConsoleColor.DarkMagenta, 128, 0, 128),
        (ConsoleColor.DarkYellow, 128, 128, 0),
        (ConsoleColor.Gray, 192, 192, 192),
        (ConsoleColor.DarkGray, 128, 128, 128),
        (ConsoleColor.Blue, 0, 0, 255),
        (ConsoleColor.Green, 0, 255, 0),
        (ConsoleColor.Cyan, 0, 255, 255),
        (ConsoleColor.Red, 255, 0, 0),
        (ConsoleColor.Magenta, 255, 0, 255),
        (ConsoleColor.Yellow, 255, 255, 0),
        (ConsoleColor.White, 255, 255, 255),
    ];
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
    public int Port { get; set; } = 37000;

    // 1 = normal operation, this is what I actually ship to a friend.
    // 2 = full diagnostics: raw query strings, resulting state, config
    //     load or parse failures, full exception stack traces.
    // 3 = everything, including the 5x/sec status polling. Very noisy,
    //     only useful when I'm debugging the polling itself.
    [JsonPropertyName("debugLevel")]
    public int DebugLevel { get; set; } = 1;

    // True once the first-time setup wizard has run. Run the exe
    // with --setup to go through it again any time, e.g. if Twitch
    // ad timing changes and the Streamer.bot commands need updating.
    [JsonPropertyName("setupComplete")]
    public bool SetupComplete { get; set; }

    // Remembered from the wizard purely so I know what was last
    // configured, e.g. for a future "just show me the commands again"
    // command without re-running the whole wizard. Not read anywhere
    // yet, but no reason to throw the answer away.
    [JsonPropertyName("adBreakSeconds")]
    public int AdBreakSeconds { get; set; }

    [JsonPropertyName("adFreeSeconds")]
    public int AdFreeSeconds { get; set; }

    // "bar", "radial", or "both". Which overlay(s) the wizard set up.
    // I use this so every launch after the wizard only shows the
    // link(s) that are actually relevant, instead of always showing
    // both and making someone wonder which one they're supposed to
    // use.
    [JsonPropertyName("overlayChoice")]
    public string OverlayChoice { get; set; } = "bar";
}

// Shared by both overlay types. Bar and radial each add their own
// visual-specific fields on top of this in their own subclass.
class OverlayState
{
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }

    [JsonPropertyName("initialTime")]
    public int InitialTime { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle"; // idle, running, paused, finished

    [JsonPropertyName("lastTick")]
    public DateTime? LastTick { get; set; }

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
    public DateTime? FinishedAt { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "drain";
}

class BarState : OverlayState
{
    [JsonPropertyName("barHeight")]
    public int BarHeight { get; set; } = 5;

    [JsonPropertyName("barWidth")]
    public string BarWidth { get; set; } = "100%";

    // No constructor needed here, Direction already defaults to
    // "drain" on the base class, which is exactly what I want for
    // the bar. RadialState below is the one that actually needs to
    // override it.
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
// I keep this here instead of a separate text file, so there's only
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

    NEW HERE? START WITH THIS
    ----------------------------
    Just double click AdBreakTimer.exe. A wizard walks through
    everything on screen: pick a port (press Enter is fine), pick a
    style, add it to OBS, test it, and answer two questions about
    Twitch ad timing. At the end it writes out the exact web
    addresses to paste into Streamer.bot, saved in
    config/streamerbot-setup.txt so I never have to remember them.

    Everything past this point is reference material for when I want
    more control, I don't need to read any of it to get up and
    running.

    WHAT THIS IS
    ------------
    A tiny local web server (this exe) that hosts two OBS overlay
    pages and lets me control them with simple web requests, built
    around Streamer.bot's "Web Request" or "Execute HTTP Request"
    action.

    FIRST TIME SETUP
    -----------------
    The exe walks through this for me automatically the first time I
    run it, an interactive wizard that asks for a port, seeds both
    overlays with a test countdown, shows me the OBS setup steps, lets
    me test it in a browser, and then asks how my Twitch ad breaks
    are actually timed so it can write out ready-to-paste
    Streamer.bot commands (saved to config/streamerbot-setup.txt).

    To go through it again any time (new Twitch ad timing, setting
    up on a different port, whatever), just run:
        AdBreakTimer.exe --setup

    The manual version, if I ever want to do it by hand instead:
    1. Run the exe. It prints the overlay URL(s) to the console, e.g.,
           http://localhost:37000/bar/
           http://localhost:37000/radial/
    2. In OBS, add a Browser Source, paste in whichever URL I want
       (bar, radial, or both as separate sources), and set the Browser
       Source's Width/Height to whatever I like. Both overlays are
       fully responsive and just fill whatever size they're given.
       There's no fixed resolution to match.
    3. Tick "Shutdown source when not visible" OFF, so the timer keeps
       running in the background between ad breaks.

    ABOUT THE PORT
    ---------------
    Saved in config/settings.json. Every launch tries that saved port
    first. If something else is already using it, it automatically
    tries the next port up, tells me in the console, and re-saves the
    new port, so it keeps using that same one from then on unless it
    becomes busy again. To change it manually, edit
    config/settings.json and restart, or run AdBreakTimer.exe --setup.

    CONSOLE DEBUG LEVEL
    ---------------------
    Also saved in config/settings.json as "debugLevel". Controls how
    much the console window shows.

      1  Normal operation (default, this is what I ship to a friend).
         Shows only real events: timer started/paused/stopped, config
         changes, errors, and when a countdown naturally hits zero.
         The 5x/sec status polling from the overlay pages stays
         completely silent. The startup banner also stays short at
         this level, just the overlay link(s) and how to stop it,
         the more technical detail (API URLs, config folder path)
         only shows once debugLevel is 2 or higher.

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
            http://localhost:37000/debug/set?level=2
        Takes effect immediately and is saved for next time too.
      - Start it once at a higher level without saving that choice:
            AdBreakTimer.exe --debug 3

    THE LOG FILE
    --------------
    config/latest.log gets overwritten fresh every time the exe
    starts. It always captures full detail (the same as debugLevel 2)
    regardless of what the console is actually showing, so it's
    useful for troubleshooting even if nobody thought to turn logging
    up before something went wrong. If I ever need help with an
    issue, sending this one file is the fastest way to explain what
    happened.

    It doesn't include the constant status polling (that's only ever
    shown at debugLevel 3), so it stays a reasonable size even over a
    long stream.

    TIMING DIAGNOSTICS
    ---------------------
    Every time a countdown starts (go or start), the console and log
    both state the clock time it's actually due to hit zero, e.g.
    "finishes around 14:32:05". If a countdown ever seems to finish
    earlier or later than expected, that's the line to check first,
    just compare it against the timestamp on the later "Time's up"
    line.

    If a go command arrives while something was already running with
    real time left on it, that gets called out too: "(was still
    running with 00:12:34 left before this)". Seeing that regularly
    almost always means something outside this exe, most often
    Streamer.bot, is sending commands more often or with different
    timing than expected, rather than a bug in the countdown itself.

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

    Both pages are fully responsive; there's no fixed canvas size to
    match. Resize the OBS Browser Source to whatever I want and the
    overlay just fills it.

    Each has its OWN config file (config/bar.json / config/radial.json)
    and its OWN API, so I can run totally different timers on each, or
    just use whichever style I like in OBS and ignore the other.

    THE EASY WAY: ONE-SHOT "go" COMMAND
    --------------------------------------
    This is the one I use from Streamer.bot most of the time. It sets
    whatever's given and starts the countdown immediately:

        /bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

        /radial/api?cmd=go&t=00:03:00&color=%23ff0000&dir=cw

    Parameters (all optional except t):
        t         time, hh:mm:ss or mm:ss or raw seconds (required)
        color     the running colour, e.g., %2300ff00 (that's #00ff00 URL-encoded)
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
        GET http://localhost:37000/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

    When Streamer.bot detects a 3-minute ad break starting:
        GET http://localhost:37000/bar/api?cmd=go&t=00:03:00&color=%23ff0000&dir=drain

    When that ad break's Streamer.bot timer ends (back to normal):
        GET http://localhost:37000/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

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

    Colours need to be URL-encoded if using #: %23 = #, e.g., #ff0000 becomes %23ff0000
    (though bare hex without the # also works now, e.g., just v=ff0000).
    Accepted formats: #rrggbb, #rgb, bare hex without the #, common CSS
    colour names (red, orange, skyblue, and so on), rgb(), rgba(),
    hsl(), hsla(), or the word transparent.

    Every time a colour shows up in the console log, it prints in that
    actual colour (or the closest of the console's 16 colours it can
    manage), so cmd=setcolor&v=%2300ff00 will literally print "#00ff00"
    in green.

    TROUBLESHOOTING
    -----------------
    - A countdown isn't finishing when I expect: check
      config/latest.log for the "finishes around HH:mm:ss" line and
      compare it against when it actually flashed. See TIMING
      DIAGNOSTICS above.
    - Nothing shows in OBS: check the console window is still open and
      showing "running", and double check the URL/port match.
    - Bar/ring never seems to move: make sure cmd=go was called, or
      cmd=settime followed by cmd=start. Status alone won't start it.
    - Lost the Streamer.bot commands: they're saved in
      config/streamerbot-setup.txt, or run AdBreakTimer.exe --setup
      to regenerate them.
    - Twitch changed the ad timing, or want to redo setup from
      scratch: run AdBreakTimer.exe --setup any time.
    - Want to hand-edit defaults: edit config/bar.json or
      config/radial.json while the exe is NOT running, then start it.
    - Asking someone else for help: send them config/latest.log, it
      already has full detail without needing debugLevel turned up
      first.

    ============================================================
    Made by Kaydee.Codes, https://kaydee.codes/
    Free to use, no data collected, ever.
    ============================================================
    """;
}

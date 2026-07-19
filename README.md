# Ad Break Timer

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Type](https://img.shields.io/badge/type-single%20exe-blue)
![License](https://img.shields.io/badge/license-free%20to%20use-brightgreen)
[![Latest release](https://img.shields.io/github/v/release/KaydeeCodes/StreamTools-AdBreakTimer)](https://github.com/KaydeeCodes/StreamTools-AdBreakTimer/releases/latest)

A lightweight local web server that hosts two OBS Browser Source overlays (a bar and a radial ring) and drives them with simple URL commands. Built for Streamer.bot, but works with anything that can send an HTTP request.

No install, no database, no external files. It's one exe. Everything it needs is baked in at build time, and the only thing it writes to disk is a small `config` folder next to itself.

Made by [Kaydee.Codes](https://kaydee.codes/). Free to use, no data collected, ever.

---

## Download

Grab the latest build from the [Releases page](https://github.com/KaydeeCodes/StreamTools-AdBreakTimer/releases/latest). Unzip it and run `AdBreakTimer.exe`, that's the entire install.

Each release includes a SHA256 checksum in its notes. If you want to verify your download wasn't tampered with:

```
certutil -hashfile AdBreakTimer.exe SHA256
```

Compare the output against the hash listed on the release page.

Prefer to build it yourself instead? See [Building from source](#building-from-source).

---

## Contents

- [Download](#download)
- [Features](#features)
- [Quick start](#quick-start)
- [Building from source](#building-from-source)
- [OBS setup](#obs-setup)
- [Config files](#config-files)
- [Debug levels](#debug-levels)
- [API reference](#api-reference)
  - [The `go` command](#the-go-command)
  - [General commands](#general-commands)
  - [Bar only commands](#bar-only-commands)
  - [Radial only commands](#radial-only-commands)
  - [Response format](#response-format)
- [Behavior when a countdown finishes](#behavior-when-a-countdown-finishes)
- [Streamer.bot example](#streamerbot-example)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Features

- **Two overlays in one exe.** A bottom progress bar and a circular progress ring, each independently controlled with its own config and its own API.
- **Fully responsive.** No fixed canvas size. Resize the OBS Browser Source to whatever dimensions you want and both overlays fill it correctly.
- **Automatic port handling.** Tries the last known good port first, walks upward if it's taken, and remembers the working port for next time.
- **One shot control.** A single `go` command sets the color, direction, and duration and starts the countdown, instead of chaining several requests together.
- **Self healing finish state.** When a countdown hits zero, the overlay flashes full width or a full ring in the finish color for a configurable duration, then automatically clears itself back to idle. It never gets stuck lit up if the next command is late.
- **Readable console output.** Real events only (start, stop, pause, config changes, errors) with color coding. The constant 5x/second status polling from the overlay pages stays silent unless you turn debug logging up.
- **No telemetry.** Nothing is sent anywhere except localhost.

---

## Quick start

1. Download or build `AdBreakTimer.exe` (see [Building from source](#building-from-source)).
2. Run it. It prints the overlay URLs to the console, for example:

   ```
   Bar overlay     : http://localhost:8085/bar/
   Radial overlay  : http://localhost:8085/radial/
   ```

3. Add one or both as OBS Browser Sources.
4. Send it a command, for example open this in a browser tab:

   ```
   http://localhost:8085/bar/api?cmd=go&t=00:00:30&color=%2300ff00&finish=%23ff0000
   ```

That starts a 30 second green countdown that flashes red on finish.

---

## Building from source

This needs the .NET 8 SDK to build, but the resulting exe is fully self contained and needs nothing installed to run.

```bash
# from inside the AdBreakTimer folder, the one with AdBreakTimer.csproj
dotnet publish -c Release
```

The exe lands in `bin/Release/<target framework>/win-x64/publish/AdBreakTimer.exe`. The `<target framework>` folder name depends on which .NET SDK is installed on the machine doing the build (for example `net8.0` or `net9.0`), so just look for whatever folder is actually there under `bin/Release/`. That single exe is the entire distributable, nothing else needs to travel with it.

Full walkthrough, including installing the SDK, is in [BUILD_INSTRUCTIONS.md](BUILD_INSTRUCTIONS.md).

---

## OBS setup

1. Add a **Browser Source**.
2. Paste in `http://localhost:<port>/bar/` or `http://localhost:<port>/radial/`.
3. Set the Width/Height to whatever you want. Both overlays are responsive and just fill the space they're given, there's no resolution to match.
4. Turn **off** "Shutdown source when not visible" so the timer keeps running in the background even when the source isn't on screen.

---

## Config files

Everything is stored as JSON in a `config` folder next to the exe, created automatically on first run.

| File | Purpose |
|---|---|
| `config/settings.json` | Port number and console debug level |
| `config/bar.json` | Current state and settings for the bar overlay |
| `config/radial.json` | Current state and settings for the radial overlay |
| `config/README.txt` | Full setup and command guide, written once on first run |

These can be hand edited while the exe is **not** running if you want to change a default without sending an API call.

---

## Debug levels

Set via `debugLevel` in `config/settings.json`, or live at runtime.

| Level | Shows |
|---|---|
| **1** (default) | Real events only: start, pause, stop, config changes, errors, and when a countdown finishes naturally. This is what ships to end users. |
| **2** | Everything in level 1, plus the raw command and query string behind every event, the full resulting state, config load/parse failures, and full exception stack traces. |
| **3** | Everything in level 2, plus every single HTTP request including the constant status polling. Very noisy, only useful for debugging the polling itself. |

Change it three ways:

```bash
# 1. Edit the config file and restart
config/settings.json  ->  "debugLevel": 2

# 2. While it's running, hit this URL (takes effect immediately and saves)
http://localhost:8085/debug/set?level=2

# 3. One off, without saving the change
AdBreakTimer.exe --debug 3
```

---

## API reference

Base URL is whatever the console prints on startup, `http://localhost:<port>`.

Every command below is a `GET` request and works on both endpoints unless marked otherwise:

```
/bar/api?cmd=...
/radial/api?cmd=...
```

Colors need to be URL encoded if using `#`. `%23` is `#`, so `#ff0000` becomes `%23ff0000`.

### The `go` command

This is the one command you'll use most. It sets whatever parameters you give it and starts the countdown immediately, replacing what would otherwise be four or five separate requests.

```
GET /bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain&flash=on&flashfor=30
```

| Parameter | Required | Description |
|---|---|---|
| `t` | Yes | Duration. Accepts `hh:mm:ss`, `mm:ss`, or raw seconds. |
| `color` | No | Running color, URL encoded. |
| `finish` | No | Color it switches to when the countdown hits zero. |
| `dir` | No | Bar: `drain` or `fill`. Radial: `cw` or `ccw`. |
| `flash` | No | `on` or `off`, whether it flashes when finished. |
| `flashfor` | No | Seconds to flash before auto clearing to idle. Default 30. |

### General commands

These work on both `/bar/api` and `/radial/api`.

| Command | Parameters | Description |
|---|---|---|
| `cmd=start` | none | Starts or resumes the countdown from the current remaining time. |
| `cmd=pause` | none | Pauses without losing remaining time. |
| `cmd=stop` | none | Stops and clears the remaining time to zero. |
| `cmd=reset` | none | Returns to the initial time set, stays idle (does not auto start). |
| `cmd=status` | none | Returns the current state. Used internally by the overlay pages, five times a second. |
| `cmd=settime` | `t` | Sets the duration without starting it. |
| `cmd=addtime` | `s` (seconds) | Adds time to the current countdown. |
| `cmd=subtime` | `s` (seconds) | Removes time from the current countdown. |
| `cmd=setcolor` | `v` (color) | Sets the running color. |
| `cmd=setfinishcolor` | `v` (color) | Sets the color used when the countdown hits zero. |
| `cmd=setbgcolor` | `v` (color, or `transparent`) | Sets the page background color. |
| `cmd=setflash` | `v` (`on`/`off`) | Turns the finish flash animation on or off. |
| `cmd=setflashduration` | `v` (seconds) | How long it flashes before auto clearing to idle. |

### Bar only commands

| Command | Parameters | Description |
|---|---|---|
| `cmd=setdirection` | `v` (`drain`/`fill`) | `drain` starts full and shrinks, `fill` starts empty and grows. |
| `cmd=setbarheight` | `v` (pixels) | Height of the bar. Default `5`. |
| `cmd=setbarwidth` | `v` (CSS width, e.g. `100%`) | Width of the bar track. |

### Radial only commands

| Command | Parameters | Description |
|---|---|---|
| `cmd=setdirection` | `v` (`cw`/`ccw`) | Sweep direction. |
| `cmd=setsize` | `v` (5 to 100) | Diameter as a percentage of the viewport's smaller side. Default `60`. This is what makes the ring scale as the Browser Source is resized. |
| `cmd=setthickness` | `v` (1 to 50) | Ring stroke width as a percentage of the diameter. Default `7`. |
| `cmd=settrackcolor` | `v` (color) | Color of the unfilled background ring. |

### Response format

Every request returns JSON.

Success:

```json
{
  "ok": true,
  "cmd": "go",
  "state": {
    "remaining": 1800,
    "initialTime": 1800,
    "status": "running",
    "color": "#00ff00",
    "finishColor": "#ff0000",
    "direction": "drain",
    "flashOnFinish": true,
    "flashDuration": 30
  }
}
```

Failure:

```json
{
  "ok": false,
  "error": "Time must be greater than zero."
}
```

---

## Behavior when a countdown finishes

The instant a countdown hits zero, the overlay snaps to full (the whole bar, or a fully drawn ring) in the finish color and flashes for `flashDuration` seconds. If nothing tells it what to do next before that duration is up, it automatically reverts to idle (invisible) on its own. It is never left stuck lit up waiting for the next command.

---

## Streamer.bot example

A typical hour long ad break cycle with a 3 minute ad break in the middle:

```
# When the previous ad break finishes, start a 1 hour countdown to the next one
GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain

# When Streamer.bot detects the 3 minute ad break starting
GET http://localhost:8085/bar/api?cmd=go&t=00:03:00&color=%23ff0000&dir=drain

# When that ad break's Streamer.bot timer ends, back to the normal 1 hour countdown
GET http://localhost:8085/bar/api?cmd=go&t=01:00:00&color=%2300ff00&finish=%23ff0000&dir=drain
```

If the second command is a little late, the overlay just flashes red at zero and clears itself, no manual cleanup needed.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Nothing shows up in OBS | Check the console window is still open and shows "running". Confirm the port in the URL matches what the console printed. |
| Bar or ring never moves | Make sure `cmd=go` was called, or `cmd=settime` followed by `cmd=start`. `cmd=status` alone does not start anything. |
| Console is too noisy or too quiet | Adjust the debug level, see [Debug levels](#debug-levels). |
| Want to change a default without an API call | Edit `config/bar.json` or `config/radial.json` directly while the exe is not running. |

---

## License

Free to use, modify, and share. No attribution required, though it's appreciated. No warranty, use at your own risk.

---

Made by [Kaydee.Codes](https://kaydee.codes/). Free to use, no data collected, ever.

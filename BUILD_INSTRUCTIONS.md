# Building Ad Break Timer into a single exe

Made by [Kaydee.Codes](https://kaydee.codes/). Free to use, no data collected, ever.

**Just want to use the app?** You don't need anything on this page. Download the ready-made exe from the [Releases page](https://github.com/KaydeeCodes/StreamTools-AdBreakTimer/releases/latest) instead, unzip it, and double click `AdBreakTimer.exe`. This file is only relevant if you're building the exe yourself from source code.

I can't compile a Windows exe from an environment that doesn't have the
.NET SDK on it, so this is the one-time build step. Takes about 5
minutes including the SDK install if I don't already have it.

## 1. Install the .NET SDK (one-time, if I don't have it already)

Download and install the .NET 8 SDK (not just the runtime) from:
https://dotnet.microsoft.com/download/dotnet/8.0

A newer SDK (9.0 or later) works fine too if that's what's already installed, .NET rolls forward automatically and the app runs the same either way. The only difference is the folder name in the build output, covered below.

Grab the SDK installer for whatever OS I'm building on. After
installing, open a new terminal and confirm it worked:

```
dotnet --version
```

Should print something like `8.0.xxx` (or `9.0.xxx`, etc, whatever's installed).

## 2. Build the single file exe

From a terminal inside this `AdBreakTimer` folder (the one with
`AdBreakTimer.csproj` in it), run:

```
dotnet publish -c Release
```

The project file is already set up to produce a single self-contained
Windows exe regardless of what OS I'm building on. When it's done,
the output is in:

```
bin\Release\<target framework>\win-x64\publish\
```

The `<target framework>` part of that path depends on which .NET SDK is installed on the machine doing the build, for example `net8.0` or `net9.0`. Just look for whatever folder is actually there under `bin\Release\`, there'll only be one.

`AdBreakTimer.exe` in there is the only file that matters. It's fully
self-contained (the .NET runtime is baked in), so whoever runs it
doesn't need to install anything.

Note: since the `RuntimeIdentifier` in the csproj is pinned to
`win-x64`, this cross compiles a Windows exe even if I'm building from
macOS or Linux.

## 3. Hand it off

Just send `AdBreakTimer.exe`. The first time whoever you send it to runs it, an interactive setup wizard walks them through picking a port, adding it to OBS, testing it, and generating ready-to-paste Streamer.bot commands based on their own Twitch ad timing, no manual configuration needed on their end.

It also creates a `config` folder next to itself, with a `README.txt` covering every command for anyone who wants more control later.

No install, no dependencies, nothing else to lose track of.

## Rebuilding after changes

Any time `Program.cs` or the files under `Web/` change, just run
`dotnet publish -c Release` again and grab the new exe from the same
`publish` folder.

## Troubleshooting

**The exe is small (under a few hundred KB) and sits next to a separate `.dll`.**
That means it got built, not published, publish and build are different
commands and land in different folders. Run `dotnet publish -c Release`
specifically, and grab the exe from `bin\Release\...\publish\`, not
`bin\Debug\...`. A real published exe is tens of megabytes, since the
whole .NET runtime is bundled inside it.

**Getting "Embedded resource not found" when opening the overlay pages.**
This means the exe was built from a stale cache, usually from before
the `Web\*.html` files existed or changed. Clean and rebuild:

```
dotnet clean
dotnet publish -c Release
```

**A command isn't being recognised, like `dotnetclean` or `dotnetpublish`.**
Check for a missing space. It's two separate words: `dotnet` is the
command, `clean` or `publish` is the argument to it.

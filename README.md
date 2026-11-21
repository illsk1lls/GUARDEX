# GUARDEX (WIP)

Host defense system for a Windows PC. Rules act as soon as they match. A local model reads the close calls and can ask the person at the keyboard about suspicious behavior.

The tray icon opens a small panel. The switch turns inbound watching on and off. Open GUARDEX shows the window. The pages are Traffic, Blocks, Host, and Remote. Web pages and torrents are a separate program, GUARDEX Browser.

A local model can be connected via the flyout or main window.

## Inbound traffic

GUARDEX reads new inbound TCP clients of ports this PC is listening on, and HTTP.sys request lines when the process is elevated. Half-open SYN packets are not counted. Each source is scored into allow, watch, challenge, or block.

Pacing and the pause between tries are scored from HTTP requests, and only for a client that does not look like a browser. A sweep is six or more listening ports within two minutes, without one port taking the traffic. A phone loading a page, a device polling one port, or another PC using a few services over the hour is not blocked for that.

A client that gives a known scanner name is blocked, and so is one address asking for several well-known sensitive paths in the same session. One of those paths, or a headless browser name by itself, is not.

A block-band address is added to Windows Firewall when the process is elevated and blocking is on. The first block lasts 10 minutes. The same address coming back after it expires is held for 30 minutes, then 2 hours, then 12. A release or a model step-down clears that count. A day with no new block clears it too. The rule is removed when it expires and when GUARDEX exits. A rule left by a crash is removed the next time GUARDEX starts. Loopback, link-local, multicast, this PC, and the default gateway are not blocked.

Set `GUARDEX_ENFORCE` to `0` to report blocks without creating firewall rules.

## Model

The model is an OpenAI-compatible endpoint. On Connect, GUARDEX reads the model id from `/v1/models`, sends the model's instructions, and marks status ready only after that call returns.

```powershell
$env:GUARDEX_BASE_URL = "http://127.0.0.1:8080/v1"
$env:GUARDEX_MODEL = "your-model"
$env:GUARDEX_API_KEY = "none"
```

If the endpoint returns 401 or 403, a sign-in window offers three modes. None sends no authorization header. API sends a bearer token. Basic (NPM) sends a username and password. Remember on this PC stores that choice for the endpoint.

The model does not run commands. It does not lift a hard signal, such as the canary, a known scanner signature, or several sensitive paths from one address. On a gray or soft block it may move the source one band. On a watch-level startup item it may raise a malicious verdict to block. It does not delete startup items.

When one new event is seen, GUARDEX asks immediately. It does not wait for a score to accumulate. The question is a yes or no, in the window and in the tray panel. Examples are a download left running, a program in Temp or Downloads connecting out, a new startup item, or the first connection from a new address. Yes steps a normal source down one band, or leaves a download or startup item in place. No steps a normal source up one band, stops a download or temp program that is still running, or marks a startup item block. A hard rule stays in place either way.

## Startup items

GUARDEX checks startup folders, Run and RunOnce keys, services, scheduled tasks, Winlogon, image-file hijacks, AppInit and AppCert DLLs, LSA packages, logon scripts, and WMI consumers. Findings are reported. Nothing in those locations is deleted or disabled.

The first sweep records what is already there and does not mark it. An item that appears later stays in the list until a later sweep no longer sees it. It is not added to the baseline just because a sweep ran.

## Telemetry

GUARDEX blocks Windows diagnostic reporting when the process is elevated. The switch is on the Host page and is on by default. Turn it off there to put the previous settings back.

The block is a marked section in the hosts file, outbound firewall rules for the Windows compatibility and error-reporting programs, and a disabled start for the diagnostic services and tasks that send those reports. Those services and tasks are disabled, not deleted. The previous start type and policy values are saved under `%LocalAppData%\Guardex` and restored when the switch is turned off. A finding in the startup list is still only a report. This switch does not delete or disable arbitrary startup items.

Windows Update, Microsoft Defender, and sign-in are left running. Microsoft account settings sync uses one of the blocked hosts, so that sync may stop while the block is on. Without elevation the status says so and nothing is changed.

## Remote tools

Known remote-access tools are listed on the Remote page. None are allowed until checked. The choice is saved for the Windows user. An unchecked tool is stopped, including its service when the process is elevated. Programs that are not on the list are left alone.

A distinctive program name matches on its own. A generic name such as `agent.exe` or `update.exe` is left alone unless the file's version information or signature names a known product. A renamed ScreenConnect client is still recognized when the file says ScreenConnect.

Incoming Remote Desktop is disconnected only when someone is also signed in at the console. Allow RDP on the Remote page if you sign in from another computer.

## Page launches

A page script does not receive the filesystem. If the browser starts a shell, a script host, a named scanning tool, or a program from Temp, GUARDEX stops that process. A program opened from Downloads is reported and left running. The user's own terminal is left alone, as are programs that live under the real Windows directory or Program Files. A Temp or Downloads path is not excused because a folder in it is named Windows or Program Files. This check stays on when inbound watching is paused.

To allow a program by name, add it to `%LocalAppData%\Guardex\launch-allow.txt`. Write `0` to `%LocalAppData%\Guardex\launch-stop.txt` to report without stopping.

Programs running from Temp or Downloads that open an outbound connection are reported. Browser traffic is not blocked. Packet contents are not read.

A clipboard value that is a wallet address is restored if a different address of the same kind replaces it within one second.

## Browser

GUARDEX Browser is its own window, with its own tray icon and a desktop shortcut. It is not the Windows default browser, and it does not cover Chrome or Edge. The rest of the PC is not forced through the tunnel. The tray app still guards the machine.

The tabs sit on the top edge of the window. Back, forward, reload, and the address bar are on the row under them. Find, zoom, print, history, and bookmarks are in the menu. Settings is in that menu too. The browser reopens the tabs from the last session. Downloads go to the Downloads folder and are not opened. A program started from there is still only reported. Passwords are not saved. Extensions, sync, and extra profiles are not included.

Every page goes through a proxy on this PC. The VPN section of Settings imports a WireGuard or OpenVPN file and turns the tunnel on. While it is on, pages use that tunnel only. If the tunnel drops, pages stop. They do not fall back to the normal network. Turn the tunnel off to browse on this PC's network again. The browser is IPv4. QUIC is off. A site that exists only on IPv6 does not open.

This is the browser's own stack, not a system-wide network lock. A piece of the browser that ignored the proxy could still open a socket. Chrome and Edge are outside it.

Calls the open tab makes are listed from the menu. Same-site files are omitted. A third-party script is shown. A third-party script that then calls a different host is marked, as is a call to a raw IP address. That list is a label. It does not block the request. Form data and response bodies are not read.

Ads and trackers are blocked with EasyList and EasyPrivacy. The lists are downloaded to `%LocalAppData%\Guardex\filters` and refreshed about every four days. They are not part of the program. The address you open is not blocked. Matching scripts, images, frames, and other requests are. Common ad boxes are hidden with a style element in the page. A site that rejects an added style can still have its matching requests blocked. Turn this off under Settings, Privacy. While the tunnel is on, a list update uses the tunnel. If the tunnel is down, the saved lists stay and the update waits.

## Torrents

A magnet link asks before it opens. A .torrent file asks whether to open it or save it. An opened torrent shows a progress bar with the other downloads. With the tunnel on, opening one sends it through the tunnel, and it stops if the tunnel drops. With the tunnel off, type `allow torrents on this network` in that prompt to use this PC's network. Files from an opened torrent land in Downloads\GUARDEX. Stopping one leaves the files on disk.

## Packages

The source does not contain the NuGet packages. `dotnet restore` downloads them. Copying the repository to another machine and building without that step fails.

| Package | Version | What it is |
| --- | --- | --- |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | Reads HTTP.sys request lines |
| Microsoft.Web.WebView2 | 1.0.4314.35 | GUARDEX Browser view |

```powershell
dotnet restore
dotnet build
dotnet run
```

Building needs the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Running a build that is not self-contained needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). GUARDEX Browser needs the [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

Run elevated for firewall rules, the telemetry block, HTTP.sys request paths, and stopping a remote-access service. Without elevation, connections and findings are still reported.

`dotnet run -- --check` runs the tray rule self-checks. `dotnet run --project Guardex.Browser -- --check` runs the browser checks. `dotnet run -- --posture` prints a startup sweep and does not save the baseline. `dotnet run --project Guardex.Browser` opens the browser.

## Install

The release file is `GUARDEX-Setup.exe`. It carries its own runtime, so it opens on a machine that does not have .NET 10 yet. It asks for administrator approval, then:

1. Installs the .NET 10 Desktop Runtime from Microsoft when that runtime is missing.
2. Installs the WebView2 runtime from Microsoft when that runtime is missing.
3. Copies GUARDEX into `C:\Program Files\GUARDEX`.
4. Adds a Start menu shortcut for GUARDEX, and a Start menu shortcut and a desktop shortcut for GUARDEX Browser.
5. Can start GUARDEX at sign-in.

The downloads are the official Microsoft links `https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` and `https://go.microsoft.com/fwlink/p/?LinkId=2124703`.

To build that setup on a machine that already has the SDK:

```powershell
.\build-release.ps1
```

The installer is written to `artifacts\GUARDEX-Setup.exe`.

GUARDEX does not load a filesystem minifilter and it does not register itself as the Windows antivirus. Windows only lists an antivirus that Microsoft has enrolled, and a disk filter is a signed kernel driver. This program does not turn Windows Defender off. The sign-in option starts the same user-mode guard when you sign in, so the firewall rules, the telemetry block, remote-tool stops, and launch checks keep running.

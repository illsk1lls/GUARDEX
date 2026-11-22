# GUARDEX (WIP)

Host defense for a Windows PC. Rules act as soon as they match. A local model reads the close calls and can ask you what it is looking at, with the actual item on screen.

The tray icon opens a small panel. The switch turns inbound watching on and off. Open GUARDEX for the window: Traffic, Blocks, Host, Telemetry, Remote, and Log. Host, Blocks, and Remote show a count in parentheses when that list is not empty. Web pages and torrents are a separate program, GUARDEX Browser.

A local model connects from the flyout or the main window.

## Inbound traffic

GUARDEX sees new inbound TCP clients on ports this PC is listening on, and HTTP.sys request lines when it is running elevated. Half-open SYN packets are not counted. Each source is scored into allow, watch, challenge, or block.

Pacing, and the pause between tries, comes from HTTP requests, and only for a client that does not look like a browser. A sweep is six or more listening ports within two minutes, without one port taking the traffic. A phone loading a page, a device polling one port, or another PC using a few services over the hour is not blocked for that.

A client that gives a known scanner name is blocked, and so is one address asking for several well-known sensitive paths in the same session. One of those paths, or a headless browser name by itself, is not.

When the process is elevated and blocking is on, a block-band address gets a Windows Firewall rule. The first hold is 10 minutes. If that address comes back after the hold ends, the next holds are 30 minutes, then 2 hours, then 12. Releasing it, or a model step-down, clears the count. A day with no new block clears it too. The rule comes off when the hold ends and when GUARDEX exits. A rule left behind by a crash is removed the next time GUARDEX starts. Loopback, link-local, multicast, this PC, and the default gateway are not blocked.

Every firewall rule GUARDEX adds is grouped under the name GUARDEX, so the uninstaller can find them. Temporary address rules still use a name that starts with `GUARDEX `, and those are the ones removed on exit. Diagnostic program rules use a different name so they stay until you turn telemetry off, or until uninstall removes them.

Set `GUARDEX_ENFORCE` to `0` to report blocks without creating firewall rules.

## Log

The Blocks page only shows what is still active. When a hold expires, or the model steps a soft block down, that row leaves the list. The Log page keeps the line: the address, that the block was lifted, and why. A model timeout is recorded there too, and so is each confirmation.

The same lines are written to `%LocalAppData%\Guardex\log.txt` and kept across restarts. The file holds the latest 400 lines.

## Questions

When one new event shows up, GUARDEX asks right away. It does not wait for a score to pile up. The question is yes or no, in the window and in the tray panel. Under the question is the thing it means: for a startup item, the kind, name, location, and command; for a download, the program and who started it; for a new address, the score and why it was noticed. The tray balloon includes that same detail. If the model shortens the question, the detail stays.

Yes steps a normal source down one band, or leaves a download or startup item in place. No steps a normal source up one band, stops a download or temp program that is still running, or marks a startup item block. A hard rule stays either way. After a choice, the panel and the window show Saved and the item, and the buttons stay quiet for a moment so the next question is still readable.

A startup name that contains a changing id, such as `msedge_cleanup_{...}`, is remembered from the part that stays still. No on one of those also blocks the next name with a different id in that same place. Yes remembers it the other way, so the next one is not asked. A later no replaces the yes. The startup entry itself is not deleted.

## Model

The model is an OpenAI-compatible endpoint. On Connect, GUARDEX reads the model id from `/v1/models`, sends the model's instructions, and marks status ready only after that call returns.

```powershell
$env:GUARDEX_BASE_URL = "http://127.0.0.1:8080/v1"
$env:GUARDEX_MODEL = "your-model"
$env:GUARDEX_API_KEY = "none"
```

If the endpoint returns 401 or 403, a sign-in window offers three modes. None sends no authorization header. API sends a bearer token. Basic (NPM) sends a username and password. Remember on this PC stores that choice for the endpoint.

The model does not run commands. It does not lift a hard signal, such as the canary, a known scanner signature, or several sensitive paths from one address. On a gray or soft block it may move the source one band. On a watch-level startup item it may raise a malicious verdict to block. It does not delete startup items.

## Startup items

GUARDEX checks startup folders, Run and RunOnce keys, services, scheduled tasks, Winlogon, image-file hijacks, AppInit and AppCert DLLs, LSA packages, logon scripts, and WMI consumers. Findings are reported. Nothing in those locations is deleted or disabled.

The first sweep records what is already there and does not mark it. An item that appears later stays in the list until a later sweep no longer sees it. It is not added to the baseline just because a sweep ran. A startup name that only differs by a changing id keeps the earlier allow or block.

## Telemetry

GUARDEX blocks Windows diagnostic reporting when the process is elevated. The switch is on the Telemetry page and is on by default. Turn it off there to put the previous settings back.

That page lists the diagnostic programs, services, and tasks the block covers, and the host names written into the hosts file. The host list can be edited, one name per line. The edited list is saved under `%LocalAppData%\Guardex`. Names for Windows Update, Defender, and sign-in are rejected and are not written. The programs, services, and tasks stay fixed.

The block is a marked section in the hosts file, outbound firewall rules for the Windows compatibility and error-reporting programs, and a disabled start for the diagnostic services and tasks that send those reports. Those services and tasks are disabled, not deleted. The previous start type and policy values are saved under `%LocalAppData%\Guardex` and restored when the switch is turned off. A finding in the startup list is still only a report. This switch does not delete or disable arbitrary startup items.

Windows Update, Microsoft Defender, and sign-in are left running. Microsoft account settings sync uses one of the blocked hosts, so that sync may stop while the block is on. Without elevation the status says so and the hosts file, firewall, services, and tasks are left unchanged. A saved host list is applied the next time the block runs elevated.

## Remote tools

Known remote-access tools are listed on the Remote page. None are allowed until checked. The choice is saved for the Windows user. An unchecked tool is stopped, including its service when the process is elevated. Programs that are not on the list are left alone.

A distinctive program name matches on its own. A generic name such as `agent.exe` or `update.exe` is left alone unless the file's version information or signature names a known product. A renamed ScreenConnect client is still recognized when the file says ScreenConnect.

Incoming Remote Desktop is disconnected only when someone is also signed in at the console. Allow RDP on the Remote page if you sign in from another computer.

## Page launches

A page script does not receive the filesystem. If the browser starts a shell, a script host, a named scanning tool, or a program from Temp, GUARDEX stops that process. A program opened from Downloads is reported and left running. A shell started outside the browser is left running, as are programs that live under the real Windows directory or Program Files. A Temp or Downloads path is not excused because a folder in it is named Windows or Program Files. If GUARDEX Browser starts a crash reporter, an updater, or SmartScreen, the tray stops that process. The same programs started by Edge, Chrome, or Windows are left running. This check stays on when inbound watching is paused.

To allow a program by name, add it to `%LocalAppData%\Guardex\launch-allow.txt`. Write `0` to `%LocalAppData%\Guardex\launch-stop.txt` to report without stopping.

Programs running from Temp or Downloads that open an outbound connection are reported. Browser traffic is not blocked. Packet contents are not read.

A clipboard value that is a wallet address is restored if a different address of the same kind replaces it within one second.

## Browser

GUARDEX Browser is its own window and a desktop shortcut. It has no tray icon. Closing the window exits. It is not the Windows default browser, and it does not cover Chrome or Edge. The rest of the PC is not forced through the tunnel. The tray app still guards the machine.

The tabs sit on the top edge of the window. Right-click a tab to duplicate it, copy its address, close the others, or reopen a closed tab. Ctrl+9 selects the last tab. Back, forward, reload, home, and the address bar are on the row under them. A bookmarks bar under that row lists saved sites. A site icon is cached for bookmarks and history. Until an icon is saved, the row shows the first letter. The menu can hide the bar. Find, zoom, print, history, and bookmarks are in the menu. Settings is in that menu too, and Ctrl+, opens it. A newly opened window opens the home page. Opening the browser again after it was closed starts a normal window on that home page. Settings and the last page are not brought back. A video can use full screen. Esc, or Exit full screen, leaves it. The page cannot lock the keyboard or take the screen back on its own. The home button opens the home page on the current tab. The default home page is the built-in page, with search and shortcuts across the window. A shortcut icon is shown whole. The letter stays only until an icon is saved. Settings, General can set a custom http or https home page, and what a new tab opens. A new tab opens the home page unless that is changed. A page that asks for its own window still opens that page. Search uses Google, Bing, or DuckDuckGo. Shortcuts are kept in `%LocalAppData%\Guardex\home.json`. Import and Export on the home page read and write that list. A file of name and address pairs can be imported. Type a file path in the address bar to open that file. A bookmark, a shortcut, and a link do not open a file. The downloads button opens a list of recent files, with a progress bar while one is active and a count on the button. A folder icon shows that file. Opening a finished file asks first. Files land in the Downloads folder. A program started from there is still only reported. The menu opens Passwords. Entering a password on a site offers to save it for that site, including after the page moves on. Saved passwords are listed under Settings, Passwords, with a saved sign-in that has no password yet. A password stays hidden until it is shown for that site. A Chrome or Firefox password CSV can be imported there. A saved password and the sign-in cookie for that site are encrypted for this PC. The sign-in is restored before that site opens, from the address bar, a shortcut, a link, or a redirect, and it is removed when the last tab leaves. The menu and Settings, Privacy can clear cookies kept in this browser. A saved sign-in is put back the next time that site opens. Cookies that are not a saved sign-in stay until then. A copy of the sealed sign-in does not open on another computer. Extensions, sync, and extra profiles are not included.

Favorites can be imported from Chrome, Edge, or Firefox, one browser at a time. If that browser has more than one profile, pick one profile. Bookmarks already in the list stay. An imported bookmark is kept even when the address is not a normal page. This browser opens http and https pages. The list holds 2000. History is kept on this PC. Import is on the bookmarks page and under Settings, Privacy.

Every page goes through a proxy on this PC. The VPN section of Settings imports a WireGuard or OpenVPN file and turns the tunnel on. While it is on, pages use that tunnel only. If the tunnel drops, pages stop. They do not fall back to the normal network. Turn the tunnel off to browse on this PC's network again. The browser is IPv4. QUIC is off. A site that exists only on IPv6 does not open.

The tunnel covers this browser. Chrome and Edge stay on this PC's network. This browser does not send crash reports and does not start an updater. A reputation check uses the same proxy as the page. While the tunnel is on, that check uses the tunnel, and it stops if the tunnel drops. Edge's updater and this PC's SmartScreen setting are left as they are. The runtime can still send the diagnostic data Microsoft requires.

Calls the open tab makes are listed from the menu. Same-site files are omitted. A third-party script is shown. A third-party script that then calls a different host is marked, as is a call to a raw IP address. That list is a label. It does not block the request. Form data and response bodies are not read.

Ads and trackers are blocked with EasyList, EasyPrivacy, Peter Lowe's ad servers, and a malicious-address list. The lists are downloaded to `%LocalAppData%\Guardex\filters` and refreshed about every four days. They are not part of the program. Easy uses those lists. Medium also blocks third-party scripts and frames. Hard also blocks every other third-party request. Medium and hard break some sites. The page address is not blocked. Matching scripts, images, frames, and other requests are. Common ad boxes are hidden with a style element in the page. On YouTube, in-player ads are skipped and ad slots are hidden. The video keeps playing. Cosmetic rules are added after a page has started, so the list does not hold the page back. A site that rejects an added style can still have its matching requests blocked. Turn this off, or pick Easy, Medium, or Hard, under Settings, Privacy. While the tunnel is on, a list update uses the tunnel. If the tunnel is down, the saved lists stay and the update waits.

## Torrents

A magnet link asks before it opens. A .torrent file asks whether to open it or save it. An opened torrent shows a progress bar with the other downloads and saves into the Downloads folder, the same place as other files. With the tunnel on, the transfer uses the tunnel. Turning the tunnel off stops it, unless this PC's network was already confirmed. That confirmation is asked once, by typing `allow torrents on this network`, and then it is remembered. Stopping a transfer leaves the files on disk.

## Packages

The source does not contain the NuGet packages. `dotnet restore` downloads them. Copying the repository to another machine and building without that step fails.

| Package | Version | What it is |
| --- | --- | --- |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | Reads HTTP.sys request lines |
| Microsoft.Web.WebView2 | 1.0.4314.35 | GUARDEX Browser view |
| Microsoft.Data.Sqlite | 9.0.1 | Reads a Firefox bookmark database |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.3 | SQLite build used for that read |

```powershell
dotnet restore
dotnet build
dotnet run
```

Building needs the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Running a build that is not self-contained needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). GUARDEX Browser needs the [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

Run elevated for firewall rules, the telemetry block, HTTP.sys request paths, and stopping a remote-access service. If it is not elevated, GUARDEX says so at startup: those actions are limited, and connections and findings are still reported.

`dotnet run -- --check` runs the tray rule self-checks. `dotnet run --project Guardex.Browser -- --check` runs the browser checks. `dotnet run -- --posture` prints a startup sweep and does not save the baseline. `dotnet run --project Guardex.Browser` opens the browser.

## Install

The release file is `GUARDEX-Setup.exe`. It carries its own runtime, so it opens on a machine that does not have .NET 10 yet. It asks for administrator approval. If GUARDEX is already installed, the window says so and offers Upgrade, Repair, or Replace instead of a blank install. The same version is a repair. An older version is an upgrade. A newer version can still be replaced with this copy. Settings on the account are left in place.

The setup then:

1. Installs the .NET 10 Desktop Runtime from Microsoft when that runtime is missing.
2. Installs the WebView2 runtime from Microsoft when that runtime is missing.
3. Copies GUARDEX, GUARDEX Browser, and the uninstaller into `C:\Program Files\GUARDEX`.
4. Adds a Start menu shortcut for GUARDEX, and a Start menu shortcut and a desktop shortcut for GUARDEX Browser.
5. Can start GUARDEX at sign-in.
6. Creates a code-signing certificate on this PC, trusts it on this PC only, and signs the three programs with it. It is not a public certificate. Uninstall removes it.
7. Adds GUARDEX to the Windows uninstall list.

The downloads are the official Microsoft links `https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` and `https://go.microsoft.com/fwlink/p/?LinkId=2124703`.

Uninstall is `GUARDEX-Uninstall.exe`, also listed in Settings, Apps. It removes the programs, shortcuts, and the sign-in task. Two checkboxes are off unless you turn them on. Once removal starts, those choices leave the window. A progress bar then follows the steps that are actually running, and it is full when GUARDEX is removed. One removes this account's GUARDEX data (tunnel file, filters, browser profile, baseline, saved sign-in, and the log) and puts diagnostic settings back before that folder is deleted. Files already in the Downloads folder stay. The other removes firewall rules GUARDEX added. The .NET runtime and WebView2 are left installed.

To build that setup on a machine that already has the SDK:

```powershell
.\build-release.ps1
```

The installer is written to `artifacts\GUARDEX-Setup.exe`.

To try the three programs on this PC without building the installer:

```powershell
.\build-local.ps1
```

That writes `Guardex.exe`, `GuardexBrowser.exe`, and `GUARDEX-Uninstall.exe` to `artifacts\local`. It still needs the .NET 10 Desktop Runtime and the WebView2 runtime.

GUARDEX does not load a filesystem minifilter and it does not register as the Windows antivirus. It does not turn Windows Defender off. The sign-in option starts the same user-mode guard when you sign in, so the firewall rules, the telemetry block, remote-tool stops, and launch checks keep running.

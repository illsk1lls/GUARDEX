# GUARDEX (WIP)

Host defense system for a Windows PC. Rules act as soon as they match. A local model reads the close calls and can ask the person at the keyboard about suspicious behavior.

The tray icon opens a small panel. The switch turns inbound watching on and off. Open GUARDEX shows the window. The pages are Traffic, Blocks, Host, Remote, and Site (To probe specific sites for strange routing).

A local model can be connected via the flyout or main window.

## Inbound traffic

GUARDEX reads new inbound TCP clients of ports this PC is listening on, and HTTP.sys request lines when the process is elevated. Half-open SYN packets are not counted. Each source is scored into allow, watch, challenge, or block.

Pacing and the pause between tries are scored from HTTP requests, and only for a client that does not look like a browser. A sweep is six or more listening ports within two minutes, without one port taking the traffic. A phone loading a page, a device polling one port, or another PC using a few services over the hour is not blocked for that.

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

The model does not run commands. It does not lift a hard signal, such as the canary or a known scanner signature. On a gray or soft block it may move the source one band. On a watch-level startup item it may raise a malicious verdict to block. It does not delete startup items.

When one new event is seen, GUARDEX asks immediately. It does not wait for a score to accumulate. The question is a yes or no, in the window and in the tray panel. Examples are a download left running, a program in Temp or Downloads connecting out, a new startup item, or the first connection from a new address. Yes steps a normal source down one band, or leaves a download or startup item in place. No steps a normal source up one band, stops a download or temp program that is still running, or marks a startup item block. A hard rule stays in place either way.

## Startup items

GUARDEX checks startup folders, Run and RunOnce keys, services, scheduled tasks, Winlogon, image-file hijacks, AppInit and AppCert DLLs, LSA packages, logon scripts, and WMI consumers. Findings are reported. Nothing in those locations is deleted or disabled.

The first sweep records what is already there and does not mark it. An item that appears later stays in the list until a later sweep no longer sees it. It is not added to the baseline just because a sweep ran.

## Remote tools

Known remote-access tools are listed on the Remote page. None are allowed until checked. The choice is saved for the Windows user. An unchecked tool is stopped, including its service when the process is elevated. Programs that are not on the list are left alone.

Incoming Remote Desktop is disconnected only when someone is also signed in at the console. Allow RDP on the Remote page if you sign in from another computer.

## Page launches

A page script does not receive the filesystem. If the browser starts a shell, a script host, or a program from Temp, GUARDEX stops that process. A program opened from Downloads is reported and left running. The user's own terminal is left alone, as are programs that live under the real Windows directory or Program Files. A Temp or Downloads path is not excused because a folder in it is named Windows or Program Files. This check stays on when inbound watching is paused.

To allow a program by name, add it to `%LocalAppData%\Guardex\launch-allow.txt`. Write `0` to `%LocalAppData%\Guardex\launch-stop.txt` to report without stopping.

Programs running from Temp or Downloads that open an outbound connection are reported. Browser traffic is not blocked. Packet contents are not read.

A clipboard value that is a wallet address is restored if a different address of the same kind replaces it within one second.

## Site

The Site page loads an address in a separate browser view, not the signed-in profile. It lists calls the page makes. Same-site files are omitted. A third-party script is shown. A third-party script that then calls a different host is marked, as is a call to a raw IP address. Form data and response bodies are not read. Downloads from that view are cancelled.

## Run

```powershell
dotnet run
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and the Edge WebView2 runtime for the Site page.

Run elevated for firewall rules, HTTP.sys request paths, and stopping a remote-access service. Without elevation, connections and findings are still reported.

`dotnet run -- --check` runs the rule self-checks. `dotnet run -- --posture` prints a startup sweep and does not save the baseline.

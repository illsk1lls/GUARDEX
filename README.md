# GUARDEX Defense System

An automated defense against automated attack tools, including ones that local models drive.

Patterns are recognized and traffic is stopped. This build is a rough first framework based on information gathered from ARTEX

Click the shield in the tray. The switch turns inbound watching on or off. Open GUARDEX shows the window. The pages are Traffic, Blocks, Host, and Remote.

A block-band source is added to Windows Firewall for a short time when this process is elevated and blocking is on. The rule comes off when it expires, and when GUARDEX exits. A rule left behind by a crash is removed the next time GUARDEX starts. The local model breaks the close calls. It does not lift a hard pattern, and it does not run commands.

GUARDEX also checks startup folders, run keys, services, scheduled tasks, Winlogon, image hijacks, and WMI consumers. It reports those. It does not delete them.

Remote access tools are a separate list. None are allowed until you check them. The choice is saved for this Windows user. An unchecked tool is stopped. Other programs are not on that list, so a normal app is left alone. Incoming Remote Desktop is disconnected only when someone is also at this keyboard. Allow it on the Remote page if you sign in from another computer.

## Run

```powershell
dotnet run
```

You need the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

The rules score on their own. If you want a local model to break the close calls:

```powershell
$env:GUARDEX_BASE_URL = "http://127.0.0.1:8080/v1"
$env:GUARDEX_MODEL = "your-model"
```

Run it as admin if you want firewall blocks, request paths, and to stop a remote tool that runs as a service. Without that, you still see who connected, and to which port.

Turn blocking off with:

```powershell
$env:GUARDEX_ENFORCE = "0"
```

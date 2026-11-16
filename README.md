# GUARDEX Defense System

An automated defense against automated attack tools, including ones that local models drive.

Patterns are recognized and traffic is stopped. This build is a rough first framework based on information gathered from ARTEX

Click the shield in the tray. The switch turns watching on or off. Open GUARDEX shows the window. It reports. It does not change your firewall.

This build monitors a single machine using a local llm.

## Run

```powershell
dotnet run
```

You need the .NET 10 SDK.

The rules score on their own. If you want a local model to break the close calls:

```powershell
$env:GUARDEX_BASE_URL = "http://127.0.0.1:8080/v1"
$env:GUARDEX_MODEL = "your-model"
```

Run it as admin if you want request paths. Without that, you still see who connected, and to which port.

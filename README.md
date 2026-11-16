# GUARDEX

Tray app that watches the machine it is running on.

It is for automated attack tools, including ones that drive a local model. They tend to hit a few things, go quiet while the model thinks, then come back. That quiet stretch can be a couple of seconds or many minutes. The fast part, the actual requests, gets scored too.

Click the shield in the tray. The switch turns watching on or off. Open GUARDEX shows the window. It reports. It does not change your firewall.

Early build. One machine for now.

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
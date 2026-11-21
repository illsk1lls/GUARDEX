using System.Diagnostics;
using System.Runtime.InteropServices;
using Guardex.Tunnel;

namespace Guardex.Browser;

static class Program
{
	[DllImport("user32.dll")]
	static extern bool SetForegroundWindow(IntPtr handle);

	[STAThread]
	static int Main(string[] args)
	{
		if (args.Contains("--check"))
		{
			var parts = new[]
			{
				Guardex.LinkRules.SelfCheck(),
				Guardex.SiteDesk.SelfCheck(),
				TunnelCheck(),
				SocksGate.SelfCheck(),
				TorrentDesk.SelfCheck(),
				AdBlock.SelfCheck()
			};
			if (parts.All(part => part == "PASS"))
			{
				Console.WriteLine("PASS");
				return 0;
			}
			Console.WriteLine(string.Join("; ", parts.Where(part => part != "PASS")));
			return 1;
		}

		using var gate = new Mutex(true, @"Local\GuardexBrowser", out var first);
		if (!first)
		{
			using var ping = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GuardexBrowser.Show");
			ping.Set();
			foreach (var process in Process.GetProcessesByName("GuardexBrowser"))
			{
				if (process.Id == Environment.ProcessId) continue;
				if (process.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(process.MainWindowHandle);
			}
			return 0;
		}

		var armed = TunnelStore.SavedOn();
		var loaded = TunnelStore.TryLoad(out var error);
		if (armed)
		{
			SocksGate.ForceHold = true;
			if (loaded) VpnHub.Require = true;
		}
		SocksGate.Start();
		using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GuardexBrowser.Show");
		var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
		var window = new BrowserWindow(armed, loaded, error);
		var wake = new Thread(() =>
		{
			while (show.WaitOne())
			{
				if (app.Dispatcher.HasShutdownStarted) return;
				app.Dispatcher.BeginInvoke(window.ShowFromTray);
			}
		})
		{ IsBackground = true, Name = "gx-show" };
		wake.Start();
		app.Run(window);
		try { TorrentDesk.StopEngines(); } catch { }
		VpnHub.Stop();
		VpnHub.Require = false;
		SocksGate.ForceHold = false;
		SocksGate.Stop();
		return 0;
	}

	static string TunnelCheck()
	{
		var engine = Engine.SelfTest();
		if (engine != null) return engine;
		var key = Convert.ToBase64String(new byte[32]);
		var text = "[Interface]\r\nPrivateKey = " + key + "\r\nAddress = 10.7.0.2/32\r\nDNS = 1.1.1.1\r\n\r\n[Peer]\r\nPublicKey = " + key + "\r\nEndpoint = 203.0.113.1:51820\r\n";
		if (!VpnHub.LoadConfig(text, out var loadErr)) return "load " + loadErr;
		VpnHub.Require = true;
		try
		{
			if (VpnHub.OsAllowed) return "os-open";
			if (!VpnHub.HasDns) return "dns";
			using var stream = VpnHub.OpenTcp("example.com", 9, 50);
			if (stream != null) return "open-while-down";
			if (VpnHub.HttpGet("http://example.com/", 50, "guardex", out _, out _)) return "http-while-down";
		}
		finally
		{
			VpnHub.ClearConfig();
			VpnHub.Require = false;
		}
		if (!VpnHub.OsAllowed) return "os-stuck";
		return "PASS";
	}
}

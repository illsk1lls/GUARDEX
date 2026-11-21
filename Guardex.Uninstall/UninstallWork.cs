using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Guardex.Uninstall;

static class UninstallWork
{
	public const string Group = "GUARDEX";

	public static string InstallDir() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GUARDEX");

	public static string DataDir() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");

	public static bool Tagged(string name, string? group)
	{
		if (string.Equals(group, Group, StringComparison.Ordinal)) return true;
		if (name.StartsWith("GUARDEX ", StringComparison.Ordinal)) return true;
		if (name.StartsWith("GUARDEX-Telemetry ", StringComparison.Ordinal)) return true;
		return false;
	}

	public static string InstalledVersion()
	{
		try
		{
			using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GUARDEX");
			if (key?.GetValue("DisplayVersion") is string version && version.Trim().Length > 0)
				return version.Trim();
		}
		catch { }
		return "";
	}

	public static void Remove(bool data, bool rules)
	{
		StopApps();
		if (data) RestoreTelemetry();
		if (rules) RemoveRules();
		if (data) DeleteData();
		RemoveLinks();
		RemoveTask();
		RemoveCert(ReadThumb());
		RemoveKey();
		ScheduleDelete(InstallDir());
	}

	static void StopApps()
	{
		foreach (var name in new[] { "Guardex", "GuardexBrowser" })
		{
			Process[] found;
			try { found = Process.GetProcessesByName(name); }
			catch { continue; }
			foreach (var proc in found)
			{
				try
				{
					try { proc.CloseMainWindow(); } catch { }
					if (!proc.WaitForExit(2500)) proc.Kill(true);
				}
				catch { }
				finally { proc.Dispose(); }
			}
		}
	}

	static void RestoreTelemetry()
	{
		var exe = Path.Combine(InstallDir(), "Guardex.exe");
		if (!File.Exists(exe)) return;
		try
		{
			using var proc = Process.Start(new ProcessStartInfo(exe, "--telemetry-off")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			});
			proc?.WaitForExit(20000);
		}
		catch { }
	}

	public static int RemoveRules()
	{
		var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
		if (type == null) return 0;
		dynamic policy = Activator.CreateInstance(type)!;
		dynamic rules = policy.Rules;
		var names = new List<string>();
		foreach (dynamic rule in rules)
		{
			try
			{
				string name = rule.Name;
				string group = "";
				try { group = rule.Grouping ?? ""; } catch { }
				if (Tagged(name, group)) names.Add(name);
			}
			catch { }
		}
		foreach (var name in names)
		{
			try { rules.Remove(name); } catch { }
		}
		return names.Count;
	}

	static void DeleteData()
	{
		var dir = DataDir();
		try
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
		}
		catch { }
	}

	static void RemoveLinks()
	{
		TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GUARDEX.lnk"));
		TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GUARDEX Browser.lnk"));
		TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "GUARDEX Browser.lnk"));
	}

	static void TryDelete(string path)
	{
		try { if (File.Exists(path)) File.Delete(path); } catch { }
	}

	static void RemoveTask()
	{
		try
		{
			using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /F /TN GUARDEX")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			});
			proc?.WaitForExit(8000);
		}
		catch { }
	}

	static string ReadThumb()
	{
		try
		{
			using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GUARDEX");
			var thumb = key?.GetValue("GuardexCert") as string ?? "";
			thumb = thumb.Replace(" ", "").Trim();
			return thumb.Length == 40 && thumb.All(Uri.IsHexDigit) ? thumb : "";
		}
		catch
		{
			return "";
		}
	}

	static void RemoveCert(string thumb)
	{
		if (thumb.Length == 0) return;
		foreach (var name in new[] { StoreName.My, StoreName.TrustedPublisher, StoreName.Root })
		{
			try
			{
				using var store = new X509Store(name, StoreLocation.LocalMachine);
				store.Open(OpenFlags.ReadWrite);
				var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
				foreach (var cert in found) store.Remove(cert);
			}
			catch { }
		}
	}

	static void RemoveKey()
	{
		try
		{
			using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			hive.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GUARDEX", false);
		}
		catch { }
	}

	static void ScheduleDelete(string dir)
	{
		if (!Directory.Exists(dir)) return;
		try
		{
			var cmd = "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"";
			Process.Start(new ProcessStartInfo("cmd.exe", cmd)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
		}
		catch { }
	}
}

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Guardex;

// Named Windows diagnostic reporting. Update, Defender, and sign-in stay as they are.
static class Telemetry
{
	const string Begin = "# GUARDEX-Telemetry begin";
	const string End = "# GUARDEX-Telemetry end";
	const string RulePrefix = "GUARDEX-Telemetry ";

	static readonly string[] Hosts =
	[
		"vortex.data.microsoft.com",
		"vortex-win.data.microsoft.com",
		"v10.vortex-win.data.microsoft.com",
		"v20.vortex-win.data.microsoft.com",
		"v10.events.data.microsoft.com",
		"v20.events.data.microsoft.com",
		"us-v10.events.data.microsoft.com",
		"eu-v10.events.data.microsoft.com",
		"eu-v20.events.data.microsoft.com",
		"uk-v20.events.data.microsoft.com",
		"us4-v20.events.data.microsoft.com",
		"us5-v20.events.data.microsoft.com",
		"self.events.data.microsoft.com",
		"browser.events.data.microsoft.com",
		"mobile.events.data.microsoft.com",
		"functional.events.data.microsoft.com",
		"v10c.events.data.microsoft.com",
		"v20c.events.data.microsoft.com",
		"telecommand.telemetry.microsoft.com",
		"telemetry.microsoft.com",
		"oca.telemetry.microsoft.com",
		"sqm.telemetry.microsoft.com",
		"watson.telemetry.microsoft.com",
		"watson.ppe.telemetry.microsoft.com",
		"df.telemetry.microsoft.com",
		"wes.telemetry.microsoft.com",
		"reports.wes.df.telemetry.microsoft.com",
		"services.wes.df.telemetry.microsoft.com",
		"sqm.df.telemetry.microsoft.com",
		"telemetry.appex.bing.net",
		"telemetry.urs.microsoft.com",
		"settings-sandbox.data.microsoft.com",
		"vortex-sandbox.data.microsoft.com",
		"survey.watson.microsoft.com",
		"watson.live.com",
		"watson.microsoft.com",
		"feedback.windows.com",
		"feedback.microsoft-hohm.com",
		"feedback.search.microsoft.com",
		"diagnostics.support.microsoft.com",
		"redir.metaservices.microsoft.com",
		"choice.microsoft.com",
		"oca.microsoft.com",
		"onesettings-bn2.metron.live.com.nsatc.net",
		"pre.footprintpredict.com",
		"activity.windows.com",
		"pipe.aria.microsoft.com",
		"mobile.pipe.aria.microsoft.com",
		"browser.pipe.aria.microsoft.com",
		"data.microsoft.com",
		"settings-win.data.microsoft.com"
	];

	static readonly string[] Programs =
	[
		"CompatTelRunner.exe",
		"DeviceCensus.exe",
		"wsqmcons.exe",
		"WerFault.exe",
		"WerFaultSecure.exe",
		"wermgr.exe"
	];

	static readonly string[] Services =
	[
		"DiagTrack",
		"dmwappushservice",
		"diagnosticshub.standardcollector.service",
		"WerSvc"
	];

	static readonly string[] Tasks =
	[
		@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
		@"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
		@"\Microsoft\Windows\Application Experience\StartupAppTask",
		@"\Microsoft\Windows\Application Experience\PcaPatchDbTask",
		@"\Microsoft\Windows\Application Experience\MareBackup",
		@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
		@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
		@"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
		@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
		@"\Microsoft\Windows\Feedback\Siuf\DmClient",
		@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
		@"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
		@"\Microsoft\Windows\NetTrace\GatherNetworkInfo",
		@"\Microsoft\Windows\PI\Sqm-Tasks",
		@"\Microsoft\Windows\Device Information\Device",
		@"\Microsoft\Windows\Device Information\Device User"
	];

	static readonly Policy[] Policies =
	[
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowDeviceNameInTelemetry", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "LimitDiagnosticLogCollection", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
		new(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "DoNotShowFeedbackNotifications", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\TabletPC", "PreventHandwritingDataSharing", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports", "PreventHandwritingErrorReports", 1),
		new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
		new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)
	];

	static readonly object Gate = new();

	public static bool Wanted()
	{
		try
		{
			var path = FlagPath();
			if (!File.Exists(path)) return true;
			var text = File.ReadAllText(path).Trim();
			return text != "0";
		}
		catch
		{
			return true;
		}
	}

	public static string Apply(bool on)
	{
		lock (Gate)
		{
			SaveWanted(on);
			if (!Elevated())
				return on
					? "Telemetry blocking is on, but this process is not elevated, so the hosts file, firewall, services, and tasks are unchanged."
					: "Telemetry blocking is off. This process is not elevated, so an earlier block could not be removed.";
			var notes = new List<string>();
			try
			{
				if (on) EnsureBackup();
				notes.Add(WriteHosts(on));
				notes.Add(on ? BlockPrograms() : RemoveProgramRules());
				notes.Add(on ? DisableServices() : RestoreServices());
				notes.Add(on ? DisableTasks() : RestoreTasks());
				notes.Add(on ? WritePolicies() : RestorePolicies());
				if (!on) TryDeleteBackup();
			}
			catch (Exception ex)
			{
				notes.Add(ex.Message);
			}
			var head = on
				? "Telemetry blocking is on. Update and Defender were left alone."
				: "Telemetry blocking is off.";
			return head + " " + string.Join(" ", notes.Where(n => n.Length > 0));
		}
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!Hosts.Contains("vortex.data.microsoft.com")) failures.Add("vortex");
		if (Hosts.Any(ForbiddenHost)) failures.Add("update or sign-in host");
		if (!Programs.Contains("CompatTelRunner.exe")) failures.Add("compattel");
		if (Programs.Any(p => p.Contains("MsMpEng", StringComparison.OrdinalIgnoreCase) || p.Contains("wuau", StringComparison.OrdinalIgnoreCase)))
			failures.Add("update or defender program");
		foreach (var name in new[] { "wuauserv", "WinDefend", "UsoSvc", "WdNisSvc", "SecurityHealthService", "DPS", "PcaSvc" })
			if (Services.Contains(name, StringComparer.OrdinalIgnoreCase)) failures.Add("service " + name);
		if (!Tasks.Any(t => t.EndsWith("Microsoft Compatibility Appraiser", StringComparison.Ordinal))) failures.Add("appraiser");
		if (Tasks.Any(t => t.Contains(@"Autochk\Proxy", StringComparison.OrdinalIgnoreCase) || t.EndsWith("CreateObjectTask", StringComparison.Ordinal)))
			failures.Add("kept task");
		if (RulePrefix.StartsWith("GUARDEX ", StringComparison.Ordinal)) failures.Add("firewall prefix");
		if (Policies.Any(p => p.Name is "DisableAntiSpyware" or "DisableRealtimeMonitoring" or "DisableWindowsUpdate" or "NoAutoUpdate" or "DisableLocation"))
			failures.Add("policy");
		var block = RenderHosts();
		if (!block.StartsWith(Begin, StringComparison.Ordinal) || !block.Contains(End, StringComparison.Ordinal)) failures.Add("markers");
		if (!block.Contains("0.0.0.0 vortex.data.microsoft.com", StringComparison.Ordinal)) failures.Add("v4 vortex");
		if (!block.Contains("::1 vortex.data.microsoft.com", StringComparison.Ordinal)) failures.Add("v6 vortex");
		if (block.Contains("windowsupdate", StringComparison.OrdinalIgnoreCase) || block.Contains("login.live.com", StringComparison.OrdinalIgnoreCase))
			failures.Add("hosts leaked");
		var prior = "127.0.0.1 localhost\r\n";
		var once = MergeHosts(prior, true);
		var twice = MergeHosts(once, true);
		if (CountOf(twice, Begin) != 1 || CountOf(twice, End) != 1) failures.Add("hosts duplicated");
		var cleared = MergeHosts(twice, false);
		if (cleared.Contains(Begin, StringComparison.Ordinal) || !cleared.Contains("localhost", StringComparison.Ordinal))
			failures.Add("hosts undo");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static bool ForbiddenHost(string host)
	{
		if (host.Contains("windowsupdate", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Contains("update.microsoft.com", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Equals("win10.ipv6.microsoft.com", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Equals("corp.sts.microsoft.com", StringComparison.OrdinalIgnoreCase)) return true;
		if (host.Contains("wdcp", StringComparison.OrdinalIgnoreCase)) return true;
		return false;
	}

	static void SaveWanted(bool on)
	{
		var dir = Dir();
		Directory.CreateDirectory(dir);
		File.WriteAllText(FlagPath(), on ? "1" : "0");
	}

	static void EnsureBackup()
	{
		var path = BackupPath();
		if (File.Exists(path)) return;
		var lines = new List<string>();
		foreach (var name in Services)
			lines.Add("svc\t" + name + "\t" + ServiceStart(name));
		foreach (var task in Tasks)
			lines.Add("task\t" + task + "\t" + TaskState(task));
		foreach (var policy in Policies)
			lines.Add(PolicyBackup(policy));
		File.WriteAllLines(path, lines);
	}

	static string WriteHosts(bool on)
	{
		var path = HostsPath();
		var bytes = File.ReadAllBytes(path);
		var text = Encoding.UTF8.GetString(bytes);
		if (text.Contains('\uFFFD')) text = Encoding.Latin1.GetString(bytes);
		var merged = MergeHosts(text, on);
		File.WriteAllText(path, merged, new UTF8Encoding(false));
		return on ? "Hosts list is in place." : "Hosts list was removed.";
	}

	static string MergeHosts(string text, bool include)
	{
		var stripped = Strip(text).TrimEnd();
		if (!include) return stripped.Length == 0 ? "" : stripped + Environment.NewLine;
		if (stripped.Length > 0) stripped += Environment.NewLine;
		return stripped + RenderHosts() + Environment.NewLine;
	}

	static string Strip(string text)
	{
		var begin = text.IndexOf(Begin, StringComparison.Ordinal);
		if (begin < 0) return text;
		var end = text.IndexOf(End, begin, StringComparison.Ordinal);
		if (end < 0) return text;
		end += End.Length;
		if (end < text.Length && text[end] == '\r') end++;
		if (end < text.Length && text[end] == '\n') end++;
		return text.Remove(begin, end - begin);
	}

	static string RenderHosts()
	{
		var sb = new StringBuilder();
		sb.Append(Begin).Append('\n');
		foreach (var host in Hosts)
		{
			sb.Append("0.0.0.0 ").Append(host).Append('\n');
			sb.Append("::1 ").Append(host).Append('\n');
		}
		sb.Append(End);
		return sb.ToString();
	}

	static string BlockPrograms()
	{
		RemoveProgramRules();
		var added = 0;
		foreach (var name in Programs)
		{
			var path = Path.Combine(Environment.SystemDirectory, name);
			if (!File.Exists(path)) continue;
			AddProgramRule(RulePrefix + name, path);
			added++;
		}
		return added + " outbound program rules are set.";
	}

	static string RemoveProgramRules()
	{
		dynamic policy = Firewall();
		dynamic rules = policy.Rules;
		var names = new List<string>();
		foreach (dynamic rule in rules)
		{
			try
			{
				string name = rule.Name;
				if (name.StartsWith(RulePrefix, StringComparison.Ordinal)) names.Add(name);
			}
			catch { }
		}
		foreach (var name in names)
		{
			try { rules.Remove(name); } catch { }
		}
		return "Outbound program rules were removed.";
	}

	static void AddProgramRule(string name, string path)
	{
		dynamic policy = Firewall();
		dynamic rules = policy.Rules;
		try { rules.Remove(name); } catch { }
		dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
		rule.Name = name;
		rule.Description = "GUARDEX telemetry block";
		rule.ApplicationName = path;
		rule.Action = 0;
		rule.Direction = 2;
		rule.Enabled = true;
		rule.InterfaceTypes = "All";
		rule.Protocol = 256;
		rule.Profiles = 0x7FFFFFFF;
		rules.Add(rule);
	}

	static string DisableServices()
	{
		var tool = Tool("sc.exe");
		var changed = 0;
		foreach (var name in Services)
		{
			if (ServiceStart(name) == "missing") continue;
			Run(tool, "stop", name);
			if (Run(tool, "config", name, "start=", "disabled") == 0) changed++;
		}
		return changed + " diagnostic services are disabled.";
	}

	static string RestoreServices()
	{
		var rows = BackupRows("svc");
		if (rows.Count == 0) return "No service backup was found.";
		var tool = Tool("sc.exe");
		foreach (var row in rows)
		{
			if (row.Length < 3 || row[2] == "missing") continue;
			if (!int.TryParse(row[2], out var type)) continue;
			Run(tool, "config", row[1], "start=", StartArg(type));
		}
		return "Diagnostic services were restored.";
	}

	static string DisableTasks()
	{
		var tool = Tool("schtasks.exe");
		var changed = 0;
		foreach (var task in Tasks)
		{
			if (TaskState(task) == "missing") continue;
			if (Run(tool, "/Change", "/TN", task, "/DISABLE") == 0) changed++;
		}
		return changed + " diagnostic tasks are disabled.";
	}

	static string RestoreTasks()
	{
		var rows = BackupRows("task");
		if (rows.Count == 0) return "No task backup was found.";
		var tool = Tool("schtasks.exe");
		foreach (var row in rows)
		{
			if (row.Length < 3 || row[2] == "missing") continue;
			var verb = row[2] == "enabled" ? "/ENABLE" : "/DISABLE";
			Run(tool, "/Change", "/TN", row[1], verb);
		}
		return "Diagnostic tasks were restored.";
	}

	static string WritePolicies()
	{
		var wrote = 0;
		foreach (var policy in Policies)
		{
			if (PolicyKind(policy) == "skip") continue;
			using var key = Base(policy.Hive).CreateSubKey(policy.Sub, true);
			key?.SetValue(policy.Name, policy.Data, RegistryValueKind.DWord);
			wrote++;
		}
		return wrote + " reporting policies are set.";
	}

	static string RestorePolicies()
	{
		var rows = ReadBackup().Where(row => row.Length > 0 && row[0].StartsWith("reg", StringComparison.Ordinal)).ToList();
		if (rows.Count == 0) return "No policy backup was found.";
		foreach (var row in rows)
		{
			if (row[0] == "reg-skip" || row.Length < 4) continue;
			if (!TryHive(row[1], out var hive)) continue;
			using var key = Base(hive).CreateSubKey(row[2], true);
			if (key == null) continue;
			if (row[0] == "reg-absent") key.DeleteValue(row[3], false);
			else if (row.Length >= 5 && int.TryParse(row[4], out var data)) key.SetValue(row[3], data, RegistryValueKind.DWord);
		}
		return "Reporting policies were restored.";
	}

	static string PolicyBackup(Policy policy)
	{
		var kind = PolicyKind(policy);
		var hive = policy.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
		if (kind == "missing") return "reg-absent\t" + hive + "\t" + policy.Sub + "\t" + policy.Name;
		if (kind == "skip") return "reg-skip\t" + hive + "\t" + policy.Sub + "\t" + policy.Name;
		return "reg\t" + hive + "\t" + policy.Sub + "\t" + policy.Name + "\t" + kind;
	}

	static string PolicyKind(Policy policy)
	{
		using var key = Base(policy.Hive).OpenSubKey(policy.Sub);
		if (key == null) return "missing";
		var value = key.GetValue(policy.Name);
		if (value == null) return "missing";
		return value is int number ? number.ToString() : "skip";
	}

	static string ServiceStart(string name)
	{
		using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
		if (key?.GetValue("Start") is int number) return number.ToString();
		return "missing";
	}

	static string TaskState(string task)
	{
		var path = Path.Combine(Environment.SystemDirectory, "Tasks", task.TrimStart('\\'));
		if (!File.Exists(path)) return "missing";
		try
		{
			var text = ReadText(path);
			var at = text.IndexOf("<Enabled>", StringComparison.OrdinalIgnoreCase);
			if (at < 0) return "enabled";
			var end = text.IndexOf('<', at + 1);
			if (end < 0) end = Math.Min(text.Length, at + 32);
			var window = text.Substring(at, end - at);
			return window.Contains("false", StringComparison.OrdinalIgnoreCase) ? "disabled" : "enabled";
		}
		catch
		{
			return "enabled";
		}
	}

	static List<string[]> BackupRows(string kind) =>
		ReadBackup().Where(row => row.Length > 1 && row[0] == kind).ToList();

	static List<string[]> ReadBackup()
	{
		var path = BackupPath();
		if (!File.Exists(path)) return [];
		return File.ReadAllLines(path).Select(line => line.Split('\t')).ToList();
	}

	static void TryDeleteBackup()
	{
		try
		{
			var path = BackupPath();
			if (File.Exists(path)) File.Delete(path);
		}
		catch { }
	}

	static string ReadText(string path)
	{
		var bytes = File.ReadAllBytes(path);
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);
		var zeros = 0;
		for (var i = 1; i < bytes.Length && i < 80; i += 2)
			if (bytes[i] == 0) zeros++;
		if (zeros > 8) return Encoding.Unicode.GetString(bytes);
		return Encoding.UTF8.GetString(bytes);
	}

	static int Run(string file, params string[] args)
	{
		try
		{
			var start = new ProcessStartInfo(file)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var arg in args) start.ArgumentList.Add(arg);
			using var process = Process.Start(start);
			if (process == null) return -1;
			if (!process.WaitForExit(20000))
			{
				try { process.Kill(true); } catch { }
				return -1;
			}
			return process.ExitCode;
		}
		catch
		{
			return -1;
		}
	}

	static string StartArg(int type) => type switch
	{
		0 => "boot",
		1 => "system",
		2 => "auto",
		3 => "demand",
		_ => "disabled"
	};

	static string Tool(string name) => Path.Combine(Environment.SystemDirectory, name);

	static string HostsPath() => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

	static dynamic Firewall()
	{
		var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
		if (type == null) throw new InvalidOperationException("Firewall API is unavailable.");
		return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Firewall API is unavailable.");
	}

	static bool Elevated()
	{
		try
		{
			using var id = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

	static bool TryHive(string text, out RegistryHive hive)
	{
		if (text == "HKCU")
		{
			hive = RegistryHive.CurrentUser;
			return true;
		}
		if (text == "HKLM")
		{
			hive = RegistryHive.LocalMachine;
			return true;
		}
		hive = default;
		return false;
	}

	static int CountOf(string text, string token)
	{
		var count = 0;
		var at = 0;
		while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
		{
			count++;
			at += token.Length;
		}
		return count;
	}

	static string Dir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");

	static string FlagPath() => Path.Combine(Dir(), "telemetry-on.txt");

	static string BackupPath() => Path.Combine(Dir(), "telemetry-backup.txt");

	readonly record struct Policy(RegistryHive Hive, string Sub, string Name, int Data);
}

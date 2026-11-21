using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Win32;

namespace Guardex.Setup;

static class SetupWork
{
	public const string DesktopRuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
	public const string DesktopRuntimeFallbackUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe";
	public const string WebView2Url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	public static bool Official(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
		if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
		var host = uri.IdnHost;
		if (host.Equals("aka.ms", StringComparison.OrdinalIgnoreCase)) return true;
		return host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);
	}

	public static bool HasPayload()
	{
		var asm = typeof(SetupWork).Assembly;
		return asm.GetManifestResourceStream("payload.zip") != null;
	}

	public static bool HasDesktopRuntime()
	{
		foreach (var root in new[]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
		})
		{
			if (!Directory.Exists(root)) continue;
			foreach (var dir in Directory.GetDirectories(root))
			{
				if (Path.GetFileName(dir).StartsWith("10.", StringComparison.Ordinal)) return true;
			}
		}
		return false;
	}

	public static bool HasWebView2()
	{
		string[] keys =
		[
			@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
			@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
		];
		foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
		{
			foreach (var path in keys)
			{
				using var key = hive.OpenSubKey(path);
				var pv = key?.GetValue("pv") as string;
				if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return true;
			}
		}
		return false;
	}

	public static async Task Download(string url, string dest, IProgress<double> progress, CancellationToken ct)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
		using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		resp.EnsureSuccessStatusCode();
		var total = resp.Content.Headers.ContentLength ?? 0;
		await using var src = await resp.Content.ReadAsStreamAsync(ct);
		Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
		await using var dst = File.Create(dest);
		var buffer = new byte[81920];
		long read = 0;
		int n;
		while ((n = await src.ReadAsync(buffer, ct)) > 0)
		{
			await dst.WriteAsync(buffer.AsMemory(0, n), ct);
			read += n;
			if (total > 0) progress.Report(read / (double)total);
		}
	}

	public static async Task<int> RunInstaller(string path, string arguments, CancellationToken ct)
	{
		var psi = new ProcessStartInfo(path, arguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + path);
		await proc.WaitForExitAsync(ct);
		return proc.ExitCode;
	}

	public static bool InstallerOk(int code) => code is 0 or 1638 or 3010;

	public static void ExtractPayload(string dest)
	{
		var asm = typeof(SetupWork).Assembly;
		using var stream = asm.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("The setup file does not contain GUARDEX.");
		Directory.CreateDirectory(dest);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		foreach (var entry in zip.Entries)
		{
			if (string.IsNullOrEmpty(entry.Name)) continue;
			var target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
			var root = Path.GetFullPath(dest + Path.DirectorySeparatorChar);
			if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The package tried to write outside the install folder.");
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			entry.ExtractToFile(target, true);
		}
	}

	public static void Shortcut(string exe)
	{
		var menu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
		Link(menu, "GUARDEX.lnk", exe, "GUARDEX");
	}

	public static void BrowserLinks(string exe)
	{
		var menu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
		Link(menu, "GUARDEX Browser.lnk", exe, "GUARDEX Browser");
		var desk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
		Link(desk, "GUARDEX Browser.lnk", exe, "GUARDEX Browser");
	}

	static void Link(string folder, string name, string exe, string description)
	{
		Directory.CreateDirectory(folder);
		var lnk = Path.Combine(folder, name);
		var type = Type.GetTypeFromProgID("WScript.Shell");
		if (type == null) return;
		dynamic shell = Activator.CreateInstance(type)!;
		dynamic link = shell.CreateShortcut(lnk);
		link.TargetPath = exe;
		link.WorkingDirectory = Path.GetDirectoryName(exe)!;
		link.Description = description;
		link.IconLocation = exe + ",0";
		link.Save();
	}

	public static string ThisVersion()
	{
		var version = typeof(SetupWork).Assembly.GetName().Version;
		if (version == null) return "0.6.1";
		var patch = version.Build < 0 ? 0 : version.Build;
		return version.Major + "." + version.Minor + "." + patch;
	}

	public static string InstalledVersion()
	{
		try
		{
			using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
			using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GUARDEX");
			if (key?.GetValue("DisplayVersion") is string listed && listed.Trim().Length > 0)
				return listed.Trim();
		}
		catch { }
		var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GUARDEX", "Guardex.exe");
		if (!File.Exists(exe)) return "";
		var file = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
		if (file.Length == 0 || file == "0.0.0.0") return "0.0.0";
		return file;
	}

	public static string Relation(string installed, string setup)
	{
		if (string.IsNullOrWhiteSpace(installed)) return "install";
		var cmp = Parts(installed).CompareTo(Parts(setup));
		if (cmp < 0) return "upgrade";
		if (cmp > 0) return "replace";
		return "repair";
	}

	static (int Major, int Minor, int Patch) Parts(string text)
	{
		var bits = text.Split('.', '+', '-', ' ');
		int At(int i)
		{
			if (bits.Length <= i) return 0;
			var digits = new string(bits[i].TakeWhile(char.IsDigit).ToArray());
			return int.TryParse(digits, out var n) ? n : 0;
		}
		return (At(0), At(1), At(2));
	}

	public static void StopApps()
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

	public static void Register(string dir, string version, string thumbprint)
	{
		var uninstall = Path.Combine(dir, "GUARDEX-Uninstall.exe");
		using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
		using var key = hive.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GUARDEX");
		key.SetValue("DisplayName", "GUARDEX");
		key.SetValue("DisplayVersion", version);
		key.SetValue("Publisher", "GUARDEX");
		key.SetValue("InstallLocation", dir);
		key.SetValue("DisplayIcon", Path.Combine(dir, "Guardex.exe"));
		key.SetValue("UninstallString", "\"" + uninstall + "\"");
		key.SetValue("NoModify", 1, RegistryValueKind.DWord);
		key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
		var bytes = DirBytes(dir);
		key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
		if (thumbprint.Length > 0) key.SetValue("GuardexCert", thumbprint);
	}

	static long DirBytes(string dir)
	{
		long total = 0;
		try
		{
			foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
			{
				try { total += new FileInfo(file).Length; } catch { }
			}
		}
		catch { }
		return total;
	}

	public static string SignInstalled(string dir)
	{
		var script = Path.Combine(Path.GetTempPath(), "guardex-sign-" + Guid.NewGuid().ToString("n") + ".ps1");
		File.WriteAllText(script, """
			$ErrorActionPreference = 'Stop'
			$dir = $args[0]
			$cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=GUARDEX' } | Sort-Object NotAfter -Descending | Select-Object -First 1
			if (-not $cert) {
			  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=GUARDEX' -CertStoreLocation Cert:\LocalMachine\My -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
			}
			foreach ($name in @('TrustedPublisher','Root')) {
			  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, 'LocalMachine')
			  $store.Open('ReadWrite')
			  $store.Add($cert)
			  $store.Close()
			}
			foreach ($file in @('Guardex.exe','GuardexBrowser.exe','GUARDEX-Uninstall.exe')) {
			  $path = Join-Path $dir $file
			  if (Test-Path $path) { Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 | Out-Null }
			}
			Write-Output $cert.Thumbprint
			""");
		try
		{
			using var proc = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" \"" + dir + "\"")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			if (proc == null) return "";
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(60000);
			if (proc.ExitCode != 0) return "";
			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var thumb = line.Replace(" ", "");
				if (thumb.Length == 40 && thumb.All(Uri.IsHexDigit)) return thumb.ToUpperInvariant();
			}
			return "";
		}
		catch
		{
			return "";
		}
		finally
		{
			try { File.Delete(script); } catch { }
		}
	}

	public static void SignInTask(string exe, bool enabled)
	{
		if (!enabled)
		{
			RunQuiet("schtasks.exe", "/Delete /F /TN GUARDEX");
			return;
		}
		var tr = "/TR \"\\\"" + exe + "\\\"\"";
		var args = "/Create /F /TN GUARDEX /SC ONLOGON /RL HIGHEST " + tr;
		RunQuiet("schtasks.exe", args);
	}

	static void RunQuiet(string file, string arguments)
	{
		try
		{
			using var proc = Process.Start(new ProcessStartInfo(file, arguments)
			{
				UseShellExecute = false,
				CreateNoWindow = true
			});
			proc?.WaitForExit(15000);
		}
		catch { }
	}
}

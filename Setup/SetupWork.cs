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
		link.Save();
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

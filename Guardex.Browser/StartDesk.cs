using System.IO;
using System.Text.Json;
using Guardex;

namespace Guardex.Browser;

static class StartDesk
{
	public const string Page = "page";
	public const string Custom = "custom";
	public const string Home = "home";

	public static string HomeMode { get; private set; } = Page;
	public static string HomeUrl { get; private set; } = "";
	public static string TabMode { get; private set; } = Home;
	public static string TabUrl { get; private set; } = "";

	static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "start.json");

	public static void Load()
	{
		HomeMode = Page;
		HomeUrl = "";
		TabMode = Home;
		TabUrl = "";
		try
		{
			if (!File.Exists(FilePath)) return;
			var file = JsonSerializer.Deserialize<StartFile>(File.ReadAllText(FilePath));
			if (file == null) return;
			HomeMode = file.Home == Custom ? Custom : Page;
			HomeUrl = Clip(file.HomeUrl);
			TabMode = file.Tab == Page ? Page : file.Tab == Custom ? Custom : Home;
			TabUrl = Clip(file.TabUrl);
		}
		catch
		{
		}
	}

	public static void SetHome(string? mode, string? url)
	{
		HomeMode = mode == Custom ? Custom : Page;
		HomeUrl = Clip(url);
		Save();
	}

	public static void SetTab(string? mode, string? url)
	{
		TabMode = mode == Page ? Page : mode == Custom ? Custom : Home;
		TabUrl = Clip(url);
		Save();
	}

	public static string HomeTarget() => HomeTarget(HomeMode, HomeUrl);

	public static string HomeTarget(string? mode, string? url) => mode == Custom ? PageTarget(url) : "";

	public static string TabTarget() => TabTarget(TabMode, TabUrl, HomeMode, HomeUrl);

	public static string TabTarget(string? tabMode, string? tabUrl, string? homeMode, string? homeUrl)
	{
		if (tabMode == Page) return "";
		if (tabMode == Custom) return PageTarget(tabUrl);
		return HomeTarget(homeMode, homeUrl);
	}

	public static string PageTarget(string? text)
	{
		var raw = (text ?? "").Trim();
		if (raw.Contains("://", StringComparison.Ordinal))
		{
			if (!Uri.TryCreate(raw, UriKind.Absolute, out var stated) || stated.Scheme is not ("http" or "https"))
				return "";
		}
		if (!LinkRules.TryPage(raw, out var url, out _)) return "";
		return SiteDesk.IsPage(url) ? url : "";
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (HomeTarget(Page, "https://example.com/") != "") failures.Add("home page");
		if (HomeTarget(Custom, "https://example.com/a") != "https://example.com/a") failures.Add("home custom");
		if (PageTarget("example.com") != "https://example.com/") failures.Add("home bare");
		if (HomeTarget(Custom, "file:///c/windows") != "") failures.Add("home file");
		if (HomeTarget(Custom, "javascript:alert(1)") != "") failures.Add("home js");
		if (HomeTarget(Custom, "about:blank") != "") failures.Add("home blank");
		if (HomeTarget("nope", "") != "") failures.Add("home bad");
		if (TabTarget(Home, "", Custom, "https://example.com/a") != "https://example.com/a") failures.Add("tab home");
		if (TabTarget(Page, "https://example.com/a", Custom, "https://example.com/a") != "") failures.Add("tab page");
		if (TabTarget(Custom, "https://example.org/t", Page, "") != "https://example.org/t") failures.Add("tab custom");
		if (TabTarget(Custom, "about:blank", Custom, "https://example.com/a") != "") failures.Add("tab blank");
		if (TabTarget(Home, "", Page, "") != "") failures.Add("tab default");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static void Save()
	{
		try
		{
			var dir = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			var file = new StartFile { Home = HomeMode, HomeUrl = HomeUrl, Tab = TabMode, TabUrl = TabUrl };
			File.WriteAllText(FilePath, JsonSerializer.Serialize(file));
		}
		catch
		{
		}
	}

	static string Clip(string? text)
	{
		var value = (text ?? "").Trim().Replace("\r", "").Replace("\n", "").Replace("\t", "");
		return value.Length <= 2000 ? value : value[..2000];
	}

	sealed class StartFile
	{
		public string Home { get; set; } = "page";
		public string HomeUrl { get; set; } = "";
		public string Tab { get; set; } = "home";
		public string TabUrl { get; set; } = "";
	}
}

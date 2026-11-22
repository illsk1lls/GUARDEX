using System.Collections.ObjectModel;
using Guardex;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Guardex.Browser;

public sealed class HomeShortcut : INotifyPropertyChanged
{
	ImageSource? _icon;
	string _name = "";
	string _url = "";

	public string Name
	{
		get => _name;
		set
		{
			if (_name == value) return;
			_name = value;
			Changed(nameof(Name));
		}
	}

	public string Url
	{
		get => _url;
		set
		{
			if (_url == value) return;
			_url = value;
			Changed(nameof(Url));
			Changed(nameof(Mark));
		}
	}

	[JsonIgnore]
	public string Mark => Favicon.Letter(Url);

	[JsonIgnore]
	public ImageSource? Icon
	{
		get => _icon;
		set
		{
			if (_icon == value) return;
			_icon = value;
			Changed(nameof(Icon));
		}
	}

	public void RefreshIcon() => Icon = Favicon.Image(Url);

	public event PropertyChangedEventHandler? PropertyChanged;
	void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

static class HomeDesk
{
	public const int Max = 40;
	public static readonly ObservableCollection<HomeShortcut> Items = new();
	public static string Provider { get; private set; } = "google";

	static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "home.json");

	public static string Label(string? provider) => (provider ?? "").ToLowerInvariant() switch
	{
		"bing" => "Bing",
		"duckduckgo" => "DuckDuckGo",
		_ => "Google"
	};

	public static string SearchUrl(string? provider, string? query)
	{
		var text = Uri.EscapeDataString((query ?? "").Trim());
		return (provider ?? "").ToLowerInvariant() switch
		{
			"bing" => "https://www.bing.com/search?q=" + text,
			"duckduckgo" => "https://duckduckgo.com/?q=" + text,
			_ => "https://www.google.com/search?q=" + text
		};
	}

	public static void Load()
	{
		Items.Clear();
		try
		{
			if (!File.Exists(FilePath)) return;
			Apply(File.ReadAllText(FilePath));
		}
		catch
		{
		}
	}

	public static void Save() => Write(Provider, Items);

	public static void SetProvider(string? provider)
	{
		Provider = NormalizeProvider(provider);
		Save();
	}

	public static bool TryAdd(string? name, string? url, out string error)
	{
		if (!TryLink(name, url, out var link, out error)) return false;
		if (Items.Count >= Max)
		{
			error = "The home page holds " + Max + " shortcuts.";
			return false;
		}
		Items.Add(new HomeShortcut { Name = link.Name, Url = link.Url });
		Items[^1].RefreshIcon();
		Save();
		return true;
	}

	public static bool TryUpdate(HomeShortcut item, string? name, string? url, out string error)
	{
		if (!TryLink(name, url, out var link, out error)) return false;
		item.Name = link.Name;
		item.Url = link.Url;
		item.RefreshIcon();
		Save();
		return true;
	}

	public static void Remove(HomeShortcut item)
	{
		Items.Remove(item);
		Save();
	}

	public static void Clear()
	{
		Items.Clear();
		Save();
	}

	public static string ExportText() => WriteText(Provider, Items.Select(item => new HomeLink(item.Name, item.Url)).ToList());

	public static bool TryImport(string? json, out string error)
	{
		try
		{
			var read = Read(json);
			if (read.Links.Count == 0)
			{
				error = "That file has no shortcuts to import.";
				return false;
			}
			ApplyRead(read);
			Save();
			error = read.Links.Count == 1 ? "Imported 1 shortcut." : "Imported " + read.Links.Count + " shortcuts.";
			return true;
		}
		catch
		{
			error = "That shortcut file could not be read.";
			return false;
		}
	}

	public static bool TryLink(string? name, string? url, out HomeLink link, out string error)
	{
		link = default;
		var cleanName = SiteDesk.OneLine(name);
		var cleanUrl = (url ?? "").Trim();
		if (cleanName.Length == 0)
		{
			error = "Enter a name.";
			return false;
		}
		if (!SiteDesk.IsPage(cleanUrl))
		{
			error = "Enter an http or https address.";
			return false;
		}
		link = new HomeLink(cleanName, cleanUrl);
		error = "";
		return true;
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!SearchUrl("google", "cats dogs").StartsWith("https://www.google.com/search?q=", StringComparison.Ordinal))
			failures.Add("google");
		if (SearchUrl("bing", "a") != "https://www.bing.com/search?q=a") failures.Add("bing");
		if (SearchUrl("duckduckgo", "a b") != "https://duckduckgo.com/?q=a%20b") failures.Add("duck");
		if (Label("nope") != "Google") failures.Add("label");
		if (!TryLink(" Example ", "https://example.com/a", out var link, out _) || link.Name != "Example")
			failures.Add("link");
		if (TryLink("", "https://example.com/", out _, out _)) failures.Add("empty name");
		if (TryLink("x", "file:///c/windows", out _, out _)) failures.Add("file url");
		var text = WriteText("bing", [new HomeLink("Example", "https://example.com/"), new HomeLink("Skip", "about:blank")]);
		var read = Read(text);
		if (!read.HasProvider || read.Provider != "bing" || read.Links.Count != 1 || read.Links[0].Url != "https://example.com/")
			failures.Add("roundtrip");
		var quick = Read("""[{"name":"Box","url":"http://nas:31014/"},{"name":"Skip","url":"about:blank"}]""");
		if (quick.HasProvider || quick.Links.Count != 1 || quick.Links[0].Name != "Box" || quick.Links[0].Url != "http://nas:31014/")
			failures.Add("quicklaunch");
		var many = new List<HomeLink>();
		for (var i = 0; i < 50; i++) many.Add(new HomeLink("n" + i, "https://example.com/" + i));
		if (Read(WriteText("google", many)).Links.Count != Max) failures.Add("cap");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static int Apply(string? json)
	{
		var read = Read(json);
		ApplyRead(read);
		return Items.Count;
	}

	static void ApplyRead(HomeFile read)
	{
		if (read.HasProvider) Provider = read.Provider;
		Items.Clear();
		foreach (var link in read.Links)
		{
			var item = new HomeShortcut { Name = link.Name, Url = link.Url };
			item.RefreshIcon();
			Items.Add(item);
		}
	}

	static void Write(string provider, IEnumerable<HomeShortcut> items)
	{
		try
		{
			var path = FilePath;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, WriteText(provider, items.Select(item => new HomeLink(item.Name, item.Url)).ToList()));
		}
		catch
		{
		}
	}

	public readonly record struct HomeLink(string Name, string Url);

	sealed class FileShape
	{
		public string Provider { get; set; } = "google";
		public List<HomeLink> Shortcuts { get; set; } = [];
	}

	readonly record struct HomeFile(bool HasProvider, string Provider, List<HomeLink> Links);

	static HomeFile Read(string? json)
	{
		if (string.IsNullOrWhiteSpace(json)) return new HomeFile(false, "google", []);
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		var hasProvider = false;
		var provider = "google";
		JsonElement list;
		if (root.ValueKind == JsonValueKind.Array)
			list = root;
		else if (root.ValueKind == JsonValueKind.Object)
		{
			if (Field(root, "provider", out var providerValue) && providerValue.ValueKind == JsonValueKind.String)
			{
				hasProvider = true;
				provider = NormalizeProvider(providerValue.GetString());
			}
			if (!Field(root, "shortcuts", out list) || list.ValueKind != JsonValueKind.Array)
				return new HomeFile(hasProvider, provider, []);
		}
		else
			return new HomeFile(false, "google", []);
		var links = new List<HomeLink>();
		foreach (var item in list.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object) continue;
			var name = Field(item, "name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : "";
			var url = "";
			if (Field(item, "url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String) url = urlValue.GetString() ?? "";
			else if (Field(item, "href", out var href) && href.ValueKind == JsonValueKind.String) url = href.GetString() ?? "";
			if (!TryLink(name, url, out var link, out _)) continue;
			links.Add(link);
			if (links.Count >= Max) break;
		}
		return new HomeFile(hasProvider, provider, links);
	}

	static bool Field(JsonElement obj, string name, out JsonElement value)
	{
		foreach (var prop in obj.EnumerateObject())
		{
			if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				value = prop.Value;
				return true;
			}
		}
		value = default;
		return false;
	}

	static string WriteText(string provider, IReadOnlyList<HomeLink> links) =>
		JsonSerializer.Serialize(new FileShape
		{
			Provider = NormalizeProvider(provider),
			Shortcuts = links.Take(Max).ToList()
		});

	static string NormalizeProvider(string? provider)
	{
		var name = (provider ?? "").Trim().ToLowerInvariant();
		return name is "bing" or "duckduckgo" ? name : "google";
	}
}

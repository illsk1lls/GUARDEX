using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Guardex;

public sealed class FavoriteProfile
{
	public string Browser { get; init; } = "";
	public string Name { get; init; } = "";
	public string File { get; init; } = "";
}

public readonly record struct FavoriteMerge(int Added, int Already, bool Full);

static class Favorites
{
	public static string Label(string browser) => browser switch
	{
		"edge" => "Edge",
		"firefox" => "Firefox",
		_ => "Chrome"
	};

	public static string Missing(string browser) => Label(browser) + " bookmarks were not found on this account.";

	public static IReadOnlyList<FavoriteProfile> Profiles(string browser)
	{
		try
		{
			return browser switch
			{
				"edge" => Chromium(Path.Combine(Local(), "Microsoft", "Edge", "User Data"), "edge"),
				"firefox" => FirefoxProfiles(),
				_ => Chromium(Path.Combine(Local(), "Google", "Chrome", "User Data"), "chrome")
			};
		}
		catch
		{
			return [];
		}
	}

	public static List<SiteVisit> Read(FavoriteProfile profile)
	{
		if (profile.Browser == "firefox") return ReadFirefox(profile.File);
		return ReadChromeJson(File.ReadAllText(profile.File));
	}

	public static List<SiteVisit> ReadChromeJson(string json)
	{
		var items = new List<SiteVisit>();
		using var doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object)
			return items;
		foreach (var root in roots.EnumerateObject())
			Walk(root.Value, items);
		return items;
	}

	public static FavoriteMerge Merge(List<SiteVisit> into, IReadOnlyList<SiteVisit> incoming)
	{
		var seen = new HashSet<string>(into.Select(item => item.Url), StringComparer.OrdinalIgnoreCase);
		var added = 0;
		var already = 0;
		var full = false;
		foreach (var item in incoming)
		{
			if (!SiteDesk.IsPage(item.Url)) continue;
			if (!seen.Add(item.Url))
			{
				already++;
				continue;
			}
			if (into.Count >= SiteDesk.BookmarkMax)
			{
				full = true;
				break;
			}
			into.Add(item);
			added++;
		}
		return new FavoriteMerge(added, already, full);
	}

	public static List<(string Name, string Path)> ParseFirefoxIni(string text, string root)
	{
		var found = new List<(string Name, string Path)>();
		string name = "";
		string path = "";
		var relative = true;
		void Flush()
		{
			if (path.Length == 0) return;
			var full = relative ? System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar)) : path;
			var label = name.Length == 0 ? System.IO.Path.GetFileName(full) : name;
			found.Add((label, full));
			name = "";
			path = "";
			relative = true;
		}
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim();
			if (line.StartsWith('['))
			{
				Flush();
				continue;
			}
			var cut = line.IndexOf('=');
			if (cut <= 0) continue;
			var key = line[..cut].Trim();
			var value = line[(cut + 1)..].Trim();
			if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = value;
			else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) path = value;
			else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase)) relative = value != "0";
		}
		Flush();
		return found;
	}

	static void Walk(JsonElement node, List<SiteVisit> items)
	{
		if (items.Count >= SiteDesk.BookmarkMax) return;
		var type = node.TryGetProperty("type", out var kind) ? kind.GetString() ?? "" : "";
		if (type.Equals("url", StringComparison.OrdinalIgnoreCase))
		{
			var url = node.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
			if (!SiteDesk.IsPage(url) || items.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) return;
			var title = node.TryGetProperty("name", out var name) ? SiteDesk.OneLine(name.GetString()) : "";
			if (title.Length == 0) title = url;
			items.Add(new SiteVisit { Utc = DateTime.UtcNow.ToString("o"), Title = title, Url = url });
			return;
		}
		if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
		foreach (var child in children.EnumerateArray())
			Walk(child, items);
	}

	static List<SiteVisit> ReadFirefox(string database)
	{
		var temp = Path.Combine(Path.GetTempPath(), "guardex-places-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(temp);
		var copy = Path.Combine(temp, "places.sqlite");
		try
		{
			File.Copy(database, copy, true);
			foreach (var suffix in new[] { "-wal", "-shm" })
			{
				var side = database + suffix;
				if (File.Exists(side)) File.Copy(side, copy + suffix, true);
			}
			var items = new List<SiteVisit>();
			using var db = new SqliteConnection("Data Source=" + copy + ";Mode=ReadOnly");
			db.Open();
			using var cmd = db.CreateCommand();
			cmd.CommandText = """
				SELECT IFNULL(b.title, ''), p.url
				FROM moz_bookmarks b
				JOIN moz_places p ON p.id = b.fk
				WHERE b.type = 1 AND p.url IS NOT NULL
				ORDER BY b.dateAdded DESC
				""";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
			{
				if (items.Count >= SiteDesk.BookmarkMax) break;
				var url = reader.GetString(1);
				if (!SiteDesk.IsPage(url) || items.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
				var title = SiteDesk.OneLine(reader.GetString(0));
				if (title.Length == 0) title = url;
				items.Add(new SiteVisit { Utc = DateTime.UtcNow.ToString("o"), Title = title, Url = url });
			}
			return items;
		}
		finally
		{
			try { Directory.Delete(temp, true); } catch { }
		}
	}

	static List<FavoriteProfile> Chromium(string root, string browser)
	{
		var list = new List<FavoriteProfile>();
		if (!Directory.Exists(root)) return list;
		var names = ProfileNames(Path.Combine(root, "Local State"));
		foreach (var dir in Directory.GetDirectories(root))
		{
			var folder = Path.GetFileName(dir);
			if (folder.Equals("System Profile", StringComparison.OrdinalIgnoreCase)
				|| folder.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)
				|| folder.Equals("Crashpad", StringComparison.OrdinalIgnoreCase))
				continue;
			var file = Path.Combine(dir, "Bookmarks");
			if (!File.Exists(file)) continue;
			var name = names.TryGetValue(folder, out var nice) && nice.Length > 0 ? nice : folder;
			list.Add(new FavoriteProfile { Browser = browser, Name = name, File = file });
		}
		return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	static Dictionary<string, string> ProfileNames(string localState)
	{
		var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!File.Exists(localState)) return names;
			using var doc = JsonDocument.Parse(File.ReadAllText(localState));
			if (!doc.RootElement.TryGetProperty("profile", out var profile)) return names;
			if (!profile.TryGetProperty("info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object) return names;
			foreach (var entry in cache.EnumerateObject())
			{
				var name = entry.Value.TryGetProperty("name", out var value) ? value.GetString() ?? "" : "";
				if (name.Length > 0) names[entry.Name] = name;
			}
		}
		catch { }
		return names;
	}

	static List<FavoriteProfile> FirefoxProfiles()
	{
		var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
		var ini = Path.Combine(root, "profiles.ini");
		if (!File.Exists(ini)) return [];
		var list = new List<FavoriteProfile>();
		foreach (var (name, path) in ParseFirefoxIni(File.ReadAllText(ini), root))
		{
			var file = Path.Combine(path, "places.sqlite");
			if (!File.Exists(file)) continue;
			list.Add(new FavoriteProfile { Browser = "firefox", Name = name, File = file });
		}
		return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	static string Local() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public static string SelfCheck()
	{
		var failures = new List<string>();
		const string json = """
			{"roots":{"bookmark_bar":{"children":[{"type":"url","name":"Example","url":"https://example.com/a"},{"type":"folder","name":"Later","children":[{"type":"url","name":"Skip","url":"javascript:alert(1)"},{"type":"url","name":"Other","url":"https://example.org/b"}]},{"type":"url","name":"Dup","url":"https://example.com/a"}]},"other":{"children":[{"type":"url","name":"File","url":"file:///c/windows"}]}}}
			""";
		var read = ReadChromeJson(json);
		if (read.Count != 2 || read[0].Url != "https://example.com/a" || read[1].Title != "Other")
			failures.Add("chrome " + read.Count);
		var into = new List<SiteVisit> { new() { Title = "Had", Url = "https://example.org/b" } };
		var merged = Merge(into, read);
		if (merged.Added != 1 || merged.Already != 1 || into.Count != 2 || into[0].Url != "https://example.org/b")
			failures.Add("merge");
		var ini = "[InstallX]\nDefault=Profiles/a\n\n[Profile0]\nName=Day\nIsRelative=1\nPath=Profiles/day\n\n[Profile1]\nName=Night\nIsRelative=0\nPath=C:\\fx\\night\n";
		var profiles = ParseFirefoxIni(ini, Path.Combine("C:", "fx"));
		if (profiles.Count != 2 || profiles[0].Name != "Day" || !profiles[0].Path.EndsWith(Path.Combine("Profiles", "day"), StringComparison.OrdinalIgnoreCase))
			failures.Add("ini relative");
		if (profiles[1].Name != "Night" || !profiles[1].Path.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
			failures.Add("ini absolute");

		var dir = Path.Combine(Path.GetTempPath(), "guardex-fav-" + Guid.NewGuid().ToString("n"));
		try
		{
			Directory.CreateDirectory(dir);
			var db = Path.Combine(dir, "places.sqlite");
			using (var created = new SqliteConnection("Data Source=" + db))
			{
				created.Open();
				using var make = created.CreateCommand();
				make.CommandText = """
					CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT);
					CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, parent INTEGER, title TEXT, dateAdded INTEGER);
					INSERT INTO moz_places (id, url) VALUES (1, 'https://example.com/f'), (2, 'about:blank');
					INSERT INTO moz_bookmarks (id, type, fk, parent, title, dateAdded) VALUES (1, 2, NULL, 0, 'Menu', 1), (2, 1, 1, 1, 'From Firefox', 2), (3, 1, 2, 1, 'Blank', 3);
					""";
				make.ExecuteNonQuery();
			}
			var fox = ReadFirefox(db);
			if (fox.Count != 1 || fox[0].Title != "From Firefox" || fox[0].Url != "https://example.com/f")
				failures.Add("firefox " + fox.Count);
		}
		catch (Exception ex)
		{
			failures.Add("firefox db " + ex.GetType().Name);
		}
		finally
		{
			try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
		}
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

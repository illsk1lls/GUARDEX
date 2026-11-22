using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Guardex;

public sealed class SiteVisit : INotifyPropertyChanged
{
	ImageSource? _icon;
	public string Utc { get; set; } = "";
	public string Title { get; set; } = "";
	public string Url { get; set; } = "";

	[JsonIgnore]
	public ImageSource? Icon
	{
		get => _icon;
		set
		{
			if (_icon == value) return;
			_icon = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
		}
	}

	[JsonIgnore]
	public string Mark
	{
		get
		{
			var letter = Favicon.Letter(Url);
			if (letter.Length > 0) return letter;
			foreach (var ch in Url)
			{
				if (char.IsAsciiLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
			}
			return "";
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string When
	{
		get
		{
			if (!DateTime.TryParse(Utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)) return "";
			return time.ToLocalTime().ToString("MMM d, HH:mm");
		}
	}
}

static class SiteDesk
{
	public const int HistoryMax = 400;
	public const int BookmarkMax = 2000;
	public const int SessionMax = 16;

	public static bool IsPage(string? url)
	{
		if (string.IsNullOrWhiteSpace(url) || url.Length > 2000) return false;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
		return uri.Scheme is "http" or "https" && uri.Host.Length > 0;
	}

	public static bool IsKept(string? url) => Kept(url).Length > 0;

	public static string Kept(string? url)
	{
		if (string.IsNullOrWhiteSpace(url)) return "";
		var text = url.Trim();
		if (text.Length == 0 || text.Length > 2000) return "";
		foreach (var ch in text)
		{
			if (char.IsControl(ch)) return "";
		}
		return text;
	}

	public static bool Remember(List<SiteVisit> items, string? title, string? url, int max = HistoryMax)
	{
		if (!IsPage(url)) return false;
		var cleanTitle = OneLine(title);
		if (cleanTitle.Length == 0) cleanTitle = url!;
		if (items.Count > 0 && items[0].Url == url)
		{
			if (items[0].Title == cleanTitle) return false;
			items[0].Title = cleanTitle;
			items[0].Utc = DateTime.UtcNow.ToString("o");
			return true;
		}
		items.Insert(0, new SiteVisit { Utc = DateTime.UtcNow.ToString("o"), Title = cleanTitle, Url = url! });
		if (items.Count > max) items.RemoveRange(max, items.Count - max);
		return true;
	}

	public static bool IsMarked(IReadOnlyList<SiteVisit> items, string? url) =>
		IsPage(url) && items.Any(item => item.Url == url);

	public static bool ToggleMark(List<SiteVisit> items, string? title, string? url)
	{
		if (!IsPage(url)) return false;
		var found = items.FindIndex(item => item.Url == url);
		if (found >= 0)
		{
			items.RemoveAt(found);
			return false;
		}
		var cleanTitle = OneLine(title);
		if (cleanTitle.Length == 0) cleanTitle = url!;
		items.Insert(0, new SiteVisit { Utc = DateTime.UtcNow.ToString("o"), Title = cleanTitle, Url = url! });
		if (items.Count > BookmarkMax) items.RemoveRange(BookmarkMax, items.Count - BookmarkMax);
		return true;
	}

	public static string WriteMarks(IReadOnlyList<SiteVisit> items)
	{
		var lines = new List<string>();
		foreach (var item in items)
		{
			var url = Kept(item.Url);
			if (url.Length == 0) continue;
			lines.Add(JsonSerializer.Serialize(new SiteVisit
			{
				Utc = item.Utc,
				Title = OneLine(item.Title),
				Url = url
			}));
		}
		return string.Join('\n', lines);
	}

	public static List<SiteVisit> ReadMarks(string? text, int max)
	{
		var items = new List<SiteVisit>();
		if (string.IsNullOrWhiteSpace(text)) return items;
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			try
			{
				var item = JsonSerializer.Deserialize<SiteVisit>(line);
				var url = Kept(item?.Url);
				if (item == null || url.Length == 0) continue;
				item.Url = url;
				item.Title = OneLine(item.Title);
				if (item.Title.Length == 0) item.Title = item.Url;
				items.Add(item);
				if (items.Count >= max) break;
			}
			catch (JsonException)
			{
			}
		}
		return items;
	}

	public static string WriteVisits(IReadOnlyList<SiteVisit> items)
	{
		var lines = new List<string>();
		foreach (var item in items)
		{
			if (!IsPage(item.Url)) continue;
			lines.Add(JsonSerializer.Serialize(new SiteVisit
			{
				Utc = item.Utc,
				Title = OneLine(item.Title),
				Url = item.Url
			}));
		}
		return string.Join('\n', lines);
	}

	public static List<SiteVisit> ReadVisits(string? text, int max)
	{
		var items = new List<SiteVisit>();
		if (string.IsNullOrWhiteSpace(text)) return items;
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			try
			{
				var item = JsonSerializer.Deserialize<SiteVisit>(line);
				if (item == null || !IsPage(item.Url)) continue;
				item.Title = OneLine(item.Title);
				if (item.Title.Length == 0) item.Title = item.Url;
				items.Add(item);
				if (items.Count >= max) break;
			}
			catch (JsonException)
			{
			}
		}
		return items;
	}

	public static string WriteSession(IEnumerable<string> urls)
	{
		var kept = new List<string>();
		foreach (var url in urls)
		{
			if (!IsPage(url)) continue;
			kept.Add(url);
			if (kept.Count >= SessionMax) break;
		}
		return string.Join('\n', kept);
	}

	public static List<string> ReadSession(string? text)
	{
		var urls = new List<string>();
		if (string.IsNullOrWhiteSpace(text)) return urls;
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!IsPage(line)) continue;
			urls.Add(line);
			if (urls.Count >= SessionMax) break;
		}
		return urls;
	}

	public static string DownloadPath(string directory, string? suggested, Func<string, bool> exists)
	{
		var name = Path.GetFileName(suggested ?? "");
		foreach (var bad in Path.GetInvalidFileNameChars())
			name = name.Replace(bad, '_');
		name = name.Trim().Trim('.');
		if (name.Length == 0) name = "download";
		if (name.Length > 120) name = name[..120];
		var stem = Path.GetFileNameWithoutExtension(name);
		var ext = Path.GetExtension(name);
		if (stem.Length == 0) stem = "download";
		var path = Path.Combine(directory, stem + ext);
		var n = 1;
		while (exists(path) && n < 100)
		{
			path = Path.Combine(directory, stem + " (" + n + ")" + ext);
			n++;
		}
		return path;
	}

	public static bool IsInside(string directory, string path)
	{
		try
		{
			var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			var full = Path.GetFullPath(path);
			return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	public static (int Start, int Length) UrlToken(string? text, int index)
	{
		var value = text ?? "";
		if (value.Length == 0 || index < 0) return (0, 0);
		if (index >= value.Length) index = value.Length - 1;
		if (IsUrlBreak(value[index]))
		{
			if (index > 0 && !IsUrlBreak(value[index - 1])) index--;
			else return (index, 1);
		}
		var start = index;
		while (start > 0 && !IsUrlBreak(value[start - 1])) start--;
		var end = index + 1;
		while (end < value.Length && !IsUrlBreak(value[end])) end++;
		return (start, end - start);
	}

	public static List<SiteVisit> Suggest(IEnumerable<SiteVisit> bookmarks, IEnumerable<SiteVisit> history, string? query, int limit = 8)
	{
		var needle = (query ?? "").Trim();
		var found = new List<SiteVisit>();
		if (needle.Length == 0 || limit <= 0) return found;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void Take(IEnumerable<SiteVisit> items)
		{
			foreach (var item in items)
			{
				if (found.Count >= limit) return;
				if (!IsPage(item.Url) || !seen.Add(item.Url)) continue;
				if (item.Url.Contains(needle, StringComparison.OrdinalIgnoreCase)
					|| (item.Title ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase))
					found.Add(item);
			}
		}
		var marks = bookmarks.Where(item => Starts(item, needle)).Concat(bookmarks.Where(item => !Starts(item, needle)));
		var past = history.Where(item => Starts(item, needle)).Concat(history.Where(item => !Starts(item, needle)));
		Take(marks);
		Take(past);
		return found;
	}

	static bool Starts(SiteVisit item, string needle) =>
		item.Url.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
		|| (item.Title ?? "").StartsWith(needle, StringComparison.OrdinalIgnoreCase);

	static bool IsUrlBreak(char ch) => " \t\r\n/\\?#&=.:@%+~,;".Contains(ch);

	public static string OneLine(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return "";
		var clean = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
		return clean.Length <= 180 ? clean : clean[..180];
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var history = new List<SiteVisit>();
		if (Remember(history, "blank", "about:blank")) failures.Add("blank remembered");
		if (!Remember(history, "Example", "https://example.com/a")) failures.Add("page not remembered");
		if (Remember(history, "Example", "https://example.com/a")) failures.Add("same title duplicated");
		if (!Remember(history, "Example renamed", "https://example.com/a")) failures.Add("title not updated");
		if (history.Count != 1 || history[0].Title != "Example renamed") failures.Add("title history " + history.Count);
		if (!Remember(history, "Other", "https://example.com/b")) failures.Add("second page");
		if (history[0].Url != "https://example.com/b" || history.Count != 2) failures.Add("order");
		var capped = new List<SiteVisit>();
		for (var i = 0; i < 5; i++) Remember(capped, "n" + i, "https://example.com/" + i, 3);
		if (capped.Count != 3 || capped[0].Url != "https://example.com/4") failures.Add("cap");

		var marks = new List<SiteVisit>();
		if (!ToggleMark(marks, "Example", "https://example.com/a") || !IsMarked(marks, "https://example.com/a"))
			failures.Add("mark");
		if (ToggleMark(marks, "Example", "https://example.com/a") || IsMarked(marks, "https://example.com/a"))
			failures.Add("unmark");

		var written = WriteVisits(history);
		var read = ReadVisits(written + "\nnot json\n{\"url\":\"file:///c/windows\"}", 10);
		if (read.Count != 2 || read[0].Url != "https://example.com/b") failures.Add("roundtrip " + read.Count);

		var session = ReadSession("https://example.com/a\nabout:blank\nfile:///c/windows\nhttps://example.com/a\nhttps://example.org/");
		if (session.Count != 3 || session[1] != "https://example.com/a" || session[2] != "https://example.org/")
			failures.Add("session " + string.Join(",", session));
		if (ReadSession(WriteSession(session)).Count != 3) failures.Add("session roundtrip");

		var dir = Path.Combine("C:", "Users", "a", "Downloads");
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var first = DownloadPath(dir, Path.Combine(dir, "report.pdf"), seen.Contains);
		if (Path.GetFileName(first) != "report.pdf" || !IsInside(dir, first)) failures.Add("download name " + first);
		seen.Add(first);
		var second = DownloadPath(dir, @"..\..\Windows\report.pdf", seen.Contains);
		if (Path.GetFileName(second) != "report (1).pdf" || !IsInside(dir, second)) failures.Add("download clash " + second);
		var nested = DownloadPath(dir, @"..\..\Windows\notepad.exe", _ => false);
		if (Path.GetFileName(nested) != "notepad.exe") failures.Add("download traversal " + nested);
		if (IsInside(dir, Path.Combine("C:", "Users", "a", "Downloads2", "a.bin"))) failures.Add("sibling folder");
		if (!IsInside(dir, Path.Combine(dir, "a.bin"))) failures.Add("inside downloads");

		const string sample = "https://en.wikipedia.org/wiki/Cat";
		var host = UrlToken(sample, sample.IndexOf("wikipedia", StringComparison.Ordinal));
		if (sample.Substring(host.Start, host.Length) != "wikipedia") failures.Add("token host");
		var path = UrlToken(sample, sample.IndexOf("Cat", StringComparison.Ordinal));
		if (sample.Substring(path.Start, path.Length) != "Cat") failures.Add("token path");
		var scheme = UrlToken(sample, 2);
		if (sample.Substring(scheme.Start, scheme.Length) != "https") failures.Add("token scheme");
		if (UrlToken("", 0).Length != 0) failures.Add("token empty");

		var bookmarks = new List<SiteVisit>
		{
			new() { Title = "Example", Url = "https://example.com/a" },
			new() { Title = "Other", Url = "https://other.test/" }
		};
		var past = new List<SiteVisit> { new() { Title = "Old example", Url = "https://example.com/old" } };
		var hits = Suggest(bookmarks, past, "exam", 8);
		if (hits.Count != 2 || hits[0].Url != "https://example.com/a" || hits[1].Url != "https://example.com/old")
			failures.Add("suggest " + string.Join(",", hits.Select(item => item.Url)));
		if (Suggest(bookmarks, past, "   ", 8).Count != 0) failures.Add("suggest blank");
		if (Suggest(bookmarks, past, "missing", 8).Count != 0) failures.Add("suggest miss");

		if (!IsKept("javascript:alert(1)") || !IsKept("file:///c/windows") || !IsKept("about:blank") || !IsKept("not a url"))
			failures.Add("kept");
		if (IsKept("bad\nurl") || IsKept("  ") || IsKept(new string('a', 2001))) failures.Add("kept drop");
		var kept = new List<SiteVisit>
		{
			new() { Title = "Js", Url = " javascript:alert(1) " },
			new() { Title = "File", Url = "file:///c/windows" },
			new() { Title = "Words", Url = "not a url" },
			new() { Title = "Page", Url = "https://example.com/a" },
			new() { Title = "Ctl", Url = "has\tcontrol" }
		};
		var restored = ReadMarks(WriteMarks(kept), 10);
		if (restored.Count != 4 || restored[0].Url != "javascript:alert(1)" || restored[1].Url != "file:///c/windows"
			|| restored[2].Url != "not a url" || restored[3].Url != "https://example.com/a")
			failures.Add("marks " + restored.Count);
		if (new SiteVisit { Url = "not a url" }.Mark != "N") failures.Add("mark text");
		if (new SiteVisit { Url = "https://en.wikipedia.org/" }.Mark != "E") failures.Add("mark host");

		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

using System.IO;
using System.Text.RegularExpressions;

namespace Guardex;

// A startup name that only changes its GUID is one name. The stem is what gets blocked or allowed.
static class HostNames
{
	static readonly Regex GuidRx = new(
		@"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	static readonly Regex SepRx = new(@"[\s_\-\.]+", RegexOptions.Compiled);
	static readonly object Gate = new();
	static Book? _live;

	public static string? Core(string? name)
	{
		if (string.IsNullOrWhiteSpace(name) || !GuidRx.IsMatch(name)) return null;
		var stripped = GuidRx.Replace(name, "");
		var chars = stripped.Where(char.IsLetterOrDigit).ToArray();
		if (chars.Length < 4) return null;
		return new string(chars).ToLowerInvariant();
	}

	public static string? Wildcard(string? name)
	{
		if (Core(name) == null || name == null) return null;
		var stem = SepRx.Replace(GuidRx.Replace(name, " "), "_").Trim('_');
		return stem.Length == 0 ? null : stem + "_*";
	}

	public static string? Verdict(string? name)
	{
		lock (Gate) return Live().Verdict(name);
	}

	public static void Remember(string? name, bool block)
	{
		var core = Core(name);
		if (core == null) return;
		lock (Gate)
		{
			var book = Live();
			book.Set(name, block);
			try { book.Save(FilePath()); }
			catch { }
		}
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var a = "msedge_cleanup_{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
		var b = "msedge_cleanup_{C50565E9-CCCF-44B4-BA15-5AC5C6569197}";
		var helper = "msedge_cleanup_helper_{C50565E9-CCCF-44B4-BA15-5AC5C6569197}";
		var other = "widget_{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
		if (Core(a) == null || Core(a) != Core(b)) failures.Add("guid pair");
		if (Core("msedge_cleanup_helper") != null) failures.Add("plain name");
		if (Core(a) == Core(helper) || Core(a) == Core(other)) failures.Add("different stem");
		if (Wildcard(a) != "msedge_cleanup_*") failures.Add("wildcard " + Wildcard(a));
		var blocked = new Book();
		blocked.Set(a, true);
		if (blocked.Verdict(b) != "block") failures.Add("block");
		blocked.Set(b, false);
		if (blocked.Verdict(a) != "block") failures.Add("block sticks");
		if (blocked.Verdict("msedge_cleanup_helper") != null) failures.Add("helper matched");
		var allowed = new Book();
		allowed.Set(a, false);
		if (allowed.Verdict(b) != "allow") failures.Add("allow");
		if (allowed.Verdict(helper) != null) failures.Add("helper allowed");
		allowed.Set(b, true);
		if (allowed.Verdict(a) != "block") failures.Add("block replaces");
		var path = Path.Combine(Path.GetTempPath(), "guardex-hostnames-" + Guid.NewGuid().ToString("N") + ".txt");
		try
		{
			allowed.Save(path);
			var loaded = Book.Load(path);
			if (loaded.Verdict(b) != "block") failures.Add("roundtrip");
			if (loaded.Verdict(other) != null) failures.Add("roundtrip stem");
		}
		catch (Exception ex)
		{
			failures.Add(ex.Message);
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static Book Live()
	{
		if (_live != null) return _live;
		try { _live = Book.Load(FilePath()); }
		catch { _live = new Book(); }
		return _live;
	}

	static string FilePath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "host-block.txt");

	sealed class Book
	{
		readonly Dictionary<string, string> _block = new(StringComparer.Ordinal);
		readonly Dictionary<string, string> _allow = new(StringComparer.Ordinal);

		public void Set(string? name, bool block)
		{
			var core = Core(name);
			var wild = Wildcard(name);
			if (core == null || wild == null) return;
			if (block)
			{
				_allow.Remove(core);
				_block[core] = wild;
				return;
			}
			if (_block.ContainsKey(core)) return;
			_allow[core] = wild;
		}

		public string? Verdict(string? name)
		{
			var core = Core(name);
			if (core == null) return null;
			if (_block.ContainsKey(core)) return "block";
			if (_allow.ContainsKey(core)) return "allow";
			return null;
		}

		public void Save(string path)
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			var lines = new List<string>();
			foreach (var pair in _block) lines.Add("block\t" + pair.Key + "\t" + pair.Value);
			foreach (var pair in _allow) lines.Add("allow\t" + pair.Key + "\t" + pair.Value);
			File.WriteAllLines(path, lines);
		}

		public static Book Load(string path)
		{
			var book = new Book();
			if (!File.Exists(path)) return book;
			foreach (var raw in File.ReadAllLines(path))
			{
				var parts = raw.Split('\t');
				if (parts.Length < 2) continue;
				var core = parts[1].Trim().ToLowerInvariant();
				if (core.Length < 4) continue;
				var wild = parts.Length > 2 ? parts[2].Trim() : core + "_*";
				if (parts[0] == "block")
				{
					book._allow.Remove(core);
					book._block[core] = wild;
				}
				else if (parts[0] == "allow" && !book._block.ContainsKey(core))
					book._allow[core] = wild;
			}
			return book;
		}
	}
}

static class AskAck
{
	public static string Line(bool yes, string subject)
	{
		var lead = string.IsNullOrWhiteSpace(subject) ? "" : subject.Split('\n', '\r')[0].Trim();
		if (lead.Length > 160) lead = lead[..160];
		var verb = yes ? "You said this was you" : "You said this was not you";
		return lead.Length == 0 ? "Saved. " + verb + "." : "Saved. " + verb + ": " + lead + ".";
	}
}

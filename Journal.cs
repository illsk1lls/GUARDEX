using System.IO;

namespace Guardex;

// The Blocks page only shows what is still active. This file keeps the events after they end.
static class Journal
{
	public const int MaxLines = 400;
	static readonly object Gate = new();
	static List<string>? _lines;

	public static string Dir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");

	public static string FilePath() => Path.Combine(Dir(), "log.txt");

	public static string Format(DateTime utc, string text)
	{
		var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (flat.Length > 500) flat = flat[..500];
		return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  " + flat;
	}

	public static List<string> Keep(List<string> lines, int max)
	{
		if (lines.Count <= max) return lines;
		return lines.GetRange(lines.Count - max, max);
	}

	public static void Append(DateTime utc, string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		var line = Format(utc, text);
		lock (Gate)
		{
			var lines = Load();
			lines.Add(line);
			if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
			try
			{
				Directory.CreateDirectory(Dir());
				File.WriteAllLines(FilePath(), lines);
			}
			catch { }
		}
	}

	public static IReadOnlyList<string> Lines()
	{
		lock (Gate) return Load().ToList();
	}

	static List<string> Load()
	{
		if (_lines != null) return _lines;
		_lines = [];
		try
		{
			if (File.Exists(FilePath()))
				_lines.AddRange(File.ReadAllLines(FilePath()).Where(line => line.Length > 0).TakeLast(MaxLines));
		}
		catch { }
		return _lines;
	}

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var line = Format(new DateTime(2026, 1, 2, 15, 4, 5, DateTimeKind.Utc), "block\nlifted");
		if (!line.Contains("block lifted", StringComparison.Ordinal)) failures.Add("flatten");
		if (!line.Contains("2026-", StringComparison.Ordinal)) failures.Add("date");
		if (line.Contains('\n')) failures.Add("newline");
		var kept = Keep(["0", "1", "2", "3", "4"], 3);
		if (string.Join(",", kept) != "2,3,4") failures.Add("trim");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

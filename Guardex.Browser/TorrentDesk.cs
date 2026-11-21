using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Guardex.Tunnel;

namespace Guardex.Browser;

public sealed class TorrentRow : INotifyPropertyChanged
{
	string _line = "";
	public Engine Engine { get; }
	public bool UserPaused { get; set; }
	public string Line { get => _line; set { if (_line == value) return; _line = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Line))); } }
	public event PropertyChangedEventHandler? PropertyChanged;
	public TorrentRow(Engine engine) => Engine = engine;
}

static class TorrentDesk
{
	public const string Phrase = "allow torrents on this network";
	public static readonly ObservableCollection<TorrentRow> Items = new();
	static readonly List<Entry> _entries = new();
	static bool _policyOn;

	sealed class Entry
	{
		public string Kind = "";
		public string Value = "";
		public TorrentRow? Row;
	}

	public static bool Wanted { get; private set; }
	public static bool OsConfirmed { get; private set; }
	public static bool KillswitchOn => VpnHub.Require && VpnHub.HasConfig;
	public static bool Allowed => Wanted && (KillswitchOn || OsConfirmed);
	public static string SaveRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

	static string FlagPath => Path.Combine(TunnelStore.Root, "torrent-flags.txt");
	static string ListPath => Path.Combine(TunnelStore.Root, "torrent-list.txt");

	public static bool PhraseOk(string? text) =>
		string.Equals((text ?? "").Trim(), Phrase, StringComparison.OrdinalIgnoreCase);

	public static string SelfCheck()
	{
		if (!PhraseOk("  Allow Torrents On This Network  ")) return "phrase";
		if (PhraseOk("allow")) return "short";
		if (PhraseOk("")) return "empty";
		return "PASS";
	}

	public static void Load()
	{
		Directory.CreateDirectory(SaveRoot);
		try
		{
			if (File.Exists(FlagPath))
			{
				foreach (var line in File.ReadAllLines(FlagPath))
				{
					if (line == "want") Wanted = true;
					if (line == "os") OsConfirmed = true;
				}
			}
		}
		catch { }
		try
		{
			if (!File.Exists(ListPath)) return;
			foreach (var line in File.ReadAllLines(ListPath))
			{
				var cut = line.IndexOf('\t');
				if (cut < 1) continue;
				var kind = line[..cut];
				var value = line[(cut + 1)..];
				if (kind != "M" && kind != "F") continue;
				TryAdd(kind, value, start: false, out _);
			}
		}
		catch { }
		_policyOn = false;
		Sync();
	}

	public static string Enable(string? typed)
	{
		if (!KillswitchOn)
		{
			if (!OsConfirmed && !PhraseOk(typed))
				return "Type the confirmation to use this PC's network.";
			OsConfirmed = true;
		}
		Wanted = true;
		SaveFlags();
		Sync();
		return "";
	}

	public static void Disable()
	{
		Wanted = false;
		SaveFlags();
		Sync();
	}

	public static void StopEngines()
	{
		_policyOn = false;
		List<Entry> snap;
		lock (_entries) snap = _entries.ToList();
		foreach (var entry in snap)
		{
			try { entry.Row?.Engine.Stop(); } catch { }
		}
	}

	public static string AddMagnet(string magnet)
	{
		if (!Allowed) return "Allow torrents first.";
		if (!TryAdd("M", magnet.Trim(), start: true, out var error)) return error;
		SaveList();
		return "";
	}

	public static string AddFile(string path)
	{
		if (!Allowed) return "Allow torrents first.";
		if (!File.Exists(path)) return "That torrent file is not there.";
		if (!TryAdd("F", path, start: true, out var error)) return error;
		SaveList();
		return "";
	}

	public static void Pause(TorrentRow row)
	{
		row.UserPaused = true;
		try { row.Engine.Pause(); } catch { }
	}

	public static void Resume(TorrentRow row)
	{
		if (!Allowed) return;
		row.UserPaused = false;
		try { row.Engine.Resume(); } catch { }
	}

	public static void Remove(TorrentRow row)
	{
		try { row.Engine.Stop(); } catch { }
		lock (_entries)
		{
			_entries.RemoveAll(e => e.Row == row);
		}
		Items.Remove(row);
		SaveList();
	}

	public static void Sync()
	{
		var allow = Allowed;
		if (allow == _policyOn) return;
		_policyOn = allow;
		List<Entry> snap;
		lock (_entries) snap = _entries.ToList();
		foreach (var entry in snap)
		{
			var engine = entry.Row?.Engine;
			if (engine == null) continue;
			try
			{
				if (!allow) engine.Stop();
				else if (entry.Row!.UserPaused) { }
				else engine.Start();
			}
			catch { }
		}
	}

	public static void Refresh()
	{
		List<Entry> snap;
		lock (_entries) snap = _entries.ToList();
		foreach (var entry in snap)
		{
			var row = entry.Row;
			if (row == null) continue;
			EngineStatus status;
			try { status = row.Engine.GetStatus(); }
			catch { continue; }
			var name = string.IsNullOrEmpty(status.Name) ? "Torrent" : status.Name;
			var err = string.IsNullOrEmpty(status.ErrorMessage) ? "" : "  " + status.ErrorMessage;
			row.Line = name + "  " + status.ProgressPercent.ToString("0", CultureInfo.InvariantCulture) + "%  " + status.State
				+ "  down " + Engine.Fmt((long)status.DownBytesPerSec) + "/s"
				+ "  up " + Engine.Fmt((long)status.UpBytesPerSec) + "/s"
				+ "  peers " + status.PeersConnected.ToString(CultureInfo.InvariantCulture)
				+ err;
		}
	}

	static bool TryAdd(string kind, string value, bool start, out string error)
	{
		error = "";
		try
		{
			var settings = new EngineSettings
			{
				SavePath = SaveRoot,
				ListenPort = 6881,
				MaxPeers = 80,
				EnableDht = true,
				EnableUtp = true,
				EnableEncrypt = true,
				SeedAfterComplete = true
			};
			if (kind == "M") settings.MagnetUri = value;
			else settings.TorrentPath = value;
			var engine = new Engine(settings);
			var row = new TorrentRow(engine);
			var entry = new Entry { Kind = kind, Value = value, Row = row };
			lock (_entries) _entries.Add(entry);
			Items.Add(row);
			if (start && Allowed && !row.UserPaused) engine.Start();
			Refresh();
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	static void SaveFlags()
	{
		try
		{
			Directory.CreateDirectory(TunnelStore.Root);
			var lines = new List<string>();
			if (Wanted) lines.Add("want");
			if (OsConfirmed) lines.Add("os");
			File.WriteAllLines(FlagPath, lines);
		}
		catch { }
	}

	static void SaveList()
	{
		try
		{
			Directory.CreateDirectory(TunnelStore.Root);
			List<string> lines;
			lock (_entries) lines = _entries.Select(e => e.Kind + "\t" + e.Value).ToList();
			File.WriteAllLines(ListPath, lines);
		}
		catch { }
	}
}

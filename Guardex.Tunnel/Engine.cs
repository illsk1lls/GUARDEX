// Tunnel and torrent engine for the GUARDEX browser.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace Guardex.Tunnel {

	public static class IpcHub {
		const string PipeName = "PowerTorrent";
		static readonly object gate = new object();
		static readonly List<string> q = new List<string>();
		static volatile bool run;
		static Thread thr;
		static NamedPipeServerStream pipe;
		public static void Start() {
			if (thr != null) return;
			run = true;
			thr = new Thread(Loop);
			thr.IsBackground = true;
			thr.Name = "pt-ipc";
			thr.Start();
		}
		public static void Stop() {
			run = false;
			try {
				NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
				c.Connect(200);
				c.Dispose();
			} catch { }
			try { if (pipe != null) pipe.Dispose(); } catch { }
			try { if (thr != null) thr.Join(1000); } catch { }
			thr = null;
			pipe = null;
		}
		public static bool Send(string source) {
			if (string.IsNullOrEmpty(source)) return false;
			try {
				using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out)) {
					c.Connect(2000);
					using (StreamWriter sw = new StreamWriter(c, Encoding.UTF8)) {
						sw.WriteLine(source);
						sw.Flush();
					}
				}
				return true;
			} catch { return false; }
		}
		public static string[] Drain() {
			lock (gate) {
				if (q.Count == 0) return new string[0];
				string[] a = q.ToArray();
				q.Clear();
				return a;
			}
		}
		static void Loop() {
			while (run) {
				NamedPipeServerStream p = null;
				try {
					p = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
					pipe = p;
					p.WaitForConnection();
					if (!run) break;
					StreamReader sr = new StreamReader(p, Encoding.UTF8);
					string all = sr.ReadToEnd();
					try { sr.Dispose(); } catch { }
					if (string.IsNullOrEmpty(all)) continue;
					string[] lines = all.Replace("\r", "").Split('\n');
					lock (gate) {
						for (int i = 0; i < lines.Length; i++) {
							string t = lines[i].Trim();
							if (t.Length > 0) q.Add(t);
						}
					}
				} catch { if (!run) break; }
				finally {
					pipe = null;
					try { if (p != null) p.Dispose(); } catch { }
				}
			}
		}
	}

	public sealed class EngineSettings {
		public string TorrentPath;
		public string MagnetUri;
		public string SavePath;
		public int ListenPort;
		public int MaxPeers;
		public int LogLevel;
		public bool Sequential;
		public bool EnableDht;
		public bool SeedAfterComplete;
		public string ForcedPeer;
		public bool EnableEncrypt;
		public bool EnableUtp;
	}

	public sealed class TorrentRow : INotifyPropertyChanged {
		public event PropertyChangedEventHandler PropertyChanged;
		void Notify(string n) {
			PropertyChangedEventHandler h = PropertyChanged;
			if (h != null) h(this, new PropertyChangedEventArgs(n));
		}
		public string Id;
		string name = "";
		string sizeText = "";
		string progressText = "0%";
		string status = "Idle";
		string peersText = "0 / 0";
		string seedsText = "0";
		string downText = "0 B/s";
		string upText = "0 B/s";
		string eta = "--";
		string hash = "";
		string ratioText = "--";
		string availText = "--";
		string remainingText = "--";
		string downTotalText = "0 B";
		string upTotalText = "0 B";
		string piecesText = "0 / 0";
		double progress;
		public string Name { get { return name; } set { if (name != value) { name = value; Notify("Name"); } } }
		public string SizeText { get { return sizeText; } set { if (sizeText != value) { sizeText = value; Notify("SizeText"); } } }
		public double Progress { get { return progress; } set { if (progress != value) { progress = value; Notify("Progress"); } } }
		public string ProgressText { get { return progressText; } set { if (progressText != value) { progressText = value; Notify("ProgressText"); } } }
		public string Status { get { return status; } set { if (status != value) { status = value; Notify("Status"); } } }
		public string PeersText { get { return peersText; } set { if (peersText != value) { peersText = value; Notify("PeersText"); } } }
		public string SeedsText { get { return seedsText; } set { if (seedsText != value) { seedsText = value; Notify("SeedsText"); } } }
		public string DownText { get { return downText; } set { if (downText != value) { downText = value; Notify("DownText"); } } }
		public string UpText { get { return upText; } set { if (upText != value) { upText = value; Notify("UpText"); } } }
		public string Eta { get { return eta; } set { if (eta != value) { eta = value; Notify("Eta"); } } }
		public string Hash { get { return hash; } set { if (hash != value) { hash = value; Notify("Hash"); } } }
		public string RatioText { get { return ratioText; } set { if (ratioText != value) { ratioText = value; Notify("RatioText"); } } }
		public string AvailText { get { return availText; } set { if (availText != value) { availText = value; Notify("AvailText"); } } }
		public string RemainingText { get { return remainingText; } set { if (remainingText != value) { remainingText = value; Notify("RemainingText"); } } }
		public string DownTotalText { get { return downTotalText; } set { if (downTotalText != value) { downTotalText = value; Notify("DownTotalText"); } } }
		public string UpTotalText { get { return upTotalText; } set { if (upTotalText != value) { upTotalText = value; Notify("UpTotalText"); } } }
		public string PiecesText { get { return piecesText; } set { if (piecesText != value) { piecesText = value; Notify("PiecesText"); } } }
		public void Apply(EngineStatus s) {
			if (s == null) return;
			Name = s.Name;
			Hash = s.InfoHashHex;
			SizeText = Engine.Fmt(s.TotalSize);
			Progress = s.ProgressPercent;
			ProgressText = s.ProgressPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
			Status = s.State;
			PeersText = s.PeersConnected.ToString(CultureInfo.InvariantCulture) + " / " + s.PeersKnown.ToString(CultureInfo.InvariantCulture);
			SeedsText = s.SeedsConnected.ToString(CultureInfo.InvariantCulture) + " / " + s.SeedsKnown.ToString(CultureInfo.InvariantCulture);
			DownText = Engine.Fmt((long)s.DownBytesPerSec) + "/s";
			UpText = Engine.Fmt((long)s.UpBytesPerSec) + "/s";
			Eta = s.Eta;
			DownTotalText = Engine.Fmt(s.Downloaded);
			UpTotalText = Engine.Fmt(s.Uploaded);
			PiecesText = s.PiecesDone.ToString(CultureInfo.InvariantCulture) + " / " + s.PiecesTotal.ToString(CultureInfo.InvariantCulture);
			long rem = s.TotalSize - s.Downloaded;
			if (rem < 0) rem = 0;
			RemainingText = Engine.Fmt(rem);
			if (s.Downloaded > 0)
				RatioText = ((double)s.Uploaded / (double)s.Downloaded).ToString("0.000", CultureInfo.InvariantCulture);
			else if (s.Uploaded > 0)
				RatioText = "\u221E";
			else
				RatioText = "--";
			if (s.Availability > 0)
				AvailText = s.Availability.ToString("0.0", CultureInfo.InvariantCulture);
			else
				AvailText = "--";
		}
	}

	public static class TorrentRowFilter {
		public static string Name = "All";
		public static bool Pass(object obj) {
			TorrentRow row = obj as TorrentRow;
			if (row == null) return true;
			string st = row.Status;
			if (st == null) st = "";
			string n = Name;
			if (n == null || n.Length == 0 || n == "All") return true;
			if (n == "Downloading") return st == "Downloading" || st == "Metadata" || st == "Announcing";
			if (n == "Seeding") return st == "Seeding";
			if (n == "Completed") return st == "Complete" || st == "Seeding";
			if (n == "Paused") return st == "Paused";
			if (n == "Checking") return st == "Hashing";
			return true;
		}
		public static Predicate<object> GetPredicate() {
			return Pass;
		}
	}

	public sealed class MagnetLink {
		public byte[] InfoHash;
		public string InfoHashHex = "";
		public byte[] V2Hash;
		public string V2InfoHashHex = "";
		public bool V2Only;
		public string DisplayName = "";
		public List<string> Trackers = new List<string>();
		public List<string> Webseeds = new List<string>();

		public static MagnetLink Parse(string uri) {
			if (string.IsNullOrEmpty(uri)) throw new ArgumentException("empty magnet");
			string u = uri.Trim();
			if (u.Length < 8 || u.Substring(0, 8).ToLowerInvariant() != "magnet:?")
				throw new Exception("not a magnet URI");
			MagnetLink mag = new MagnetLink();
			string query = u.Substring(8);
			string[] parts = query.Split('&');
			for (int i = 0; i < parts.Length; i++) {
				string kv = parts[i];
				int eq = kv.IndexOf('=');
				if (eq <= 0) continue;
				string key = Unesc(kv.Substring(0, eq)).ToLowerInvariant();
				string val = Unesc(kv.Substring(eq + 1));
				if (key == "xt" || key.StartsWith("xt.")) {
					string low = val.ToLowerInvariant();
					if (low.StartsWith("urn:btih:")) {
						string h = val.Substring(9).Trim();
						byte[] hash = ParseInfoHash(h);
						if (hash != null) {
							mag.InfoHash = hash;
							mag.InfoHashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
						}
					} else if (low.StartsWith("urn:btmh:")) {
						string h = val.Substring(9).Trim().ToLowerInvariant();
						if (h.StartsWith("1220")) h = h.Substring(4);
						if (h.Length == 64) {
							try {
								byte[] v2 = new byte[32];
								for (int n = 0; n < 32; n++) v2[n] = Convert.ToByte(h.Substring(n * 2, 2), 16);
								mag.V2Hash = v2;
								mag.V2InfoHashHex = h;
								if (mag.InfoHash == null) {
									mag.InfoHash = new byte[20];
									Buffer.BlockCopy(v2, 0, mag.InfoHash, 0, 20);
									mag.InfoHashHex = BitConverter.ToString(mag.InfoHash).Replace("-", "").ToLowerInvariant();
									mag.V2Only = true;
								}
							} catch { }
						}
					}
				} else if (key == "dn" && mag.DisplayName.Length == 0) {
					mag.DisplayName = val;
				} else if (key == "tr") {
					if (!string.IsNullOrEmpty(val)) mag.Trackers.Add(val.Trim());
				} else if (key == "ws") {
					if (!string.IsNullOrEmpty(val)) mag.Webseeds.Add(val.Trim());
				}
			}
			if (mag.InfoHash == null || mag.InfoHash.Length != 20)
				throw new Exception("magnet missing xt=urn:btih or xt=urn:btmh info hash");
			return mag;
		}

		static string Unesc(string s) {
			try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
			catch { return s; }
		}

		public static byte[] ParseInfoHash(string h) {
			if (string.IsNullOrEmpty(h)) return null;
			h = h.Trim();
			if (h.Length == 40) {
				try {
					byte[] b = new byte[20];
					for (int i = 0; i < 20; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
					return b;
				} catch { return null; }
			}
			if (h.Length == 32) return FromBase32(h);
			return null;
		}

		public static byte[] FromBase32(string s) {
			const string abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
			s = s.ToUpperInvariant().Replace("=", "");
			int buffer = 0, bits = 0, idx = 0;
			byte[] outb = new byte[20];
			for (int i = 0; i < s.Length; i++) {
				int v = abc.IndexOf(s[i]);
				if (v < 0) return null;
				buffer = (buffer << 5) | v;
				bits += 5;
				if (bits >= 8) {
					bits -= 8;
					if (idx >= 20) return null;
					outb[idx++] = (byte)((buffer >> bits) & 0xFF);
				}
			}
			if (idx != 20) return null;
			return outb;
		}
	}

	public sealed class EngineStatus {
		public string Name = "";
		public string InfoHashHex = "";
		public string State = "Idle";
		public string ErrorMessage = "";
		public long Downloaded;
		public long Uploaded;
		public long TotalSize;
		public double ProgressPercent;
		public double DownBytesPerSec;
		public double UpBytesPerSec;
		public int PeersConnected;
		public int PeersKnown;
		public int PiecesDone;
		public int PiecesTotal;
		public int ListenPort;
		public int SeedsConnected;
		public int SeedsKnown;
		public double Availability;
		public string Eta = "--";
		public string[] LogLines = new string[0];
	}

	public sealed class TorrentInfo {
		public string Name = "";
		public string Comment = "";
		public string CreatedBy = "";
		public string InfoHashHex = "";
		public long TotalSize;
		public int PieceLength;
		public int PieceCount;
		public int FileCount;
		public string[] Trackers = new string[0];
		public string[] Webseeds = new string[0];
		public string[] Files = new string[0];
		public FileRow[] FileRows = new FileRow[0];
		public bool IsMulti;
		public string SavePath = "";
	}

	public sealed class SearchRow {
		public string Name { get; set; }
		public string SizeText { get; set; }
		public string SeedsText { get; set; }
		public string LeechText { get; set; }
		public string Source { get; set; }
		public string Magnet { get; set; }
		public string Hash { get; set; }
		public int Seeds { get; set; }
		public int Leech { get; set; }
		public long SizeBytes { get; set; }
	}

	public static class TorrentSearch {
		public const string TpbUrl = "https://apibay.org/q.php?q=";
		public const string CsvUrl = "https://torrents-csv.com/service/search?size=100&q=";
		public static readonly string[] Trackers = new string[] {
			"udp://tracker.internetwarriors.net:1337/announce",
			"udp://tracker.opentrackr.org:1337/announce",
			"udp://p4p.arenabg.ch:1337/announce",
			"udp://tracker.openbittorrent.com:6969/announce",
			"udp://www.torrent.eu.org:451/announce",
			"udp://tracker.torrent.eu.org:451/announce",
			"udp://retracker.lanta-net.ru:2710/announce",
			"udp://open.stealth.si:80/announce",
			"udp://exodus.desync.com:6969/announce",
			"udp://tracker.tiny-vps.com:6969/announce"
		};
		public static string MakeMagnet(string hex, string name) {
			if (string.IsNullOrEmpty(hex)) return "";
			StringBuilder sb = new StringBuilder();
			sb.Append("magnet:?xt=urn:btih:").Append(hex.Trim());
			if (!string.IsNullOrEmpty(name)) {
				sb.Append("&dn=");
				sb.Append(Uri.EscapeDataString(name));
			}
			for (int i = 0; i < Trackers.Length; i++) {
				sb.Append("&tr=");
				sb.Append(Uri.EscapeDataString(Trackers[i]));
			}
			return sb.ToString();
		}
		public static bool IsZeroHash(string hex) {
			if (string.IsNullOrEmpty(hex) || hex.Length < 40) return true;
			for (int i = 0; i < hex.Length; i++) {
				char c = hex[i];
				if (c != '0') return false;
			}
			return true;
		}

		static int SkipWs(string s, int i) {
			while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
			return i;
		}
		static int FindMatching(string s, int open) {
			char oc = s[open];
			char cc = oc == '{' ? '}' : ']';
			int depth = 0;
			bool inStr = false;
			bool esc = false;
			for (int i = open; i < s.Length; i++) {
				char c = s[i];
				if (inStr) {
					if (esc) esc = false;
					else if (c == '\\') esc = true;
					else if (c == '"') inStr = false;
					continue;
				}
				if (c == '"') { inStr = true; continue; }
				if (c == oc) depth++;
				else if (c == cc) {
					depth--;
					if (depth == 0) return i;
				}
			}
			return -1;
		}
		static int SkipString(string s, int i, int b) {
			if (i >= b || s[i] != '"') return i;
			i++;
			bool esc = false;
			while (i < b) {
				char c = s[i];
				if (esc) { esc = false; i++; continue; }
				if (c == '\\') { esc = true; i++; continue; }
				if (c == '"') return i + 1;
				i++;
			}
			return i;
		}
		static int SkipValue(string s, int i, int b) {
			i = SkipWs(s, i);
			if (i >= b) return i;
			char c = s[i];
			if (c == '"') return SkipString(s, i, b);
			if (c == '{' || c == '[') {
				int m = FindMatching(s, i);
				return m < 0 ? b : m + 1;
			}
			while (i < b) {
				char ch = s[i];
				if (ch == ',' || ch == '}' || ch == ']' || char.IsWhiteSpace(ch)) break;
				i++;
			}
			return i;
		}
		static string UnescapeJson(string s, int openQuote, int afterClose) {
			if (afterClose - openQuote < 2) return "";
			StringBuilder sb = new StringBuilder();
			int e = openQuote + 1;
			int last = afterClose - 1;
			while (e < last) {
				char ch = s[e];
				if (ch == '\\' && e + 1 < last) {
					char n = s[e + 1];
					if (n == '"' || n == '\\' || n == '/') sb.Append(n);
					else if (n == 'n') sb.Append('\n');
					else if (n == 'r') sb.Append('\r');
					else if (n == 't') sb.Append('\t');
					else if (n == 'u' && e + 5 < last) {
						int u;
						if (int.TryParse(s.Substring(e + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u))
							sb.Append((char)u);
						else sb.Append(n);
						e += 6;
						continue;
					} else sb.Append(n);
					e += 2;
					continue;
				}
				sb.Append(ch);
				e++;
			}
			return sb.ToString();
		}
		static string FieldStr(string s, int a, int b, string key) {
			if (s == null || a < 0 || b <= a || a >= s.Length || s[a] != '{') return "";
			int i = a + 1;
			while (i < b) {
				i = SkipWs(s, i);
				if (i >= b || s[i] == '}') break;
				if (s[i] != '"') { i++; continue; }
				int keyBeg = i;
				int keyEnd = SkipString(s, i, b);
				string k = UnescapeJson(s, keyBeg, keyEnd);
				i = SkipWs(s, keyEnd);
				if (i < b && s[i] == ':') i++;
				i = SkipWs(s, i);
				if (k == key) {
					if (i < b && s[i] == '"') {
						int vend = SkipString(s, i, b);
						return UnescapeJson(s, i, vend);
					}
					int v2 = i;
					if (v2 < b && s[v2] == '-') v2++;
					while (v2 < b && char.IsDigit(s[v2])) v2++;
					return s.Substring(i, v2 - i);
				}
				i = SkipValue(s, i, b);
				i = SkipWs(s, i);
				if (i < b && s[i] == ',') i++;
			}
			return "";
		}
		static long FieldLong(string s, int a, int b, string key) {
			string v = FieldStr(s, a, b, key);
			long n;
			if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
			return 0;
		}
		static void AddRow(List<SearchRow> rows, Dictionary<string, bool> seen, string hex, string name, long size, int seeds, int leech, string source) {
			if (string.IsNullOrEmpty(hex)) return;
			hex = hex.Trim().ToLowerInvariant();
			if (IsZeroHash(hex)) return;
			if (seen.ContainsKey(hex)) return;
			seen[hex] = true;
			SearchRow r = new SearchRow();
			r.Name = string.IsNullOrEmpty(name) ? hex : name;
			r.SizeBytes = size;
			r.SizeText = Engine.Fmt(size);
			r.Seeds = seeds;
			r.Leech = leech;
			r.SeedsText = seeds.ToString(CultureInfo.InvariantCulture);
			r.LeechText = leech.ToString(CultureInfo.InvariantCulture);
			r.Source = source;
			r.Hash = hex;
			r.Magnet = MakeMagnet(hex, r.Name);
			rows.Add(r);
		}
		public static void ParseObjects(string json, string hexKey, string sizeKey, string source, List<SearchRow> rows, Dictionary<string, bool> seen) {
			if (string.IsNullOrEmpty(json) || rows == null || seen == null) return;
			int i = 0;
			while (i < json.Length) {
				int a = json.IndexOf('{', i);
				if (a < 0) break;
				int b = FindMatching(json, a);
				if (b < 0) break;
				string hex = FieldStr(json, a, b, hexKey);
				if (hex.Length < 40) {
					if (hexKey != "info_hash") hex = FieldStr(json, a, b, "info_hash");
					if (hex.Length < 40 && hexKey != "infohash") hex = FieldStr(json, a, b, "infohash");
				}
				if (hex.Length >= 40) {
					string name = FieldStr(json, a, b, "name");
					long size = FieldLong(json, a, b, sizeKey);
					if (size <= 0) size = FieldLong(json, a, b, sizeKey == "size" ? "size_bytes" : "size");
					int seeds = (int)FieldLong(json, a, b, "seeders");
					int leech = (int)FieldLong(json, a, b, "leechers");
					AddRow(rows, seen, hex, name, size, seeds, leech, source);
					i = b + 1;
				} else {
					i = a + 1;
				}
			}
		}
		public static List<SearchRow> Run(string query, out string status) {
			return Run(query, true, true, out status);
		}
		public static List<SearchRow> Run(string query, bool useTpb, bool useCsv, out string status) {
			List<SearchRow> rows = new List<SearchRow>();
			Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			status = "";
			if (!useTpb && !useCsv) {
				status = "none";
				return rows;
			}
			if (VpnHub.Blocked) {
				status = "blocked";
				return rows;
			}
			if (string.IsNullOrEmpty(query) || query.Trim().Length == 0) {
				status = "empty";
				return rows;
			}
			string enc = Uri.EscapeDataString(query.Trim());
			string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
			int tpbN = 0, csvN = 0;
			string tpbErr = null, csvErr = null;
			int st; byte[] body;
			if (useTpb) {
				if (VpnHub.Blocked) { status = "blocked"; return rows; }
				if (!VpnHub.HttpGet(TpbUrl + enc, 20000, ua, out st, out body) || body == null)
					tpbErr = st > 0 ? st.ToString(CultureInfo.InvariantCulture) : "failed";
				else {
					int before = rows.Count;
					ParseObjects(Encoding.UTF8.GetString(body), "info_hash", "size", "The Pirate Bay", rows, seen);
					tpbN = rows.Count - before;
				}
			}
			if (useCsv) {
				if (VpnHub.Blocked) {
					if (csvErr == null) csvErr = "blocked";
				} else if (!VpnHub.HttpGet(CsvUrl + enc, 20000, ua, out st, out body) || body == null)
					csvErr = st > 0 ? st.ToString(CultureInfo.InvariantCulture) : "failed";
				else {
					List<SearchRow> csvRows = new List<SearchRow>();
					Dictionary<string, bool> csvSeen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
					ParseObjects(Encoding.UTF8.GetString(body), "infohash", "size_bytes", "torrents-csv", csvRows, csvSeen);
					csvN = csvRows.Count;
					for (int ci = 0; ci < csvRows.Count; ci++) {
						SearchRow cr = csvRows[ci];
						AddRow(rows, seen, cr.Hash, cr.Name, cr.SizeBytes, cr.Seeds, cr.Leech, cr.Source);
					}
				}
			}
			rows.Sort(delegate(SearchRow x, SearchRow y) { return y.Seeds.CompareTo(x.Seeds); });
			StringBuilder sb = new StringBuilder();
			if (useTpb) {
				sb.Append("The Pirate Bay: ").Append(tpbN.ToString(CultureInfo.InvariantCulture));
				if (tpbErr != null) sb.Append(" (").Append(tpbErr).Append(")");
			}
			if (useCsv) {
				if (sb.Length > 0) sb.Append("    ");
				sb.Append("Torrents-CSV: ").Append(csvN.ToString(CultureInfo.InvariantCulture));
				if (csvErr != null) sb.Append(" (").Append(csvErr).Append(")");
			}
			if (sb.Length > 0) sb.Append("    ");
			sb.Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(" results");
			status = sb.ToString();
			return rows;
		}
	}

	public sealed class SearchResult {
		public bool Ready;
		public List<SearchRow> Rows;
		public string Status;
	}

	public static class SearchJob {
		static readonly object gate = new object();
		static volatile bool busy;
		static List<SearchRow> ready;
		static string readyStatus;
		public static bool IsBusy { get { return busy; } }
		public static bool Start(string query) {
			return Start(query, true, true);
		}
		public static bool Start(string query, bool useTpb, bool useCsv) {
			lock (gate) {
				if (busy) return false;
				busy = true;
				ready = null;
				readyStatus = null;
			}
			Thread t = new Thread(delegate() {
				string st;
				List<SearchRow> r;
				try { r = TorrentSearch.Run(query, useTpb, useCsv, out st); }
				catch (Exception ex) {
					st = ex.Message;
					r = new List<SearchRow>();
				}
				lock (gate) {
					ready = r;
					readyStatus = st;
					busy = false;
				}
			});
			t.IsBackground = true;
			t.Name = "pt-search";
			t.Start();
			return true;
		}
		public static SearchResult Poll() {
			SearchResult r = new SearchResult();
			lock (gate) {
				if (busy) return r;
				if (ready == null && readyStatus == null) return r;
				r.Ready = true;
				r.Rows = ready;
				r.Status = readyStatus;
				ready = null;
				readyStatus = null;
			}
			return r;
		}
	}

	public sealed class FileRow : INotifyPropertyChanged {
		public event PropertyChangedEventHandler PropertyChanged;
		void Notify(string n) {
			PropertyChangedEventHandler h = PropertyChanged;
			if (h != null) h(this, new PropertyChangedEventArgs(n));
		}
		string name = "";
		string sizeText = "";
		string progressText = "0%";
		double progress;
		public string Name { get { return name; } set { if (name != value) { name = value; Notify("Name"); } } }
		public string SizeText { get { return sizeText; } set { if (sizeText != value) { sizeText = value; Notify("SizeText"); } } }
		public double Progress { get { return progress; } set { if (progress != value) { progress = value; Notify("Progress"); } } }
		public string ProgressText { get { return progressText; } set { if (progressText != value) { progressText = value; Notify("ProgressText"); } } }
		public FileRow() { }
	}

	internal static class Bt {
		public static void W32(byte[] b, int o, int v) {
			b[o] = (byte)((v >> 24) & 0xFF);
			b[o + 1] = (byte)((v >> 16) & 0xFF);
			b[o + 2] = (byte)((v >> 8) & 0xFF);
			b[o + 3] = (byte)(v & 0xFF);
		}
		public static int R32(byte[] b, int o) {
			return (int)(((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | (uint)b[o + 3]);
		}
		public static void W64(byte[] b, int o, long v) {
			ulong u = (ulong)v;
			b[o] = (byte)(u >> 56);
			b[o + 1] = (byte)(u >> 48);
			b[o + 2] = (byte)(u >> 40);
			b[o + 3] = (byte)(u >> 32);
			b[o + 4] = (byte)(u >> 24);
			b[o + 5] = (byte)(u >> 16);
			b[o + 6] = (byte)(u >> 8);
			b[o + 7] = (byte)u;
		}
		public static long R64(byte[] b, int o) {
			uint hi = (uint)R32(b, o);
			uint lo = (uint)R32(b, o + 4);
			return (long)(((ulong)hi << 32) | (ulong)lo);
		}
		public static string UrlEnc(byte[] data) {
			char[] hex = "0123456789ABCDEF".ToCharArray();
			StringBuilder sb = new StringBuilder(data.Length * 3);
			for (int i = 0; i < data.Length; i++) {
				sb.Append('%');
				sb.Append(hex[(data[i] >> 4) & 0xF]);
				sb.Append(hex[data[i] & 0xF]);
			}
			return sb.ToString();
		}
		public static string SafeName(string n) {
			if (string.IsNullOrEmpty(n)) return "unknown";
			n = n.Replace('/', '_').Replace('\\', '_');
			char[] bad = Path.GetInvalidFileNameChars();
			StringBuilder sb = new StringBuilder(n.Length);
			for (int i = 0; i < n.Length; i++) {
				char c = n[i];
				bool ok = true;
				for (int j = 0; j < bad.Length; j++) {
					if (c == bad[j]) { ok = false; break; }
				}
				sb.Append(ok ? c : '_');
			}
			string s = sb.ToString().Trim();
			if (s.Length == 0 || s == "." || s == "..") return "_";
			return s;
		}
		public static void AddCompactPeers(byte[] data, List<string> dst) {
			if (data == null) return;
			int n = data.Length / 6;
			for (int i = 0; i < n; i++) {
				int o = i * 6;
				int port = (data[o + 4] << 8) | data[o + 5];
				byte b0 = data[o];
				if (port <= 0 || port > 65535) continue;
				if (b0 == 0 || b0 == 127 || b0 == 255) continue;
				dst.Add(string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}:{4}", data[o], data[o + 1], data[o + 2], data[o + 3], port));
			}
		}
		public static void AddCompact6(byte[] data, List<string> dst) {
			if (data == null) return;
			int n = data.Length / 18;
			for (int i = 0; i < n; i++) {
				int o = i * 18;
				int port = (data[o + 16] << 8) | data[o + 17];
				if (port <= 0 || port > 65535) continue;
				byte[] raw = new byte[16];
				Buffer.BlockCopy(data, o, raw, 0, 16);
				IPAddress ip = new IPAddress(raw);
				if (IPAddress.IsLoopback(ip)) continue;
				dst.Add("[" + ip.ToString() + "]:" + port.ToString(CultureInfo.InvariantCulture));
			}
		}
		public static IPAddress ResolveV4(string host) {
			try {
				IPAddress parsed;
				if (IPAddress.TryParse(host, out parsed)) {
					if (parsed.AddressFamily == AddressFamily.InterNetwork) return parsed;
					return null;
				}
				if (VpnHub.TunnelOn) return VpnHub.Resolve(host);
				if (!VpnHub.OsAllowed) return null;
				IPAddress[] addrs = Dns.GetHostAddresses(host);
				for (int i = 0; i < addrs.Length; i++) {
					if (addrs[i].AddressFamily == AddressFamily.InterNetwork) return addrs[i];
				}
			} catch {
			}
			return null;
		}
		public static byte[] MakePeerId() {
			byte[] id = new byte[20];
			byte[] prefix = Encoding.ASCII.GetBytes("-PT1500-");
			Buffer.BlockCopy(prefix, 0, id, 0, 8);
			RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
			byte[] r = new byte[12];
			rng.GetBytes(r);
			rng.Dispose();
			string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
			for (int i = 0; i < 12; i++) id[8 + i] = (byte)alphabet[r[i] % alphabet.Length];
			return id;
		}
	}

	internal sealed class Be {
		public int Kind;
		public long I;
		public byte[] Bytes;
		public List<Be> List;
		public Dictionary<string, Be> Dict;
		public List<KeyValuePair<byte[], Be>> Pairs;
		public static Be Int(long n) { Be b = new Be(); b.Kind = 0; b.I = n; return b; }
		public static Be Blob(byte[] x) { Be b = new Be(); b.Kind = 1; b.Bytes = x; return b; }
		public static Be FromList(List<Be> x) { Be b = new Be(); b.Kind = 2; b.List = x; return b; }
		public static Be FromDict(Dictionary<string, Be> x) { Be b = new Be(); b.Kind = 3; b.Dict = x; return b; }
		public Be Get(string key) {
			if (Dict == null) return null;
			Be v;
			if (Dict.TryGetValue(key, out v)) return v;
			return null;
		}
		public string Str(string key) {
			Be v = Get(key);
			if (v == null || v.Bytes == null) return null;
			return Encoding.UTF8.GetString(v.Bytes);
		}
		public long GetInt(string key, long def) {
			Be v = Get(key);
			if (v == null || v.Kind != 0) return def;
			return v.I;
		}
	}

	internal sealed class Benc {
		byte[] d;
		int i;
		int depth;
		Benc(byte[] data) { d = data; i = 0; depth = 0; }

		public static Be Decode(byte[] data) {
			int n;
			return DecodeAt(data, 0, out n);
		}

		public static Be DecodeAt(byte[] data, int offset, out int consumed) {
			consumed = 0;
			if (data == null || offset < 0 || offset >= data.Length) throw new Exception("empty bencode");
			Benc p = new Benc(data);
			p.i = offset;
			Be v = p.Parse();
			consumed = p.i - offset;
			return v;
		}

		public static byte[] SliceDictValue(byte[] data, string key) {
			if (data == null || data.Length == 0 || data[0] != (byte)'d') return null;
			Benc p = new Benc(data);
			p.i = 1;
			while (p.i < p.d.Length && p.d[p.i] != (byte)'e') {
				Be k = p.Parse();
				int vs = p.i;
				p.Parse();
				int ve = p.i;
				if (k.Bytes != null && Encoding.UTF8.GetString(k.Bytes) == key) {
					byte[] sl = new byte[ve - vs];
					Buffer.BlockCopy(p.d, vs, sl, 0, sl.Length);
					return sl;
				}
			}
			return null;
		}

		public static byte[] Encode(Be v) {
			MemoryStream ms = new MemoryStream();
			Enc(v, ms);
			return ms.ToArray();
		}

		static void Enc(Be v, MemoryStream ms) {
			if (v.Kind == 0) {
				byte[] t = Encoding.ASCII.GetBytes("i" + v.I.ToString(CultureInfo.InvariantCulture) + "e");
				ms.Write(t, 0, t.Length);
			} else if (v.Kind == 1) {
				byte[] t = Encoding.ASCII.GetBytes(v.Bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
				ms.Write(t, 0, t.Length);
				ms.Write(v.Bytes, 0, v.Bytes.Length);
			} else if (v.Kind == 2) {
				ms.WriteByte((byte)'l');
				for (int n = 0; n < v.List.Count; n++) Enc(v.List[n], ms);
				ms.WriteByte((byte)'e');
			} else {
				ms.WriteByte((byte)'d');
				List<string> keys = new List<string>(v.Dict.Keys);
				keys.Sort(StringComparer.Ordinal);
				for (int n = 0; n < keys.Count; n++) {
					Enc(Be.Blob(Encoding.UTF8.GetBytes(keys[n])), ms);
					Enc(v.Dict[keys[n]], ms);
				}
				ms.WriteByte((byte)'e');
			}
		}

		Be Parse() {
			if (depth > 64) throw new Exception("bencode too nested");
			if (i >= d.Length) throw new Exception("truncated bencode");
			byte c = d[i];
			if (c == (byte)'i') return ParseInt();
			if (c == (byte)'l') return ParseList();
			if (c == (byte)'d') return ParseDict();
			if (c >= (byte)'0' && c <= (byte)'9') return ParseBytes();
			throw new Exception("invalid bencode at " + i.ToString(CultureInfo.InvariantCulture));
		}

		Be ParseInt() {
			i++;
			int s = i;
			if (i < d.Length && d[i] == (byte)'-') i++;
			if (i >= d.Length || d[i] < (byte)'0' || d[i] > (byte)'9') throw new Exception("bad int");
			while (i < d.Length && d[i] != (byte)'e') {
				if (d[i] < (byte)'0' || d[i] > (byte)'9') throw new Exception("bad int");
				i++;
			}
			if (i >= d.Length) throw new Exception("unterminated int");
			string ns = Encoding.ASCII.GetString(d, s, i - s);
			i++;
			return Be.Int(long.Parse(ns, CultureInfo.InvariantCulture));
		}

		Be ParseBytes() {
			int s = i;
			while (i < d.Length && d[i] != (byte)':') i++;
			if (i >= d.Length) throw new Exception("bad string");
			int len = int.Parse(Encoding.ASCII.GetString(d, s, i - s), CultureInfo.InvariantCulture);
			i++;
			if (len < 0 || i + len > d.Length) throw new Exception("string overflow");
			byte[] b = new byte[len];
			Buffer.BlockCopy(d, i, b, 0, len);
			i += len;
			return Be.Blob(b);
		}

		Be ParseList() {
			depth++;
			i++;
			List<Be> list = new List<Be>();
			while (i < d.Length && d[i] != (byte)'e') list.Add(Parse());
			if (i >= d.Length) throw new Exception("unterminated list");
			i++;
			depth--;
			return Be.FromList(list);
		}

		Be ParseDict() {
			depth++;
			i++;
			Dictionary<string, Be> dict = new Dictionary<string, Be>();
			List<KeyValuePair<byte[], Be>> pairs = new List<KeyValuePair<byte[], Be>>();
			while (i < d.Length && d[i] != (byte)'e') {
				Be k = Parse();
				Be v = Parse();
				if (k.Bytes == null) throw new Exception("dict key is not a string");
				dict[Encoding.UTF8.GetString(k.Bytes)] = v;
				pairs.Add(new KeyValuePair<byte[], Be>(k.Bytes, v));
			}
			if (i >= d.Length) throw new Exception("unterminated dict");
			i++;
			depth--;
			Be b = Be.FromDict(dict);
			b.Pairs = pairs;
			return b;
		}
	}

	internal sealed class Rc4 {
		byte[] s = new byte[256];
		int i, j;
		public Rc4(byte[] key) {
			for (int k = 0; k < 256; k++) s[k] = (byte)k;
			int jj = 0;
			for (int k = 0; k < 256; k++) {
				jj = (jj + s[k] + key[k % key.Length]) & 255;
				byte t = s[k]; s[k] = s[jj]; s[jj] = t;
			}
			i = 0; j = 0;
		}
		Rc4() { }
		public Rc4 Clone() {
			Rc4 c = new Rc4();
			c.s = (byte[])s.Clone();
			c.i = i; c.j = j;
			return c;
		}
		public void Crypt(byte[] buf, int off, int n) {
			for (int k = 0; k < n; k++) {
				i = (i + 1) & 255;
				j = (j + s[i]) & 255;
				byte t = s[i]; s[i] = s[j]; s[j] = t;
				buf[off + k] ^= s[(s[i] + s[j]) & 255];
			}
		}
		public void Discard(int n) {
			byte[] t = new byte[n];
			Crypt(t, 0, n);
		}
	}

	internal static class Crypto {
		static readonly BigInteger DhP = BigInteger.Parse("00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563", NumberStyles.AllowHexSpecifier);
		static readonly BigInteger DhG = new BigInteger(2);

		public static byte[] Sha1(params byte[][] parts) {
			int len = 0;
			for (int i = 0; i < parts.Length; i++) len += parts[i].Length;
			byte[] all = new byte[len];
			int o = 0;
			for (int i = 0; i < parts.Length; i++) { Buffer.BlockCopy(parts[i], 0, all, o, parts[i].Length); o += parts[i].Length; }
			using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) return sha.ComputeHash(all);
		}
		public static byte[] Sha256(byte[] d, int off, int len) {
			using (SHA256CryptoServiceProvider sha = new SHA256CryptoServiceProvider()) return sha.ComputeHash(d, off, len);
		}
		public static byte[] Sha256All(byte[] d) { return Sha256(d, 0, d.Length); }

		public static BigInteger RandomX() {
			byte[] r = new byte[21];
			RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
			rng.GetBytes(r);
			rng.Dispose();
			r[20] = 0;
			r[0] |= 1;
			return new BigInteger(r);
		}
		public static byte[] Int96(BigInteger v) {
			byte[] le = v.ToByteArray();
			byte[] be = new byte[96];
			int n = le.Length;
			if (n > 0 && le[n - 1] == 0) n--;
			for (int i = 0; i < n && i < 96; i++) be[95 - i] = le[i];
			return be;
		}
		public static BigInteger From96(byte[] be) {
			byte[] le = new byte[97];
			for (int i = 0; i < 96; i++) le[i] = be[95 - i];
			return new BigInteger(le);
		}
		public static byte[] DhPub(BigInteger x) { return Int96(BigInteger.ModPow(DhG, x, DhP)); }
		public static byte[] DhSecret(byte[] their96, BigInteger x) {
			return Int96(BigInteger.ModPow(From96(their96), x, DhP));
		}
		public static Rc4 Rc4Key(bool initiatorSend, byte[] S, byte[] skey) {
			byte[] tag = Encoding.ASCII.GetBytes(initiatorSend ? "keyA" : "keyB");
			Rc4 r = new Rc4(Sha1(tag, S, skey));
			r.Discard(1024);
			return r;
		}
		public static byte[] MerklePiece(byte[] piece, int len) {
			List<byte[]> nodes = new List<byte[]>();
			int i = 0;
			while (i < len) {
				int n = Math.Min(16384, len - i);
				nodes.Add(Sha256(piece, i, n));
				i += n;
			}
			if (nodes.Count == 0) nodes.Add(Sha256(new byte[0], 0, 0));
			while (nodes.Count > 1) {
				List<byte[]> next = new List<byte[]>();
				for (int k = 0; k < nodes.Count; k += 2) {
					byte[] cat = new byte[64];
					Buffer.BlockCopy(nodes[k], 0, cat, 0, 32);
					if (k + 1 < nodes.Count) Buffer.BlockCopy(nodes[k + 1], 0, cat, 32, 32);
					next.Add(Sha256(cat, 0, 64));
				}
				nodes = next;
			}
			return nodes[0];
		}
		public static byte[] MerkleRoot(List<byte[]> layers) {
			if (layers == null || layers.Count == 0) return Sha256(new byte[0], 0, 0);
			List<byte[]> nodes = new List<byte[]>(layers);
			while (nodes.Count > 1) {
				List<byte[]> next = new List<byte[]>();
				for (int k = 0; k < nodes.Count; k += 2) {
					byte[] cat = new byte[64];
					Buffer.BlockCopy(nodes[k], 0, cat, 0, 32);
					if (k + 1 < nodes.Count) Buffer.BlockCopy(nodes[k + 1], 0, cat, 32, 32);
					next.Add(Sha256(cat, 0, 64));
				}
				nodes = next;
			}
			return nodes[0];
		}
	}

	internal abstract class PeerIo {
		public string Transport = "TCP";
		public abstract bool ReadExact(byte[] buf, int off, int n, int timeoutMs);
		public abstract void Write(byte[] buf, int n);
		public abstract bool PollRead(int microSeconds);
		public abstract int Available { get; }
		public abstract void Close();
		public virtual IPEndPoint Remote { get { return null; } }
	}

	internal sealed class TcpPeerIo : PeerIo {
		TcpClient tcp;
		Stream st;
		VpnTcp vt;
		Rc4 sendRc4, recvRc4;
		byte[] push = new byte[0];
		int pushOff;
		public TcpPeerIo(TcpClient c) {
			tcp = c;
			tcp.NoDelay = true;
			try { tcp.ReceiveBufferSize = 512 * 1024; } catch { }
			try { tcp.SendBufferSize = 256 * 1024; } catch { }
			st = c.GetStream();
			Transport = "TCP";
		}
		public TcpPeerIo(VpnTcp v) {
			vt = v;
			st = new VpnTcpStream(v);
			Transport = "TCP/WG";
		}
		public static TcpPeerIo Dial(string host, int port, int timeoutMs) {
			if (VpnHub.TunnelOn) {
				VpnTcp v = VpnHub.ConnectTcp(host, port, timeoutMs);
				if (v == null) return null;
				return new TcpPeerIo(v);
			}
			if (!VpnHub.OsAllowed) return null;
			TcpClient c = new TcpClient();
			c.NoDelay = true;
			c.ReceiveBufferSize = 512 * 1024;
			c.SendBufferSize = 256 * 1024;
			IAsyncResult ar = c.BeginConnect(host, port, null, null);
			if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) {
				try { c.Close(); } catch { }
				return null;
			}
			try { c.EndConnect(ar); } catch { try { c.Close(); } catch { } return null; }
			return new TcpPeerIo(c);
		}
		void PushFront(byte[] d, int off, int n) {
			byte[] nb = new byte[n + (push.Length - pushOff)];
			Buffer.BlockCopy(d, off, nb, 0, n);
			if (push.Length - pushOff > 0) Buffer.BlockCopy(push, pushOff, nb, n, push.Length - pushOff);
			push = nb; pushOff = 0;
		}
		bool ReadRaw(byte[] buf, int off, int n, int timeoutMs) {
			int got = 0;
			while (got < n && pushOff < push.Length) buf[off + got++] = push[pushOff++];
			if (pushOff >= push.Length) { push = new byte[0]; pushOff = 0; }
			if (vt != null) {
				while (got < n) {
					int r = vt.Read(buf, off + got, n - got, timeoutMs);
					if (r <= 0) return false;
					got += r;
				}
				return true;
			}
			if (tcp != null) tcp.ReceiveTimeout = timeoutMs;
			while (got < n) {
				int r;
				try { r = st.Read(buf, off + got, n - got); }
				catch { return false; }
				if (r <= 0) return false;
				got += r;
			}
			return true;
		}
		public override bool ReadExact(byte[] buf, int off, int n, int timeoutMs) {
			if (!ReadRaw(buf, off, n, timeoutMs)) return false;
			if (recvRc4 != null) recvRc4.Crypt(buf, off, n);
			return true;
		}
		public override void Write(byte[] buf, int n) {
			byte[] outb = buf;
			if (sendRc4 != null) {
				outb = new byte[n];
				Buffer.BlockCopy(buf, 0, outb, 0, n);
				sendRc4.Crypt(outb, 0, n);
			}
			if (vt != null) vt.Write(outb, 0, n);
			else st.Write(outb, 0, n);
		}
		public override bool PollRead(int microSeconds) {
			try {
				if (pushOff < push.Length) return true;
				if (vt != null) return vt.Poll(microSeconds);
				NetworkStream ns = st as NetworkStream;
				if (ns != null && ns.DataAvailable) return true;
				return tcp.Client.Poll(microSeconds, SelectMode.SelectRead);
			} catch { return false; }
		}
		public override int Available {
			get {
				int a = push.Length - pushOff;
				if (vt != null) return a + vt.Available;
				try { a += tcp.Client.Available; } catch { }
				return a;
			}
		}
		public override void Close() {
			try { if (st != null) st.Close(); } catch { }
			try { if (vt != null) vt.Close(); } catch { }
			try { if (tcp != null) tcp.Close(); } catch { }
		}
		public override IPEndPoint Remote {
			get {
				try {
					if (tcp != null && tcp.Client != null && tcp.Client.RemoteEndPoint != null)
						return (IPEndPoint)tcp.Client.RemoteEndPoint;
				} catch { }
				if (vt != null && vt.remIp != 0) {
					uint ip = vt.remIp;
					IPAddress a = new IPAddress(new byte[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });
					return new IPEndPoint(a, vt.remPort);
				}
				return null;
			}
		}
		bool SockReady() {
			if (vt != null) return vt.Available > 0;
			try { return tcp != null && tcp.Client.Available > 0; } catch { return false; }
		}

		public bool TryMseOutgoing(byte[] skey) {
			try {
				BigInteger x = Crypto.RandomX();
				byte[] ya = Crypto.DhPub(x);
				RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
				byte[] pad = new byte[24];
				rng.GetBytes(pad);
				rng.Dispose();
				byte[] pkt = new byte[96 + pad.Length];
				Buffer.BlockCopy(ya, 0, pkt, 0, 96);
				Buffer.BlockCopy(pad, 0, pkt, 96, pad.Length);
				st.Write(pkt, 0, pkt.Length);

				byte[] yb = new byte[96];
				if (!ReadRaw(yb, 0, 96, 5000)) return false;
				byte[] S = Crypto.DhSecret(yb, x);
				sendRc4 = Crypto.Rc4Key(true, S, skey);
				recvRc4 = Crypto.Rc4Key(false, S, skey);

				byte[] req1 = Crypto.Sha1(Encoding.ASCII.GetBytes("req1"), S);
				byte[] xor = Crypto.Sha1(Encoding.ASCII.GetBytes("req2"), skey);
				byte[] r3 = Crypto.Sha1(Encoding.ASCII.GetBytes("req3"), S);
				for (int i = 0; i < 20; i++) xor[i] ^= r3[i];
				byte[] vc = new byte[8];
				byte[] provide = new byte[4]; provide[3] = 0x03;
				byte[] padCLen = new byte[2];
				byte[] iaLen = new byte[2];
				byte[] encPart = new byte[8 + 4 + 2 + 2];
				Buffer.BlockCopy(vc, 0, encPart, 0, 8);
				Buffer.BlockCopy(provide, 0, encPart, 8, 4);
				sendRc4.Crypt(encPart, 0, encPart.Length);
				byte[] p3 = new byte[40 + encPart.Length];
				Buffer.BlockCopy(req1, 0, p3, 0, 20);
				Buffer.BlockCopy(xor, 0, p3, 20, 20);
				Buffer.BlockCopy(encPart, 0, p3, 40, encPart.Length);
				st.Write(p3, 0, p3.Length);

				byte[] rest = new byte[528];
				if (tcp != null) tcp.ReceiveTimeout = 5000;
				int got = 0;
				DateTime dead = DateTime.UtcNow.AddMilliseconds(5000);
				while (got < 8 && DateTime.UtcNow < dead) {
					if (!SockReady()) { Thread.Sleep(20); continue; }
					int r = st.Read(rest, got, rest.Length - got);
					if (r <= 0) break;
					got += r;
				}
				if (got < 8) return false;
				byte[] want = new byte[8];
				Rc4 probe = recvRc4.Clone();
				probe.Crypt(want, 0, 8);
				int start = -1;
				for (int s = 0; s <= got - 8; s++) {
					bool match = true;
					for (int k = 0; k < 8; k++) if (rest[s + k] != want[k]) { match = false; break; }
					if (match) { start = s; break; }
				}
				if (start < 0) return false;
				int left = got - start;
				byte[] p4 = new byte[Math.Max(left, 16)];
				Buffer.BlockCopy(rest, start, p4, 0, left);
				int need = 8 + 4 + 2;
				while (left < need) {
					int r = st.Read(p4, left, p4.Length - left);
					if (r <= 0) return false;
					left += r;
				}
				recvRc4.Crypt(p4, 0, 8);
				for (int k = 0; k < 8; k++) if (p4[k] != 0) return false;
				recvRc4.Crypt(p4, 8, 4);
				int sel = (p4[8] << 24) | (p4[9] << 16) | (p4[10] << 8) | p4[11];
				recvRc4.Crypt(p4, 12, 2);
				int padD = (p4[12] << 8) | p4[13];
				if (padD < 0 || padD > 512) return false;
				int have = left - 14;
				if (have < padD) {
					Array.Resize(ref p4, 14 + padD);
					if (!ReadRaw(p4, left, padD - have, 4000)) return false;
					left = 14 + padD;
				}
				if (padD > 0) recvRc4.Crypt(p4, 14, padD);
				int extra = left - (14 + padD);
				if (extra > 0) PushFront(p4, 14 + padD, extra);
				if ((sel & 2) == 0) { sendRc4 = null; recvRc4 = null; }
				Transport = "TCP/MSE";
				return true;
			} catch { return false; }
		}

		public void Unread(byte[] d, int off, int n) { PushFront(d, off, n); }

		public bool AcceptRouted(bool allowMse, out Engine matched) {
			matched = null;
			byte[] first = new byte[20];
			if (!ReadRaw(first, 0, 20, 8000)) return false;
			if (first[0] == 19 && Encoding.ASCII.GetString(first, 1, 19) == "BitTorrent protocol") {
				byte[] hs = new byte[68];
				Buffer.BlockCopy(first, 0, hs, 0, 20);
				if (!ReadRaw(hs, 20, 48, 8000)) return false;
				byte[] ih = new byte[20];
				Buffer.BlockCopy(hs, 28, ih, 0, 20);
				matched = Session.FindByHash(ih);
				PushFront(hs, 0, 68);
				return matched != null;
			}
			if (!allowMse) return false;
			PushFront(first, 0, 20);
			return AcceptMseRouted(out matched);
		}

		bool AcceptMseRouted(out Engine matched) {
			matched = null;
			try {
				byte[] ya = new byte[96];
				if (!ReadRaw(ya, 0, 96, 5000)) return false;
				BigInteger x = Crypto.RandomX();
				byte[] yb = Crypto.DhPub(x);
				RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
				byte[] pad = new byte[24];
				rng.GetBytes(pad);
				rng.Dispose();
				byte[] pkt = new byte[96 + pad.Length];
				Buffer.BlockCopy(yb, 0, pkt, 0, 96);
				Buffer.BlockCopy(pad, 0, pkt, 96, pad.Length);
				st.Write(pkt, 0, pkt.Length);
				byte[] S = Crypto.DhSecret(ya, x);
				byte[] req1 = Crypto.Sha1(Encoding.ASCII.GetBytes("req1"), S);
				byte[] acc = new byte[640];
				int got = 0;
				DateTime dead = DateTime.UtcNow.AddMilliseconds(6000);
				int hit = -1;
				while (DateTime.UtcNow < dead && hit < 0) {
					if (SockReady()) {
						int r = st.Read(acc, got, acc.Length - got);
						if (r <= 0) break;
						got += r;
					} else Thread.Sleep(15);
					for (int s = 0; s <= got - 20; s++) {
						bool m = true;
						for (int k = 0; k < 20; k++) if (acc[s + k] != req1[k]) { m = false; break; }
						if (m) { hit = s; break; }
					}
				}
				if (hit < 0) return false;
				int pos = hit + 20;
				while (got < pos + 20) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				byte[] rec = new byte[20];
				Buffer.BlockCopy(acc, pos, rec, 0, 20);
				byte[] r3 = Crypto.Sha1(Encoding.ASCII.GetBytes("req3"), S);
				for (int i = 0; i < 20; i++) rec[i] ^= r3[i];
				byte[][] keys = Session.AllInfoHashes();
				byte[] skey = null;
				for (int ki = 0; ki < keys.Length; ki++) {
					byte[] expect = Crypto.Sha1(Encoding.ASCII.GetBytes("req2"), keys[ki]);
					bool ok = true;
					for (int i = 0; i < 20; i++) if (rec[i] != expect[i]) { ok = false; break; }
					if (ok) { skey = keys[ki]; break; }
				}
				if (skey == null) return false;
				matched = Session.FindByHash(skey);
				if (matched == null) return false;
				pos += 20;
				sendRc4 = Crypto.Rc4Key(false, S, skey);
				recvRc4 = Crypto.Rc4Key(true, S, skey);
				int need = pos + 8 + 4 + 2;
				while (got < need) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				recvRc4.Crypt(acc, pos, 8);
				for (int k = 0; k < 8; k++) if (acc[pos + k] != 0) return false;
				pos += 8;
				recvRc4.Crypt(acc, pos, 4);
				int provide = (acc[pos] << 24) | (acc[pos + 1] << 16) | (acc[pos + 2] << 8) | acc[pos + 3];
				pos += 4;
				recvRc4.Crypt(acc, pos, 2);
				int padC = (acc[pos] << 8) | (acc[pos + 1] & 0xFF);
				pos += 2;
				if (padC < 0 || padC > 512) return false;
				while (got < pos + padC + 2) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				if (padC > 0) { recvRc4.Crypt(acc, pos, padC); pos += padC; }
				recvRc4.Crypt(acc, pos, 2);
				int ia = (acc[pos] << 8) | (acc[pos + 1] & 0xFF);
				pos += 2;
				while (got < pos + ia) {
					Array.Resize(ref acc, Math.Max(acc.Length * 2, pos + ia));
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				if (ia > 0) { recvRc4.Crypt(acc, pos, ia); PushFront(acc, pos, ia); pos += ia; }
				if (got > pos) PushFront(acc, pos, got - pos);
				byte[] p4 = new byte[14];
				int sel = ((provide & 2) != 0) ? 2 : 1;
				p4[11] = (byte)sel;
				sendRc4.Crypt(p4, 0, 8);
				sendRc4.Crypt(p4, 8, 4);
				sendRc4.Crypt(p4, 12, 2);
				st.Write(p4, 0, 14);
				if (sel != 2) { sendRc4 = null; recvRc4 = null; Transport = "TCP"; }
				else Transport = "TCP/MSE";
				return true;
			} catch { matched = null; return false; }
		}

		public bool AcceptMseOrPlain(byte[] skey, bool allowMse) {
			byte[] first = new byte[20];
			if (!ReadRaw(first, 0, 20, 8000)) return false;
			if (first[0] == 19 && Encoding.ASCII.GetString(first, 1, 19) == "BitTorrent protocol") {
				PushFront(first, 0, 20);
				Transport = "TCP";
				return true;
			}
			if (!allowMse) return false;
			PushFront(first, 0, 20);
			try {
				byte[] ya = new byte[96];
				if (!ReadRaw(ya, 0, 96, 5000)) return false;
				BigInteger x = Crypto.RandomX();
				byte[] yb = Crypto.DhPub(x);
				RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
				byte[] pad = new byte[24];
				rng.GetBytes(pad);
				rng.Dispose();
				byte[] pkt = new byte[96 + pad.Length];
				Buffer.BlockCopy(yb, 0, pkt, 0, 96);
				Buffer.BlockCopy(pad, 0, pkt, 96, pad.Length);
				st.Write(pkt, 0, pkt.Length);
				byte[] S = Crypto.DhSecret(ya, x);
				byte[] req1 = Crypto.Sha1(Encoding.ASCII.GetBytes("req1"), S);
				byte[] acc = new byte[640];
				int got = 0;
				DateTime dead = DateTime.UtcNow.AddMilliseconds(6000);
				int hit = -1;
				while (DateTime.UtcNow < dead && hit < 0) {
					if (SockReady()) {
						int r = st.Read(acc, got, acc.Length - got);
						if (r <= 0) break;
						got += r;
					} else Thread.Sleep(15);
					for (int s = 0; s <= got - 20; s++) {
						bool m = true;
						for (int k = 0; k < 20; k++) if (acc[s + k] != req1[k]) { m = false; break; }
						if (m) { hit = s; break; }
					}
				}
				if (hit < 0) return false;
				int pos = hit + 20;
				while (got < pos + 20) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				byte[] rec = new byte[20];
				Buffer.BlockCopy(acc, pos, rec, 0, 20);
				byte[] r3 = Crypto.Sha1(Encoding.ASCII.GetBytes("req3"), S);
				for (int i = 0; i < 20; i++) rec[i] ^= r3[i];
				byte[] expect = Crypto.Sha1(Encoding.ASCII.GetBytes("req2"), skey);
				for (int i = 0; i < 20; i++) if (rec[i] != expect[i]) return false;
				pos += 20;
				sendRc4 = Crypto.Rc4Key(false, S, skey);
				recvRc4 = Crypto.Rc4Key(true, S, skey);
				int need = pos + 8 + 4 + 2;
				while (got < need) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				recvRc4.Crypt(acc, pos, 8);
				for (int k = 0; k < 8; k++) if (acc[pos + k] != 0) return false;
				pos += 8;
				recvRc4.Crypt(acc, pos, 4);
				int provide = (acc[pos] << 24) | (acc[pos + 1] << 16) | (acc[pos + 2] << 8) | acc[pos + 3];
				pos += 4;
				recvRc4.Crypt(acc, pos, 2);
				int padC = (acc[pos] << 8) | acc[pos + 1];
				pos += 2;
				if (padC < 0 || padC > 512) return false;
				while (got < pos + padC + 2) {
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				if (padC > 0) { recvRc4.Crypt(acc, pos, padC); pos += padC; }
				recvRc4.Crypt(acc, pos, 2);
				int ia = (acc[pos] << 8) | acc[pos + 1];
				pos += 2;
				while (got < pos + ia) {
					Array.Resize(ref acc, Math.Max(acc.Length * 2, pos + ia));
					int r = st.Read(acc, got, acc.Length - got);
					if (r <= 0) return false;
					got += r;
				}
				if (ia > 0) { recvRc4.Crypt(acc, pos, ia); PushFront(acc, pos, ia); pos += ia; }
				if (got > pos) PushFront(acc, pos, got - pos);
				byte[] p4 = new byte[14];
				int sel = ((provide & 2) != 0) ? 2 : 1;
				p4[11] = (byte)sel;
				sendRc4.Crypt(p4, 0, 8);
				sendRc4.Crypt(p4, 8, 4);
				sendRc4.Crypt(p4, 12, 2);
				st.Write(p4, 0, 14);
				if (sel != 2) { sendRc4 = null; recvRc4 = null; Transport = "TCP"; }
				else Transport = "TCP/MSE";
				return true;
			} catch { return false; }
		}
	}

	internal sealed class UtpConn {
		public IPEndPoint Remote;
		public int SendId, RecvId;
		public int State; // 0 idle 1 syn 2 connected 3 closed
		UtpHub hub;
		ushort seq, ack;
		int nextIn;
		int remoteWnd = 64 * 1024;
		readonly object gate = new object();
		readonly Queue<byte> appIn = new Queue<byte>();
		readonly AutoResetEvent ev = new AutoResetEvent(false);
		readonly List<byte[]> inflight = new List<byte[]>();
		readonly List<ushort> infSeq = new List<ushort>();
		readonly List<DateTime> infSent = new List<DateTime>();
		readonly Dictionary<int, byte[]> reorder = new Dictionary<int, byte[]>();
		DateTime lastRecv = DateTime.UtcNow;
		const int Mss = 1200;

		public UtpConn(UtpHub hub) { this.hub = hub; }

		public bool Connect(string host, int port, int timeoutMs) {
			IPAddress ip;
			if (!IPAddress.TryParse(host, out ip) || ip == null) ip = Bt.ResolveV4(host);
			if (ip == null) return false;
			Remote = new IPEndPoint(ip, port);
			Random rng = new Random(Guid.NewGuid().GetHashCode());
			RecvId = rng.Next(1, 60000);
			SendId = RecvId + 1;
			seq = (ushort)rng.Next(1, 60000);
			ack = 0;
			nextIn = -1;
			State = 1;
			hub.Register(this);
			SendPkt(4, RecvId, seq, ack, null);
			seq++;
			DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < dead && State != 2 && State != 3) {
				ev.WaitOne(50);
			}
			return State == 2;
		}

		public void AcceptSyn(ushort synSeq) {
			Random rng = new Random(Guid.NewGuid().GetHashCode());
			seq = (ushort)rng.Next(1, 60000);
			ack = synSeq;
			nextIn = synSeq + 1;
			State = 2;
			SendPkt(2, SendId, seq, ack, null);
		}

		public void OnPacket(byte[] pkt) {
			if (pkt == null || pkt.Length < 20) return;
			int type = (pkt[0] >> 4) & 0xF;
			int ext = pkt[1];
			int hdr = 20;
			while (ext != 0 && hdr + 2 <= pkt.Length) {
				ext = pkt[hdr];
				int elen = pkt[hdr + 1];
				hdr += 2 + elen;
				if (hdr > pkt.Length) return;
			}
			ushort theirSeq = (ushort)((pkt[16] << 8) | pkt[17]);
			ushort theirAck = (ushort)((pkt[18] << 8) | pkt[19]);
			remoteWnd = (pkt[12] << 24) | (pkt[13] << 16) | (pkt[14] << 8) | pkt[15];
			lastRecv = DateTime.UtcNow;
			lock (gate) {
				for (int i = inflight.Count - 1; i >= 0; i--) {
					if ((short)(theirAck - infSeq[i]) >= 0) {
						inflight.RemoveAt(i); infSeq.RemoveAt(i); infSent.RemoveAt(i);
					}
				}
				if (type == 4) { // SYN incoming
					ack = theirSeq;
					nextIn = theirSeq + 1;
					State = 2;
					SendPkt(2, SendId, seq, ack, null);
					ev.Set();
					return;
				}
				if (State == 1 && type == 2) {
					ack = theirSeq;
					nextIn = theirSeq + 1;
					State = 2;
					ev.Set();
					return;
				}
				if (type == 3) { State = 3; ev.Set(); return; }
				if (type == 1) {
					ack = theirSeq;
					State = 3;
					SendPkt(2, SendId, seq, ack, null);
					ev.Set();
					return;
				}
				if (type == 0 && pkt.Length > hdr) {
					int pay = pkt.Length - hdr;
					byte[] data = new byte[pay];
					Buffer.BlockCopy(pkt, hdr, data, 0, pay);
					if (nextIn < 0) nextIn = theirSeq;
					if (theirSeq == (ushort)nextIn) {
						for (int i = 0; i < data.Length; i++) appIn.Enqueue(data[i]);
						nextIn = (nextIn + 1) & 0xFFFF;
						ack = theirSeq;
						while (reorder.ContainsKey(nextIn)) {
							byte[] more = reorder[nextIn];
							reorder.Remove(nextIn);
							for (int i = 0; i < more.Length; i++) appIn.Enqueue(more[i]);
							ack = (ushort)nextIn;
							nextIn = (nextIn + 1) & 0xFFFF;
						}
						ev.Set();
					} else if ((short)(theirSeq - (ushort)nextIn) > 0 && reorder.Count < 64) {
						reorder[theirSeq] = data;
					}
					SendPkt(2, SendId, seq, ack, null);
				}
			}
		}

		public void SendPkt(int type, int connId, ushort sq, ushort ak, byte[] payload) {
			int plen = payload == null ? 0 : payload.Length;
			byte[] pkt = new byte[20 + plen];
			pkt[0] = (byte)((type << 4) | 1);
			pkt[1] = 0;
			pkt[2] = (byte)((connId >> 8) & 0xFF);
			pkt[3] = (byte)(connId & 0xFF);
			uint ts = (uint)(Environment.TickCount * 1000);
			pkt[4] = (byte)(ts >> 24); pkt[5] = (byte)(ts >> 16); pkt[6] = (byte)(ts >> 8); pkt[7] = (byte)ts;
			pkt[12] = 0; pkt[13] = 0x40; pkt[14] = 0; pkt[15] = 0;
			pkt[16] = (byte)(sq >> 8); pkt[17] = (byte)sq;
			pkt[18] = (byte)(ak >> 8); pkt[19] = (byte)ak;
			if (plen > 0) Buffer.BlockCopy(payload, 0, pkt, 20, plen);
			hub.SendRaw(pkt, Remote);
		}

		public void Tick() {
			lock (gate) {
				DateTime now = DateTime.UtcNow;
				if (State == 3) return;
				if ((now - lastRecv).TotalSeconds > 30) { State = 3; ev.Set(); return; }
				for (int i = 0; i < inflight.Count; i++) {
					if ((now - infSent[i]).TotalMilliseconds > 600 && infSeq.Count > 0) {
						hub.SendRaw(inflight[i], Remote);
						infSent[i] = now;
					}
				}
			}
		}

		public bool ReadExact(byte[] buf, int off, int n, int timeoutMs) {
			DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			int got = 0;
			while (got < n) {
				Tick();
				lock (gate) {
					while (appIn.Count > 0 && got < n) buf[off + got++] = appIn.Dequeue();
					if (State == 3 && got < n && appIn.Count == 0) return false;
				}
				if (got >= n) return true;
				int left = (int)(dead - DateTime.UtcNow).TotalMilliseconds;
				if (left <= 0) return false;
				ev.WaitOne(Math.Min(200, left));
			}
			return true;
		}

		public void Write(byte[] buf, int n) {
			int o = 0;
			while (o < n && State == 2) {
				Tick();
				lock (gate) {
					if (inflight.Count >= 128) { }
					else {
						int take = Math.Min(Mss, n - o);
						byte[] pay = new byte[take];
						Buffer.BlockCopy(buf, o, pay, 0, take);
						byte[] pkt = new byte[20 + take];
						int connId = SendId;
						ushort sq = seq;
						seq++;
						pkt[0] = (byte)((0 << 4) | 1);
						pkt[2] = (byte)((connId >> 8) & 0xFF);
						pkt[3] = (byte)(connId & 0xFF);
						uint ts = (uint)(Environment.TickCount * 1000);
						pkt[4] = (byte)(ts >> 24); pkt[5] = (byte)(ts >> 16); pkt[6] = (byte)(ts >> 8); pkt[7] = (byte)ts;
						pkt[13] = 0x40;
						pkt[16] = (byte)(sq >> 8); pkt[17] = (byte)sq;
						pkt[18] = (byte)(ack >> 8); pkt[19] = (byte)ack;
						Buffer.BlockCopy(pay, 0, pkt, 20, take);
						inflight.Add(pkt); infSeq.Add(sq); infSent.Add(DateTime.UtcNow);
						hub.SendRaw(pkt, Remote);
						o += take;
						continue;
					}
				}
				ev.WaitOne(50);
			}
		}

		public bool PollRead(int microSeconds) {
			lock (gate) { if (appIn.Count > 0) return true; }
			Tick();
			ev.WaitOne(Math.Max(1, microSeconds / 1000));
			lock (gate) { return appIn.Count > 0 || State == 3; }
		}
		public int Avail { get { lock (gate) return appIn.Count; } }
		public void Close() {
			lock (gate) {
				if (State == 2) SendPkt(1, SendId, seq, ack, null);
				State = 3;
			}
			hub.Remove(this);
		}
	}

	internal sealed class UtpPeerIo : PeerIo {
		UtpConn c;
		byte[] push = new byte[0];
		int pushOff;
		public UtpPeerIo(UtpConn c) { this.c = c; Transport = "uTP"; }
		public void Unread(byte[] buf, int off, int n) {
			byte[] nb = new byte[n + (push.Length - pushOff)];
			Buffer.BlockCopy(buf, off, nb, 0, n);
			if (push.Length - pushOff > 0) Buffer.BlockCopy(push, pushOff, nb, n, push.Length - pushOff);
			push = nb; pushOff = 0;
		}
		public override bool ReadExact(byte[] buf, int off, int n, int timeoutMs) {
			int got = 0;
			while (got < n && pushOff < push.Length) buf[off + got++] = push[pushOff++];
			if (pushOff >= push.Length) { push = new byte[0]; pushOff = 0; }
			if (got >= n) return true;
			return c.ReadExact(buf, off + got, n - got, timeoutMs);
		}
		public override void Write(byte[] buf, int n) { c.Write(buf, n); }
		public override bool PollRead(int microSeconds) {
			if (pushOff < push.Length) return true;
			return c.PollRead(microSeconds);
		}
		public override int Available {
			get {
				int a = push.Length - pushOff;
				if (a < 0) a = 0;
				return a + c.Avail;
			}
		}
		public override void Close() { c.Close(); }
		public override IPEndPoint Remote { get { return c != null ? c.Remote : null; } }
	}

	internal sealed class UtpHub {
		Engine eng;
		UdpClient udp;
		VpnUdpSock vudp;
		Thread thr;
		volatile bool run;
		readonly object gate = new object();
		readonly Dictionary<string, UtpConn> map = new Dictionary<string, UtpConn>();
		public int Port;

		public UtpHub() { }
		public UtpHub(Engine e) { this.eng = e; }

		public bool Start(int port) {
			if (VpnHub.Blocked) return false;
			if (!VpnHub.TunnelOn && !VpnHub.OsAllowed) return false;
			try {
				if (VpnHub.TunnelOn) {
					vudp = VpnHub.BindUdp(port);
					Port = vudp.locPort;
				} else if (VpnHub.OsAllowed) {
					udp = new UdpClient(port);
					Port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
				} else return false;
				run = true;
				thr = new Thread(RecvLoop);
				thr.IsBackground = true;
				thr.Start();
				return true;
			} catch { udp = null; vudp = null; return false; }
		}
		public void Stop() {
			run = false;
			try { if (udp != null) udp.Close(); } catch { }
			try { if (vudp != null) VpnHub.DropUdp(vudp); } catch { }
			udp = null; vudp = null;
		}
		public void SendRaw(byte[] pkt, IPEndPoint ep) {
			try {
				if (vudp != null) vudp.SendTo(pkt, ep);
				else if (udp != null) udp.Send(pkt, pkt.Length, ep);
			} catch { }
		}
		public void Remove(UtpConn c) {
			lock (gate) {
				string k = Key(c.Remote, c.RecvId);
				map.Remove(k);
			}
		}
		static string Key(IPEndPoint ep, int recvId) {
			return ep.Address + ":" + ep.Port.ToString(CultureInfo.InvariantCulture) + ":" + recvId.ToString(CultureInfo.InvariantCulture);
		}
		public void Register(UtpConn c) {
			lock (gate) map[Key(c.Remote, c.RecvId)] = c;
		}
		public UtpConn Connect(string host, int port, int timeoutMs) {
			if (VpnHub.Blocked) return null;
			if (!VpnHub.TunnelOn && !VpnHub.OsAllowed) return null;
			UtpConn c = new UtpConn(this);
			if (!c.Connect(host, port, timeoutMs)) return null;
			return c;
		}
		void RecvLoop() {
			IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
			while (run) {
				try {
					byte[] pkt = null;
					IPEndPoint from = null;
					if (vudp != null) {
						if (!vudp.Recv(250, out pkt, out from)) continue;
					} else {
						if (udp == null) break;
						udp.Client.ReceiveTimeout = 250;
						pkt = udp.Receive(ref any);
						from = new IPEndPoint(any.Address, any.Port);
					}
					if (pkt == null || pkt.Length < 20) continue;
					if (pkt[0] == (byte)'d') {
						Dht.OnPacket(pkt, from);
						continue;
					}
					int connId = (pkt[2] << 8) | pkt[3];
					int type = (pkt[0] >> 4) & 0xF;
					UtpConn c = null;
					lock (gate) {
						foreach (KeyValuePair<string, UtpConn> kv in map) {
							if (kv.Value.Remote != null && kv.Value.Remote.Address.Equals(from.Address) && kv.Value.Remote.Port == from.Port && kv.Value.RecvId == connId) {
								c = kv.Value; break;
							}
						}
					}
					if (c == null && type == 4) {
						c = new UtpConn(this);
						c.Remote = from;
						c.RecvId = connId + 1;
						c.SendId = connId;
						ushort synSeq = (ushort)((pkt[16] << 8) | pkt[17]);
						lock (gate) map[Key(from, c.RecvId)] = c;
						c.AcceptSyn(synSeq);
						Session.DispatchUtp(c);
					} else if (c != null) c.OnPacket(pkt);
				} catch (SocketException) {
				} catch { if (!run) break; }
			}
		}
	}

	internal static class Blake2s {
		static readonly uint[] IV = new uint[] { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
		static readonly int[][] S = new int[][] {
			new int[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},
			new int[]{14,10,4,8,9,15,13,6,1,12,0,2,11,7,5,3},
			new int[]{11,8,12,0,5,2,15,13,10,14,3,6,7,1,9,4},
			new int[]{7,9,3,1,13,12,11,14,2,6,5,10,4,0,15,8},
			new int[]{9,0,5,7,2,4,10,15,14,1,11,12,6,8,3,13},
			new int[]{2,12,6,10,0,11,8,3,4,13,7,5,15,14,1,9},
			new int[]{12,5,1,15,14,13,4,10,0,7,6,3,9,2,8,11},
			new int[]{13,11,7,14,12,1,3,9,5,0,15,4,8,6,2,10},
			new int[]{6,15,14,9,11,3,0,8,12,2,13,7,1,4,10,5},
			new int[]{10,2,8,4,7,6,1,5,15,11,9,14,3,12,13,0}
		};
		static uint R(uint x, int n) { return (x >> n) | (x << (32 - n)); }
		static void G(uint[] v, int a, int b, int c, int d, uint x, uint y) {
			v[a] = v[a] + v[b] + x; v[d] = R(v[d] ^ v[a], 16);
			v[c] = v[c] + v[d]; v[b] = R(v[b] ^ v[c], 12);
			v[a] = v[a] + v[b] + y; v[d] = R(v[d] ^ v[a], 8);
			v[c] = v[c] + v[d]; v[b] = R(v[b] ^ v[c], 7);
		}
		static void Compress(uint[] h, byte[] block, ulong t, bool last) {
			uint[] v = new uint[16]; uint[] m = new uint[16];
			for (int i = 0; i < 8; i++) { v[i] = h[i]; v[i + 8] = IV[i]; }
			v[12] ^= (uint)t; v[13] ^= (uint)(t >> 32);
			if (last) v[14] = ~v[14];
			for (int i = 0; i < 16; i++) m[i] = BitConverter.ToUInt32(block, i * 4);
			for (int r = 0; r < 10; r++) {
				int[] s = S[r];
				G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]); G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
				G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]); G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
				G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]); G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
				G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]); G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
			}
			for (int i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
		}
		public static byte[] Hash(byte[] data, int outLen) { return HashKeyed(null, data, outLen); }
		public static byte[] HashKeyed(byte[] key, byte[] data, int outLen) {
			if (data == null) data = new byte[0];
			uint[] h = (uint[])IV.Clone();
			int klen = key == null ? 0 : key.Length;
			h[0] ^= 0x01010000U ^ ((uint)klen << 8) ^ (uint)outLen;
			byte[] block = new byte[64];
			ulong t = 0;
			int off = 0;
			if (klen > 0) {
				Buffer.BlockCopy(key, 0, block, 0, klen);
				t = 64; Compress(h, block, t, data.Length == 0); Array.Clear(block, 0, 64);
			}
			if (data.Length > 0 || klen == 0) {
				while (off + 64 < data.Length) {
					Buffer.BlockCopy(data, off, block, 0, 64); t += 64; Compress(h, block, t, false); off += 64;
				}
				Array.Clear(block, 0, 64);
				int left = data.Length - off;
				if (left > 0) Buffer.BlockCopy(data, off, block, 0, left);
				t += (ulong)left;
				Compress(h, block, t, true);
			}
			byte[] hs = new byte[32];
			for (int i = 0; i < 8; i++) Buffer.BlockCopy(BitConverter.GetBytes(h[i]), 0, hs, i * 4, 4);
			byte[] o = new byte[outLen]; Buffer.BlockCopy(hs, 0, o, 0, outLen); return o;
		}
		public static byte[] Hmac(byte[] key, byte[] data) {
			byte[] k = key.Length > 64 ? Hash(key, 32) : key;
			byte[] ip = new byte[64]; byte[] op = new byte[64];
			for (int i = 0; i < 64; i++) { ip[i] = 0x36; op[i] = 0x5c; }
			for (int i = 0; i < k.Length; i++) { ip[i] ^= k[i]; op[i] ^= k[i]; }
			byte[] inner = new byte[64 + data.Length];
			Buffer.BlockCopy(ip, 0, inner, 0, 64); Buffer.BlockCopy(data, 0, inner, 64, data.Length);
			byte[] ih = Hash(inner, 32);
			byte[] outer = new byte[96];
			Buffer.BlockCopy(op, 0, outer, 0, 64); Buffer.BlockCopy(ih, 0, outer, 64, 32);
			return Hash(outer, 32);
		}
		public static byte[] Mac16(byte[] key, byte[] data) { return HashKeyed(key, data, 16); }
	}

	internal static class ChaChaPoly {
		static readonly byte[] NoAd = new byte[0];
		static uint Rotl(uint x, int n) { return (x << n) | (x >> (32 - n)); }
		static uint Le32(byte[] b, int o) {
			return (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
		}
		static void St32(byte[] b, int o, uint v) {
			b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
		}
		static void Qr(uint[] s, int a, int b, int c, int d) {
			s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 16);
			s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 12);
			s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 8);
			s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 7);
		}
		static void Block(byte[] key, byte[] nonce, uint counter, uint[] s, uint[] w, byte[] out64) {
			s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
			s[4] = Le32(key, 0); s[5] = Le32(key, 4); s[6] = Le32(key, 8); s[7] = Le32(key, 12);
			s[8] = Le32(key, 16); s[9] = Le32(key, 20); s[10] = Le32(key, 24); s[11] = Le32(key, 28);
			s[12] = counter;
			s[13] = Le32(nonce, 0); s[14] = Le32(nonce, 4); s[15] = Le32(nonce, 8);
			for (int i = 0; i < 16; i++) w[i] = s[i];
			for (int r = 0; r < 10; r++) {
				Qr(w, 0, 4, 8, 12); Qr(w, 1, 5, 9, 13); Qr(w, 2, 6, 10, 14); Qr(w, 3, 7, 11, 15);
				Qr(w, 0, 5, 10, 15); Qr(w, 1, 6, 11, 12); Qr(w, 2, 7, 8, 13); Qr(w, 3, 4, 9, 14);
			}
			for (int i = 0; i < 16; i++) St32(out64, i * 4, w[i] + s[i]);
		}
		[ThreadStatic] static uint[] tsS;
		[ThreadStatic] static uint[] tsW;
		[ThreadStatic] static byte[] tsBlk;
		static uint[] S16() { uint[] a = tsS; if (a == null) { a = new uint[16]; tsS = a; } return a; }
		static uint[] W16() { uint[] a = tsW; if (a == null) { a = new uint[16]; tsW = a; } return a; }
		static byte[] B64() { byte[] a = tsBlk; if (a == null) { a = new byte[64]; tsBlk = a; } return a; }
		static void XorChaCha(byte[] key, byte[] nonce, uint ctr, byte[] src, int so, int n, byte[] dst, int dof) {
			uint[] st = S16(); uint[] w = W16(); byte[] blk = B64();
			int off = 0;
			while (off < n) {
				Block(key, nonce, ctr++, st, w, blk);
				int take = n - off; if (take > 64) take = 64;
				for (int i = 0; i < take; i++) dst[dof + off + i] = (byte)(src[so + off + i] ^ blk[i]);
				off += take;
			}
		}
		static void PolyBlocks(ref uint h0, ref uint h1, ref uint h2, ref uint h3, ref uint h4,
			uint r0, uint r1, uint r2, uint r3, uint r4, uint s1, uint s2, uint s3, uint s4,
			byte[] m, int off, int len) {
			uint hh0 = h0, hh1 = h1, hh2 = h2, hh3 = h3, hh4 = h4;
			int end = off + len;
			while (off + 16 <= end) {
				hh0 += Le32(m, off) & 0x3ffffff;
				hh1 += (Le32(m, off + 3) >> 2) & 0x3ffffff;
				hh2 += (Le32(m, off + 6) >> 4) & 0x3ffffff;
				hh3 += (Le32(m, off + 9) >> 6) & 0x3ffffff;
				hh4 += (Le32(m, off + 12) >> 8) | (1u << 24);
				ulong d0 = (ulong)hh0 * r0 + (ulong)hh1 * s4 + (ulong)hh2 * s3 + (ulong)hh3 * s2 + (ulong)hh4 * s1;
				ulong d1 = (ulong)hh0 * r1 + (ulong)hh1 * r0 + (ulong)hh2 * s4 + (ulong)hh3 * s3 + (ulong)hh4 * s2;
				ulong d2 = (ulong)hh0 * r2 + (ulong)hh1 * r1 + (ulong)hh2 * r0 + (ulong)hh3 * s4 + (ulong)hh4 * s3;
				ulong d3 = (ulong)hh0 * r3 + (ulong)hh1 * r2 + (ulong)hh2 * r1 + (ulong)hh3 * r0 + (ulong)hh4 * s4;
				ulong d4 = (ulong)hh0 * r4 + (ulong)hh1 * r3 + (ulong)hh2 * r2 + (ulong)hh3 * r1 + (ulong)hh4 * r0;
				uint c = (uint)(d0 >> 26); hh0 = (uint)d0 & 0x3ffffff; d1 += c;
				c = (uint)(d1 >> 26); hh1 = (uint)d1 & 0x3ffffff; d2 += c;
				c = (uint)(d2 >> 26); hh2 = (uint)d2 & 0x3ffffff; d3 += c;
				c = (uint)(d3 >> 26); hh3 = (uint)d3 & 0x3ffffff; d4 += c;
				c = (uint)(d4 >> 26); hh4 = (uint)d4 & 0x3ffffff;
				hh0 += c * 5; c = hh0 >> 26; hh0 &= 0x3ffffff; hh1 += c;
				off += 16;
			}
			h0 = hh0; h1 = hh1; h2 = hh2; h3 = hh3; h4 = hh4;
		}
		[ThreadStatic] static byte[] tsTail;
		[ThreadStatic] static byte[] tsLens;
		static byte[] Lens16() {
			byte[] a = tsLens;
			if (a == null) { a = new byte[16]; tsLens = a; }
			return a;
		}
		static byte[] Tail16() {
			byte[] a = tsTail;
			if (a == null) { a = new byte[16]; tsTail = a; }
			return a;
		}
		static void PolyMsg(ref uint h0, ref uint h1, ref uint h2, ref uint h3, ref uint h4,
			uint r0, uint r1, uint r2, uint r3, uint r4, uint s1, uint s2, uint s3, uint s4,
			byte[] m, int off, int len) {
			if (m == null || len <= 0) return;
			int full = len & ~15;
			if (full > 0) PolyBlocks(ref h0, ref h1, ref h2, ref h3, ref h4, r0, r1, r2, r3, r4, s1, s2, s3, s4, m, off, full);
			int left = len - full;
			if (left > 0) {
				byte[] p = Tail16();
				for (int i = 0; i < 16; i++) p[i] = 0;
				Buffer.BlockCopy(m, off + full, p, 0, left);
				PolyBlocks(ref h0, ref h1, ref h2, ref h3, ref h4, r0, r1, r2, r3, r4, s1, s2, s3, s4, p, 0, 16);
			}
		}
		static void Poly(byte[] otk, byte[] ad, byte[] ct, int ctOff, int ctLen, byte[] tag) {
			uint t0 = Le32(otk, 0), t1 = Le32(otk, 4), t2 = Le32(otk, 8), t3 = Le32(otk, 12);
			uint r0 = t0 & 0x3ffffff;
			uint r1 = ((t0 >> 26) | (t1 << 6)) & 0x3ffff03;
			uint r2 = ((t1 >> 20) | (t2 << 12)) & 0x3ffc0ff;
			uint r3 = ((t2 >> 14) | (t3 << 18)) & 0x3f03fff;
			uint r4 = (t3 >> 8) & 0x00fffff;
			uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
			uint p0 = Le32(otk, 16), p1 = Le32(otk, 20), p2 = Le32(otk, 24), p3 = Le32(otk, 28);
			uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;
			if (ad == null) ad = NoAd;
			PolyMsg(ref h0, ref h1, ref h2, ref h3, ref h4, r0, r1, r2, r3, r4, s1, s2, s3, s4, ad, 0, ad.Length);
			PolyMsg(ref h0, ref h1, ref h2, ref h3, ref h4, r0, r1, r2, r3, r4, s1, s2, s3, s4, ct, ctOff, ctLen);
			byte[] lens = Lens16();
			St32(lens, 0, (uint)ad.Length); St32(lens, 4, (uint)((ulong)ad.Length >> 32));
			St32(lens, 8, (uint)ctLen); St32(lens, 12, 0);
			PolyBlocks(ref h0, ref h1, ref h2, ref h3, ref h4, r0, r1, r2, r3, r4, s1, s2, s3, s4, lens, 0, 16);
			uint c = h1 >> 26; h1 &= 0x3ffffff;
			h2 += c; c = h2 >> 26; h2 &= 0x3ffffff;
			h3 += c; c = h3 >> 26; h3 &= 0x3ffffff;
			h4 += c; c = h4 >> 26; h4 &= 0x3ffffff;
			h0 += c * 5; c = h0 >> 26; h0 &= 0x3ffffff;
			h1 += c;
			uint g0 = h0 + 5; c = g0 >> 26; g0 &= 0x3ffffff;
			uint g1 = h1 + c; c = g1 >> 26; g1 &= 0x3ffffff;
			uint g2 = h2 + c; c = g2 >> 26; g2 &= 0x3ffffff;
			uint g3 = h3 + c; c = g3 >> 26; g3 &= 0x3ffffff;
			uint g4 = h4 + c - (1u << 26);
			uint mask = (g4 >> 31) - 1u;
			g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
			mask = ~mask;
			h0 = (h0 & mask) | g0; h1 = (h1 & mask) | g1; h2 = (h2 & mask) | g2; h3 = (h3 & mask) | g3; h4 = (h4 & mask) | g4;
			h0 = (h0 | (h1 << 26)) & 0xffffffff; h1 = ((h1 >> 6) | (h2 << 20)) & 0xffffffff;
			h2 = ((h2 >> 12) | (h3 << 14)) & 0xffffffff; h3 = ((h3 >> 18) | (h4 << 8)) & 0xffffffff;
			ulong f = (ulong)h0 + p0; h0 = (uint)f;
			f = (ulong)h1 + p1 + (f >> 32); h1 = (uint)f;
			f = (ulong)h2 + p2 + (f >> 32); h2 = (uint)f;
			f = (ulong)h3 + p3 + (f >> 32); h3 = (uint)f;
			St32(tag, 0, h0); St32(tag, 4, h1); St32(tag, 8, h2); St32(tag, 12, h3);
		}
		static byte[] Nonce(ulong counter) {
			byte[] n = new byte[12]; St32(n, 4, (uint)counter); St32(n, 8, (uint)(counter >> 32)); return n;
		}
		public static byte[] Seal(byte[] key, ulong counter, byte[] plain, byte[] ad) {
			return SealNonce(key, Nonce(counter), plain, ad);
		}
		public static byte[] SealNonce(byte[] key, byte[] nonce, byte[] plain, byte[] ad) {
			if (plain == null) plain = NoAd;
			if (ad == null) ad = NoAd;
			byte[] otk = new byte[64]; Block(key, nonce, 0, S16(), W16(), otk);
			byte[] ct = new byte[plain.Length + 16];
			if (plain.Length > 0) XorChaCha(key, nonce, 1, plain, 0, plain.Length, ct, 0);
			byte[] tag = new byte[16]; Poly(otk, ad, ct, 0, plain.Length, tag);
			Buffer.BlockCopy(tag, 0, ct, plain.Length, 16);
			return ct;
		}
		[ThreadStatic] static byte[] tsNonce;
		[ThreadStatic] static byte[] tsOtk;
		[ThreadStatic] static byte[] tsTag;
		static byte[] NonceBuf() { byte[] a = tsNonce; if (a == null) { a = new byte[12]; tsNonce = a; } return a; }
		static byte[] OtkBuf() { byte[] a = tsOtk; if (a == null) { a = new byte[64]; tsOtk = a; } return a; }
		static byte[] TagBuf() { byte[] a = tsTag; if (a == null) { a = new byte[16]; tsTag = a; } return a; }
		static void StoreCounter(byte[] nonce, ulong counter) {
			nonce[0] = 0; nonce[1] = 0; nonce[2] = 0; nonce[3] = 0;
			St32(nonce, 4, (uint)counter); St32(nonce, 8, (uint)(counter >> 32));
		}
		public static int SealInto(byte[] key, ulong counter, byte[] plain, int plainOff, int plainLen, byte[] dst, int dstOff) {
			byte[] nonce = NonceBuf();
			StoreCounter(nonce, counter);
			byte[] otk = OtkBuf();
			Block(key, nonce, 0, S16(), W16(), otk);
			if (plainLen > 0) XorChaCha(key, nonce, 1, plain, plainOff, plainLen, dst, dstOff);
			byte[] tag = TagBuf();
			Poly(otk, NoAd, dst, dstOff, plainLen, tag);
			Buffer.BlockCopy(tag, 0, dst, dstOff + plainLen, 16);
			return plainLen + 16;
		}
		public static bool OpenInto(byte[] key, ulong counter, byte[] cipher, int cOff, int cLen, byte[] dst, int dstOff) {
			if (cipher == null || cLen < 16) return false;
			int plen = cLen - 16;
			byte[] nonce = NonceBuf();
			StoreCounter(nonce, counter);
			byte[] otk = OtkBuf();
			Block(key, nonce, 0, S16(), W16(), otk);
			byte[] tag = TagBuf();
			Poly(otk, NoAd, cipher, cOff, plen, tag);
			int diff = 0;
			for (int i = 0; i < 16; i++) diff |= tag[i] ^ cipher[cOff + plen + i];
			if (diff != 0) return false;
			if (plen > 0) XorChaCha(key, nonce, 1, cipher, cOff, plen, dst, dstOff);
			return true;
		}
		public static byte[] Open(byte[] key, ulong counter, byte[] cipher, byte[] ad) {
			if (cipher == null || cipher.Length < 16) return null;
			if (ad == null) ad = NoAd;
			int plen = cipher.Length - 16;
			byte[] nonce = Nonce(counter);
			byte[] otk = new byte[64]; Block(key, nonce, 0, S16(), W16(), otk);
			byte[] tag = new byte[16]; Poly(otk, ad, cipher, 0, plen, tag);
			int diff = 0;
			for (int i = 0; i < 16; i++) diff |= tag[i] ^ cipher[plen + i];
			if (diff != 0) return null;
			byte[] plain = new byte[plen];
			if (plen > 0) XorChaCha(key, nonce, 1, cipher, 0, plen, plain, 0);
			return plain;
		}
	}

	internal static class X25519 {
		static readonly BigInteger P = (BigInteger.One << 255) - 19;
		static BigInteger Mod(BigInteger x) { x %= P; if (x.Sign < 0) x += P; return x; }
		static BigInteger Decode(byte[] b) {
			byte[] t = new byte[33]; Buffer.BlockCopy(b, 0, t, 0, 32); t[31] &= 127; return new BigInteger(t);
		}
		static byte[] Encode(BigInteger x) {
			x = Mod(x); byte[] t = x.ToByteArray(); byte[] o = new byte[32];
			Buffer.BlockCopy(t, 0, o, 0, t.Length > 32 ? 32 : t.Length); return o;
		}
		static void Cswap(ref BigInteger a, ref BigInteger b, int sw) {
			if (sw == 0) return; BigInteger t = a; a = b; b = t;
		}
		public static byte[] ScalarMult(byte[] n, byte[] p) {
			byte[] k = new byte[32]; Buffer.BlockCopy(n, 0, k, 0, 32);
			k[0] &= 248; k[31] &= 127; k[31] |= 64;
			BigInteger x1 = Decode(p), x2 = 1, z2 = 0, x3 = x1, z3 = 1;
			int swap = 0;
			for (int t = 254; t >= 0; t--) {
				int kt = (k[t >> 3] >> (t & 7)) & 1;
				swap ^= kt; Cswap(ref x2, ref x3, swap); Cswap(ref z2, ref z3, swap); swap = kt;
				BigInteger a = Mod(x2 + z2), aa = Mod(a * a), b = Mod(x2 - z2), bb = Mod(b * b);
				BigInteger e = Mod(aa - bb), c = Mod(x3 + z3), d = Mod(x3 - z3);
				BigInteger da = Mod(d * a), cb = Mod(c * b);
				x3 = Mod((da + cb) * (da + cb)); z3 = Mod(x1 * (da - cb) * (da - cb));
				x2 = Mod(aa * bb); z2 = Mod(e * (aa + Mod(121665 * e)));
			}
			Cswap(ref x2, ref x3, swap); Cswap(ref z2, ref z3, swap);
			return Encode(Mod(x2 * BigInteger.ModPow(z2, P - 2, P)));
		}
		public static byte[] PublicFromPrivate(byte[] priv) {
			byte[] nine = new byte[32]; nine[0] = 9; return ScalarMult(priv, nine);
		}
		public static byte[] RandomPrivate() {
			byte[] k = new byte[32]; new RNGCryptoServiceProvider().GetBytes(k);
			k[0] &= 248; k[31] &= 127; k[31] |= 64; return k;
		}
	}

	internal static class WgKdf {
		public static void Kdf1(byte[] ck, byte[] data, byte[] o1) {
			byte[] t = Blake2s.Hmac(ck, data); byte[] o = Blake2s.Hmac(t, new byte[] { 1 }); Buffer.BlockCopy(o, 0, o1, 0, 32);
		}
		public static void Kdf2(byte[] ck, byte[] data, byte[] o1, byte[] o2) {
			byte[] t = Blake2s.Hmac(ck, data);
			byte[] a = Blake2s.Hmac(t, new byte[] { 1 }); Buffer.BlockCopy(a, 0, o1, 0, 32);
			byte[] mix = new byte[33]; Buffer.BlockCopy(a, 0, mix, 0, 32); mix[32] = 2;
			byte[] b = Blake2s.Hmac(t, mix); Buffer.BlockCopy(b, 0, o2, 0, 32);
		}
		public static void Kdf3(byte[] ck, byte[] data, byte[] o1, byte[] o2, byte[] o3) {
			byte[] t = Blake2s.Hmac(ck, data);
			byte[] a = Blake2s.Hmac(t, new byte[] { 1 }); Buffer.BlockCopy(a, 0, o1, 0, 32);
			byte[] m2 = new byte[33]; Buffer.BlockCopy(a, 0, m2, 0, 32); m2[32] = 2;
			byte[] b = Blake2s.Hmac(t, m2); Buffer.BlockCopy(b, 0, o2, 0, 32);
			byte[] m3 = new byte[33]; Buffer.BlockCopy(b, 0, m3, 0, 32); m3[32] = 3;
			byte[] c = Blake2s.Hmac(t, m3); Buffer.BlockCopy(c, 0, o3, 0, 32);
		}
		public static byte[] HashCat(byte[] a, byte[] b) {
			byte[] c = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, c, 0, a.Length); Buffer.BlockCopy(b, 0, c, a.Length, b.Length);
			return Blake2s.Hash(c, 32);
		}
	}

	internal sealed class VpnSeg {
		internal byte[] data;
		internal uint seq;
		internal DateTime sent;
		internal int sends;
	}

	internal sealed class VpnTcpStream : Stream {
		readonly VpnTcp t;
		public VpnTcpStream(VpnTcp t) { this.t = t; }
		public override bool CanRead { get { return true; } }
		public override bool CanWrite { get { return true; } }
		public override bool CanSeek { get { return false; } }
		public override long Length { get { throw new NotSupportedException(); } }
		public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
		public override void Flush() { }
		public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
		public override void SetLength(long v) { throw new NotSupportedException(); }
		public override int Read(byte[] b, int o, int n) {
			while (true) {
				int got = t.Read(b, o, n, 60000);
				if (got > 0) return got;
				if (t.state == 0 || t.state == 4 || t.rst) return 0;
			}
		}
		public override void Write(byte[] b, int o, int n) { t.Write(b, o, n); }
		protected override void Dispose(bool d) { try { t.Close(); } catch { } base.Dispose(d); }
	}

	internal sealed class VpnTcp {
		internal const int Mss = 1280;
		internal const int IwSegs = 32;
		internal const int CwndMax = 32 * 1024 * 1024;
		internal const int WScale = 8;
		internal uint remIp, locIp; internal ushort remPort, locPort;
		internal int state;
		internal uint sndNxt, sndUna, rcvNxt, iss, finSeq;
		internal bool finSeen;
		internal readonly Queue<byte[]> rxq = new Queue<byte[]>();
		internal int rxOff, rxAvail, dupAcks;
		internal uint peerWnd = 65535;
		internal int sndWScale;
		internal int cwnd = Mss * IwSegs;
		internal int ssthresh = CwndMax;
		internal int sndMss = Mss;
		internal int srttMs, rttvarMs, rtoMs = 250, rtoCur = 250;
		internal bool fastRec, lossHeld;
		internal uint recoverAt, lossUna;
		internal int lastRawWnd = -1;
		internal VpnSeg rexmit;
		internal bool wantPump;
		internal readonly Dictionary<uint, byte[]> ooo = new Dictionary<uint, byte[]>();
		internal readonly Queue<byte[]> pending = new Queue<byte[]>();
		internal readonly List<VpnSeg> flight = new List<VpnSeg>();
		internal readonly AutoResetEvent ev = new AutoResetEvent(false);
		internal readonly object gate = new object();
		internal DateTime lastTx = DateTime.UtcNow;
		internal bool rst;
		public int Available { get { lock (gate) return rxAvail; } }
		public bool Poll(int micro) {
			if (Available > 0) return true;
			ev.WaitOne(Math.Max(1, micro / 1000));
			return Available > 0 || state == 0 || state == 4 || rst;
		}
		public int Read(byte[] b, int o, int n, int timeoutMs) {
			DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			int got = 0;
			while (got < n) {
				lock (gate) {
					while (got < n && rxq.Count > 0) {
						byte[] c = rxq.Peek();
						int ntake = n - got; int cleft = c.Length - rxOff;
						if (ntake > cleft) ntake = cleft;
						Buffer.BlockCopy(c, rxOff, b, o + got, ntake);
						rxOff += ntake; rxAvail -= ntake; got += ntake;
						if (rxOff >= c.Length) { rxq.Dequeue(); rxOff = 0; }
					}
				}
				if (got > 0) return got;
				if (state == 0 || state == 4 || rst) return got;
				int left = (int)(dead - DateTime.UtcNow).TotalMilliseconds;
				if (left <= 0) return got;
				ev.WaitOne(Math.Min(200, left));
			}
			return got;
		}
		int QueuedBytes() {
			int n = 0;
			foreach (byte[] p in pending) n += p.Length;
			for (int i = 0; i < flight.Count; i++) n += flight[i].data.Length;
			return n;
		}
		internal void NoteRtt(int sampleMs) {
			if (sampleMs < 1) sampleMs = 1;
			if (sampleMs > 10000) sampleMs = 10000;
			if (srttMs == 0) {
				srttMs = sampleMs;
				rttvarMs = sampleMs / 2;
			} else {
				int delta = sampleMs - srttMs;
				if (delta < 0) delta = -delta;
				rttvarMs = (3 * rttvarMs + delta) / 4;
				srttMs = (7 * srttMs + sampleMs) / 8;
			}
			int r = srttMs + 4 * rttvarMs;
			if (r < 50) r = 50;
			if (r > 3000) r = 3000;
			rtoMs = r;
			rtoCur = r;
		}
		internal void OnNewAck(int acked) {
			if (acked < 1) return;
			if (cwnd < ssthresh) {
				int add = acked;
				if (add > 2 * Mss) add = 2 * Mss;
				cwnd += add;
			} else {
				int inc = (Mss * Mss) / cwnd;
				if (inc < 1) inc = 1;
				cwnd += inc;
			}
			if (cwnd > CwndMax) cwnd = CwndMax;
			if (cwnd < Mss) cwnd = Mss;
		}
		internal int FlightCap() {
			int cap = cwnd;
			if (cap > CwndMax) cap = CwndMax;
			if (cap < Mss) cap = Mss;
			if (peerWnd > 16384 && peerWnd < (uint)cap) cap = (int)peerWnd;
			return cap;
		}
		internal void NotePeerMss(int peerMss) {
			int ours = VpnHub.SegMss();
			int m = peerMss;
			if (m < 536) m = 536;
			if (m > ours) m = ours;
			if (m > 1440) m = 1440;
			sndMss = m;
		}
		internal void EnterFastRecovery(int flightBytes, uint sndNxtNow) {
			fastRec = true;
			recoverAt = sndNxtNow;
			int half = flightBytes / 2;
			if (half < 2 * Mss) half = 2 * Mss;
			ssthresh = half;
			if (ssthresh > CwndMax) ssthresh = CwndMax;
			cwnd = ssthresh + 3 * Mss;
			if (cwnd > CwndMax) cwnd = CwndMax;
			dupAcks = 0;
		}
		internal void CutOnTimeout(int flightBytes) {
			int half = flightBytes / 2;
			if (half < 2 * Mss) half = 2 * Mss;
			ssthresh = half;
			if (ssthresh > CwndMax) ssthresh = CwndMax;
			cwnd = Mss;
			fastRec = false;
			dupAcks = 0;
		}
		internal void OnCumulativeAck(uint ack, bool countDup) {
			rexmit = null;
			wantPump = false;
			uint unaBefore = sndUna;
			int rttSample = -1;
			while (flight.Count > 0) {
				VpnSeg sg = flight[0];
				uint end = sg.seq + (uint)sg.data.Length;
				if (ack >= end) {
					if (sg.sends == 1) {
						int ms = (int)(DateTime.UtcNow - sg.sent).TotalMilliseconds;
						if (ms < 0) ms = 0;
						rttSample = ms;
					}
					flight.RemoveAt(0);
					sndUna = end;
				} else break;
			}
			if (sndUna != unaBefore) {
				if (rttSample >= 0) NoteRtt(rttSample);
				ev.Set();
				int acked = (int)(sndUna - unaBefore);
				rtoCur = rtoMs;
				lossHeld = false;
				if (fastRec) {
					if (ack >= recoverAt) {
						fastRec = false;
						dupAcks = 0;
						cwnd = ssthresh;
						if (cwnd < Mss) cwnd = Mss;
						if (cwnd > CwndMax) cwnd = CwndMax;
					} else {
						dupAcks = 0;
						cwnd = ssthresh;
						if (cwnd < Mss) cwnd = Mss;
						if (flight.Count > 0) {
							flight[0].sent = DateTime.UtcNow;
							flight[0].sends++;
							rexmit = flight[0];
						}
					}
				} else {
					dupAcks = 0;
					OnNewAck(acked);
				}
				wantPump = true;
				return;
			}
			if (countDup && flight.Count > 0 && ack == sndUna) {
				dupAcks++;
				if (!fastRec && dupAcks >= 3) {
					int fb = 0;
					for (int i = 0; i < flight.Count; i++) fb += flight[i].data.Length;
					EnterFastRecovery(fb, sndNxt);
					flight[0].sent = DateTime.UtcNow;
					flight[0].sends++;
					rexmit = flight[0];
				} else if (fastRec) {
					cwnd += Mss;
					if (cwnd > CwndMax) cwnd = CwndMax;
					wantPump = true;
				}
			}
		}
		internal bool OnRetransmitTimeout(DateTime now) {
			rexmit = null;
			if (state != 3 || flight.Count == 0) return false;
			if ((now - flight[0].sent).TotalMilliseconds < rtoCur) return false;
			if (!lossHeld || lossUna != sndUna) {
				int fb = 0;
				for (int i = 0; i < flight.Count; i++) fb += flight[i].data.Length;
				CutOnTimeout(fb);
				recoverAt = sndNxt;
				lossHeld = true;
				lossUna = sndUna;
			}
			int nb = rtoCur * 2;
			if (nb < 50) nb = 50;
			if (nb > 3000) nb = 3000;
			rtoCur = nb;
			flight[0].sent = now;
			flight[0].sends++;
			rexmit = flight[0];
			return true;
		}
		public void Write(byte[] b, int o, int n) {
			if (n <= 0 || state != 3) return;
			int mss = sndMss;
			if (mss < 536) mss = 536;
			if (mss > 1440) mss = 1440;
			int off = 0;
			lock (gate) {
				while (off < n) {
					int take = n - off; if (take > mss) take = mss;
					byte[] seg = new byte[take];
					Buffer.BlockCopy(b, o + off, seg, 0, take);
					pending.Enqueue(seg);
					off += take;
				}
			}
			VpnHub.TcpPump(this);
			DateTime dead = DateTime.UtcNow.AddMilliseconds(30000);
			while (DateTime.UtcNow < dead) {
				lock (gate) {
					if (rst || state != 3) return;
					if (QueuedBytes() < 2097152) return;
				}
				ev.WaitOne(40);
			}
		}
		public void Close() {
			if (state == 3) {
				try { VpnHub.TcpSend(this, 0x11, null, sndNxt); } catch { }
			}
			state = 0; ev.Set(); VpnHub.DropTcp(this);
		}
	}

	internal sealed class VpnUdpSock {
		internal ushort locPort;
		internal readonly Queue<byte[]> rx = new Queue<byte[]>();
		internal readonly Queue<IPEndPoint> from = new Queue<IPEndPoint>();
		internal readonly AutoResetEvent ev = new AutoResetEvent(false);
		internal readonly object gate = new object();
		public void SendTo(byte[] data, IPEndPoint ep) { VpnHub.UdpSend(this, data, ep); }
		public bool Recv(int timeoutMs, out byte[] data, out IPEndPoint ep) {
			data = null; ep = null;
			DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (true) {
				lock (gate) {
					if (rx.Count > 0) { data = rx.Dequeue(); ep = from.Dequeue(); return true; }
				}
				int left = (int)(dead - DateTime.UtcNow).TotalMilliseconds;
				if (left <= 0) return false;
				ev.WaitOne(Math.Min(200, left));
			}
		}
	}

	internal static class Tls1Prf {
		static byte[] PHash(HMAC hmac, byte[] seed, int n) {
			byte[] a = seed;
			byte[] o = new byte[n];
			int off = 0;
			while (off < n) {
				a = hmac.ComputeHash(a);
				byte[] inp = new byte[a.Length + seed.Length];
				Buffer.BlockCopy(a, 0, inp, 0, a.Length);
				Buffer.BlockCopy(seed, 0, inp, a.Length, seed.Length);
				byte[] b = hmac.ComputeHash(inp);
				int take = n - off; if (take > b.Length) take = b.Length;
				Buffer.BlockCopy(b, 0, o, off, take);
				off += take;
			}
			return o;
		}
		public static byte[] Prf(byte[] secret, string label, byte[] seed, int n) {
			byte[] lab = Encoding.ASCII.GetBytes(label);
			byte[] ls = new byte[lab.Length + seed.Length];
			Buffer.BlockCopy(lab, 0, ls, 0, lab.Length);
			Buffer.BlockCopy(seed, 0, ls, lab.Length, seed.Length);
			int half = (secret.Length + 1) / 2;
			byte[] s1 = new byte[half]; byte[] s2 = new byte[half];
			Buffer.BlockCopy(secret, 0, s1, 0, half);
			Buffer.BlockCopy(secret, secret.Length - half, s2, 0, half);
			byte[] p1, p2;
			using (HMACMD5 m = new HMACMD5(s1)) p1 = PHash(m, ls, n);
			using (HMACSHA1 s = new HMACSHA1(s2)) p2 = PHash(s, ls, n);
			byte[] o = new byte[n];
			for (int i = 0; i < n; i++) o[i] = (byte)(p1[i] ^ p2[i]);
			return o;
		}
	}

	internal static class AesGcmPt {
		static void GfMul(byte[] x, byte[] y) {
			byte[] z = new byte[16];
			byte[] v = new byte[16];
			Buffer.BlockCopy(y, 0, v, 0, 16);
			for (int i = 0; i < 16; i++) {
				for (int j = 7; j >= 0; j--) {
					if ((x[i] & (1 << j)) != 0) {
						for (int k = 0; k < 16; k++) z[k] ^= v[k];
					}
					bool lsb = (v[15] & 1) != 0;
					for (int k = 15; k > 0; k--) v[k] = (byte)((v[k] >> 1) | (v[k - 1] << 7));
					v[0] >>= 1;
					if (lsb) v[0] ^= 0xE1;
				}
			}
			Buffer.BlockCopy(z, 0, x, 0, 16);
		}
		static void Xor16(byte[] a, byte[] b, int bo) {
			for (int i = 0; i < 16; i++) a[i] ^= b[bo + i];
		}
		static ICryptoTransform AesEnc(byte[] key, out Aes aes) {
			aes = Aes.Create();
			aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.KeySize = 256; aes.Key = key;
			return aes.CreateEncryptor();
		}
		static void AesEcb(ICryptoTransform e, byte[] block) {
			e.TransformBlock(block, 0, 16, block, 0);
		}
		static void Ctr(ICryptoTransform enc, byte[] nonce, uint start, byte[] src, int so, int n, byte[] dst, int dof) {
			byte[] ctr = new byte[16];
			Buffer.BlockCopy(nonce, 0, ctr, 0, 12);
			byte[] ks = new byte[16];
			int off = 0; uint c = start;
			while (off < n) {
				ctr[12] = (byte)(c >> 24); ctr[13] = (byte)(c >> 16); ctr[14] = (byte)(c >> 8); ctr[15] = (byte)c;
				Buffer.BlockCopy(ctr, 0, ks, 0, 16);
				AesEcb(enc, ks);
				int take = n - off; if (take > 16) take = 16;
				for (int i = 0; i < take; i++) dst[dof + off + i] = (byte)(src[so + off + i] ^ ks[i]);
				off += take; c++;
			}
		}
		static void Ghash(byte[] H, byte[] ad, byte[] ct, int ctLen, byte[] out16) {
			byte[] y = new byte[16];
			int i = 0;
			if (ad != null) {
				while (i + 16 <= ad.Length) { Xor16(y, ad, i); GfMul(y, H); i += 16; }
				if (i < ad.Length) {
					byte[] p = new byte[16]; Buffer.BlockCopy(ad, i, p, 0, ad.Length - i);
					Xor16(y, p, 0); GfMul(y, H);
				}
			}
			i = 0;
			while (i + 16 <= ctLen) { Xor16(y, ct, i); GfMul(y, H); i += 16; }
			if (i < ctLen) {
				byte[] p = new byte[16]; Buffer.BlockCopy(ct, i, p, 0, ctLen - i);
				Xor16(y, p, 0); GfMul(y, H);
			}
			byte[] len = new byte[16];
			ulong al = (ulong)(ad == null ? 0 : ad.Length) * 8UL;
			ulong cl = (ulong)ctLen * 8UL;
			for (int k = 0; k < 8; k++) { len[7 - k] = (byte)al; al >>= 8; len[15 - k] = (byte)cl; cl >>= 8; }
			Xor16(y, len, 0); GfMul(y, H);
			Buffer.BlockCopy(y, 0, out16, 0, 16);
		}
		[StructLayout(LayoutKind.Sequential)]
		struct GcmAuth {
			public uint cbSize;
			public uint dwInfoVersion;
			public IntPtr pbNonce;
			public uint cbNonce;
			public IntPtr pbAuthData;
			public uint cbAuthData;
			public IntPtr pbTag;
			public uint cbTag;
			public IntPtr pbMacContext;
			public uint cbMacContext;
			public uint cbAAD;
			public ulong cbData;
			public uint dwFlags;
		}
		sealed class GcmSlot {
			public byte[] key;
			public byte[] obj;
			public GCHandle pin;
			public IntPtr hKey;
		}
		static readonly object gcmGate = new object();
		static readonly byte[] gcmDummy = new byte[1];
		static IntPtr gcmAlg;
		static int gcmObjLen;
		static GcmSlot gcmA, gcmB;
		static int gcmUse;
		public static int CngHits;
		[DllImport("bcrypt.dll")]
		static extern uint BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, [MarshalAs(UnmanagedType.LPWStr)] string pszAlgId, [MarshalAs(UnmanagedType.LPWStr)] string pszImplementation, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptSetProperty(IntPtr hObject, [MarshalAs(UnmanagedType.LPWStr)] string pszProperty, byte[] pbInput, int cbInput, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptGetProperty(IntPtr hObject, [MarshalAs(UnmanagedType.LPWStr)] string pszProperty, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, byte[] pbKeyObject, int cbKeyObject, byte[] pbSecret, int cbSecret, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptDestroyKey(IntPtr hKey);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptEncrypt(IntPtr hKey, byte[] pbInput, int cbInput, ref GcmAuth pInfo, IntPtr pbIV, int cbIV, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);
		[DllImport("bcrypt.dll")]
		static extern uint BCryptDecrypt(IntPtr hKey, byte[] pbInput, int cbInput, ref GcmAuth pInfo, IntPtr pbIV, int cbIV, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);
		static bool GcmReady() {
			if (gcmAlg != IntPtr.Zero) return gcmObjLen > 0;
			IntPtr h;
			uint st = BCryptOpenAlgorithmProvider(out h, "AES", "Microsoft Primitive Provider", 0);
			if (st != 0) st = BCryptOpenAlgorithmProvider(out h, "AES", null, 0);
			if (st != 0) return false;
			byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
			st = BCryptSetProperty(h, "ChainingMode", mode, mode.Length, 0);
			if (st != 0) { BCryptCloseAlgorithmProvider(h, 0); return false; }
			byte[] lenBuf = new byte[4];
			int got;
			st = BCryptGetProperty(h, "ObjectLength", lenBuf, 4, out got, 0);
			if (st != 0 || got < 4) { BCryptCloseAlgorithmProvider(h, 0); return false; }
			int objLen = BitConverter.ToInt32(lenBuf, 0);
			if (objLen < 16) { BCryptCloseAlgorithmProvider(h, 0); return false; }
			gcmAlg = h; gcmObjLen = objLen;
			return true;
		}
		static bool SameKey(GcmSlot s, byte[] key) {
			if (s == null || s.key == null || key == null || s.key.Length != key.Length) return false;
			int d = 0;
			for (int i = 0; i < key.Length; i++) d |= s.key[i] ^ key[i];
			return d == 0;
		}
		static void FreeSlot(GcmSlot s) {
			if (s == null) return;
			if (s.hKey != IntPtr.Zero) { try { BCryptDestroyKey(s.hKey); } catch { } s.hKey = IntPtr.Zero; }
			if (s.pin.IsAllocated) s.pin.Free();
		}
		static GcmSlot ImportGcm(byte[] key) {
			byte[] obj = new byte[gcmObjLen];
			GCHandle pin = GCHandle.Alloc(obj, GCHandleType.Pinned);
			IntPtr hk;
			uint st = BCryptGenerateSymmetricKey(gcmAlg, out hk, obj, obj.Length, key, key.Length, 0);
			if (st != 0 || hk == IntPtr.Zero) { pin.Free(); return null; }
			GcmSlot s = new GcmSlot();
			s.key = new byte[key.Length];
			Buffer.BlockCopy(key, 0, s.key, 0, key.Length);
			s.obj = obj; s.pin = pin; s.hKey = hk;
			return s;
		}
		static GcmSlot SlotFor(byte[] key) {
			if (key == null || key.Length == 0 || !GcmReady()) return null;
			if (SameKey(gcmA, key)) { gcmUse = 0; return gcmA; }
			if (SameKey(gcmB, key)) { gcmUse = 1; return gcmB; }
			GcmSlot neu = ImportGcm(key);
			if (neu == null) return null;
			if (gcmA == null) { gcmA = neu; gcmUse = 0; return neu; }
			if (gcmB == null) { gcmB = neu; gcmUse = 1; return neu; }
			if (gcmUse == 0) { FreeSlot(gcmB); gcmB = neu; gcmUse = 1; }
			else { FreeSlot(gcmA); gcmA = neu; gcmUse = 0; }
			return neu;
		}
		static GcmAuth AuthInfo(IntPtr nonce, int nlen, IntPtr ad, int adlen, IntPtr tag) {
			GcmAuth info = new GcmAuth();
			info.cbSize = (uint)Marshal.SizeOf(typeof(GcmAuth));
			info.dwInfoVersion = 1;
			info.pbNonce = nonce;
			info.cbNonce = (uint)nlen;
			info.pbAuthData = ad;
			info.cbAuthData = (uint)adlen;
			info.pbTag = tag;
			info.cbTag = 16;
			return info;
		}
		static byte[] SealGcm(byte[] key, byte[] nonce, byte[] ad, byte[] pt, int ptOff, int ptLen) {
			if (key == null || nonce == null || nonce.Length != 12 || ptLen < 0) return null;
			if (pt == null) { pt = gcmDummy; ptOff = 0; ptLen = 0; }
			if (ad != null && ad.Length == 0) ad = null;
			if (ptOff != 0 && ptLen > 0) {
				byte[] slice = new byte[ptLen];
				Buffer.BlockCopy(pt, ptOff, slice, 0, ptLen);
				pt = slice; ptOff = 0;
			}
			lock (gcmGate) {
				GcmSlot slot = SlotFor(key);
				if (slot == null) return null;
				byte[] tag = new byte[16];
				byte[] ct = ptLen > 0 ? new byte[ptLen] : gcmDummy;
				GCHandle hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
				GCHandle ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
				GCHandle ha = default(GCHandle);
				if (ad != null) ha = GCHandle.Alloc(ad, GCHandleType.Pinned);
				try {
					GcmAuth info = AuthInfo(hn.AddrOfPinnedObject(), nonce.Length, ad != null ? ha.AddrOfPinnedObject() : IntPtr.Zero, ad != null ? ad.Length : 0, ht.AddrOfPinnedObject());
					int written;
					byte[] input = ptLen > 0 ? pt : gcmDummy;
					uint st = BCryptEncrypt(slot.hKey, input, ptLen, ref info, IntPtr.Zero, 0, ct, ptLen, out written, 0);
					if (st != 0) return null;
					if (ptLen > 0 && written != ptLen) return null;
					byte[] o = new byte[ptLen + 16];
					if (ptLen > 0) Buffer.BlockCopy(ct, 0, o, 0, ptLen);
					Buffer.BlockCopy(tag, 0, o, ptLen, 16);
					CngHits++;
					return o;
				} finally {
					hn.Free(); ht.Free();
					if (ha.IsAllocated) ha.Free();
				}
			}
		}
		static byte[] OpenGcm(byte[] key, byte[] nonce, byte[] ad, byte[] ctTag) {
			if (key == null || nonce == null || nonce.Length != 12 || ctTag == null || ctTag.Length < 16) return null;
			if (ad != null && ad.Length == 0) ad = null;
			int plen = ctTag.Length - 16;
			lock (gcmGate) {
				GcmSlot slot = SlotFor(key);
				if (slot == null) return null;
				byte[] ct = plen > 0 ? new byte[plen] : gcmDummy;
				if (plen > 0) Buffer.BlockCopy(ctTag, 0, ct, 0, plen);
				byte[] tag = new byte[16];
				Buffer.BlockCopy(ctTag, plen, tag, 0, 16);
				byte[] pt = plen > 0 ? new byte[plen] : gcmDummy;
				GCHandle hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
				GCHandle ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
				GCHandle ha = default(GCHandle);
				if (ad != null) ha = GCHandle.Alloc(ad, GCHandleType.Pinned);
				try {
					GcmAuth info = AuthInfo(hn.AddrOfPinnedObject(), nonce.Length, ad != null ? ha.AddrOfPinnedObject() : IntPtr.Zero, ad != null ? ad.Length : 0, ht.AddrOfPinnedObject());
					int written;
					uint st = BCryptDecrypt(slot.hKey, ct, plen, ref info, IntPtr.Zero, 0, pt, plen, out written, 0);
					if (st != 0) return null;
					if (plen > 0 && written != plen) return null;
					CngHits++;
					if (plen == 0) return new byte[0];
					return pt;
				} finally {
					hn.Free(); ht.Free();
					if (ha.IsAllocated) ha.Free();
				}
			}
		}
		public static byte[] Seal(byte[] key, byte[] nonce12, byte[] ad, byte[] pt) {
			if (pt == null) pt = new byte[0];
			return Seal(key, nonce12, ad, pt, 0, pt.Length);
		}
		public static byte[] Seal(byte[] key, byte[] nonce12, byte[] ad, byte[] pt, int ptOff, int ptLen) {
			byte[] fast = SealGcm(key, nonce12, ad, pt, ptOff, ptLen);
			if (fast != null) return fast;
			return SealManaged(key, nonce12, ad, pt, ptOff, ptLen);
		}
		static byte[] SealManaged(byte[] key, byte[] nonce12, byte[] ad, byte[] pt, int ptOff, int ptLen) {
			if (pt == null) { pt = new byte[0]; ptOff = 0; ptLen = 0; }
			if (ad == null) ad = new byte[0];
			if (ptLen < 0) ptLen = 0;
			Aes aes; ICryptoTransform enc = AesEnc(key, out aes);
			try {
				byte[] H = new byte[16]; AesEcb(enc, H);
				byte[] ct = new byte[ptLen + 16];
				if (ptLen > 0) Ctr(enc, nonce12, 2, pt, ptOff, ptLen, ct, 0);
				byte[] s = new byte[16]; Ghash(H, ad, ct, ptLen, s);
				byte[] t0 = new byte[16]; Ctr(enc, nonce12, 1, s, 0, 16, t0, 0);
				Buffer.BlockCopy(t0, 0, ct, ptLen, 16);
				return ct;
			} finally { enc.Dispose(); aes.Dispose(); }
		}
		public static byte[] Open(byte[] key, byte[] nonce12, byte[] ad, byte[] ctTag) {
			byte[] fast = OpenGcm(key, nonce12, ad, ctTag);
			if (fast != null) return fast;
			return OpenManaged(key, nonce12, ad, ctTag);
		}
		static byte[] OpenManaged(byte[] key, byte[] nonce12, byte[] ad, byte[] ctTag) {
			if (ctTag == null || ctTag.Length < 16) return null;
			if (ad == null) ad = new byte[0];
			int plen = ctTag.Length - 16;
			Aes aes; ICryptoTransform enc = AesEnc(key, out aes);
			try {
				byte[] H = new byte[16]; AesEcb(enc, H);
				byte[] s = new byte[16]; Ghash(H, ad, ctTag, plen, s);
				byte[] t0 = new byte[16]; Ctr(enc, nonce12, 1, s, 0, 16, t0, 0);
				int diff = 0;
				for (int i = 0; i < 16; i++) diff |= t0[i] ^ ctTag[plen + i];
				if (diff != 0) return null;
				byte[] pt = new byte[plen];
				if (plen > 0) Ctr(enc, nonce12, 2, ctTag, 0, plen, pt, 0);
				return pt;
			} finally { enc.Dispose(); aes.Dispose(); }
		}
		public static byte[] CtrCrypt(byte[] key, byte[] iv16, byte[] pt) {
			byte[] nonce = new byte[12]; Buffer.BlockCopy(iv16, 0, nonce, 0, 12);
			uint start = ((uint)iv16[12] << 24) | ((uint)iv16[13] << 16) | ((uint)iv16[14] << 8) | iv16[15];
			byte[] o = new byte[pt.Length];
			Aes aes; ICryptoTransform enc = AesEnc(key, out aes);
			try { Ctr(enc, nonce, start, pt, 0, pt.Length, o, 0); return o; }
			finally { enc.Dispose(); aes.Dispose(); }
		}
	}

	internal static class Pem {
		public static byte[] Block(string text, string name) {
			string a = "-----BEGIN " + name + "-----";
			string b = "-----END " + name + "-----";
			int i = text.IndexOf(a, StringComparison.Ordinal);
			if (i < 0) return null;
			int j = text.IndexOf(b, i, StringComparison.Ordinal);
			if (j < 0) return null;
			string body = text.Substring(i + a.Length, j - (i + a.Length)).Replace("\r", "").Replace("\n", "").Replace(" ", "");
			try { return Convert.FromBase64String(body); } catch { return null; }
		}
		public static string Tag(string text, string name) {
			string a = "<" + name + ">";
			string b = "</" + name + ">";
			int i = text.IndexOf(a, StringComparison.OrdinalIgnoreCase);
			if (i < 0) return null;
			int j = text.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
			if (j < 0) return null;
			return text.Substring(i + a.Length, j - (i + a.Length));
		}
		public static byte[] HexKey(string text) {
			string a = "-----BEGIN OpenVPN Static key V1-----";
			string b = "-----END OpenVPN Static key V1-----";
			int i = text.IndexOf(a, StringComparison.Ordinal);
			if (i < 0) return null;
			int j = text.IndexOf(b, i, StringComparison.Ordinal);
			if (j < 0) return null;
			string body = text.Substring(i + a.Length, j - (i + a.Length));
			StringBuilder sb = new StringBuilder();
			for (int k = 0; k < body.Length; k++) {
				char c = body[k];
				if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) sb.Append(c);
			}
			if ((sb.Length & 1) != 0) return null;
			byte[] o = new byte[sb.Length / 2];
			for (int k = 0; k < o.Length; k++)
				o[k] = byte.Parse(sb.ToString(k * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			return o;
		}
		static int DerLen(byte[] d, ref int i) {
			int l = d[i++];
			if ((l & 0x80) == 0) return l;
			int n = l & 0x7F; int v = 0;
			for (int k = 0; k < n; k++) v = (v << 8) | d[i++];
			return v;
		}
		static byte[] DerInt(byte[] d, ref int i) {
			if (d[i++] != 0x02) throw new InvalidDataException("der int");
			int n = DerLen(d, ref i);
			while (n > 1 && d[i] == 0) { i++; n--; }
			byte[] v = new byte[n + 1];
			Buffer.BlockCopy(d, i, v, 1, n);
			i += n;
			return v;
		}
		static RSAParameters RsaPkcs1(byte[] der) {
			int i = 0;
			if (der[i++] != 0x30) throw new InvalidDataException("seq");
			DerLen(der, ref i);
			DerInt(der, ref i);
			RSAParameters p = new RSAParameters();
			p.Modulus = Skip0(DerInt(der, ref i));
			p.Exponent = Skip0(DerInt(der, ref i));
			p.D = Skip0(DerInt(der, ref i));
			p.P = Skip0(DerInt(der, ref i));
			p.Q = Skip0(DerInt(der, ref i));
			p.DP = Skip0(DerInt(der, ref i));
			p.DQ = Skip0(DerInt(der, ref i));
			p.InverseQ = Skip0(DerInt(der, ref i));
			return p;
		}
		static byte[] Skip0(byte[] v) {
			if (v.Length > 1 && v[0] == 0) {
				byte[] o = new byte[v.Length - 1]; Buffer.BlockCopy(v, 1, o, 0, o.Length); return o;
			}
			return v;
		}
		public static RSACryptoServiceProvider RsaFromPem(string text) {
			byte[] der = Block(text, "RSA PRIVATE KEY");
			if (der == null) {
				byte[] pkcs8 = Block(text, "PRIVATE KEY");
				if (pkcs8 == null) return null;
				int i = 0;
				if (pkcs8[i++] != 0x30) return null;
				DerLen(pkcs8, ref i);
				DerInt(pkcs8, ref i);
				if (pkcs8[i++] != 0x30) return null;
				int sl = DerLen(pkcs8, ref i); i += sl;
				if (pkcs8[i++] != 0x04) return null;
				int ol = DerLen(pkcs8, ref i);
				der = new byte[ol]; Buffer.BlockCopy(pkcs8, i, der, 0, ol);
			}
			RSAParameters p = RsaPkcs1(der);
			CspParameters cp = new CspParameters();
			cp.KeyContainerName = Guid.NewGuid().ToString("N");
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(cp);
			rsa.PersistKeyInCsp = false;
			rsa.ImportParameters(p);
			return rsa;
		}
		public static X509Certificate2 CertFromPem(string text) {
			byte[] der = Block(text, "CERTIFICATE");
			if (der == null) return null;
			return new X509Certificate2(der);
		}
		public static X509Certificate2 CertWithKey(byte[] der, RSACryptoServiceProvider rsa) {
			if (der == null || rsa == null) return null;
			X509Certificate2 c = new X509Certificate2(der);
			try { c.PrivateKey = rsa; } catch { }
			try {
				byte[] pfx = c.Export(X509ContentType.Pkcs12, "pt");
				return new X509Certificate2(pfx, "pt", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
			} catch { return c; }
		}
	}

	internal sealed class OvpnCtl : Stream {
		internal readonly object gate = new object();
		internal readonly Queue<byte> rx = new Queue<byte>();
		internal readonly AutoResetEvent ev = new AutoResetEvent(false);
		internal volatile bool dead;
		public override bool CanRead { get { return true; } }
		public override bool CanWrite { get { return true; } }
		public override bool CanSeek { get { return false; } }
		public override long Length { get { throw new NotSupportedException(); } }
		public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
		public override void Flush() { }
		public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
		public override void SetLength(long v) { throw new NotSupportedException(); }
		public Action<byte[]> OnWrite;
		public override int Read(byte[] b, int o, int n) {
			DateTime deadAt = DateTime.UtcNow.AddSeconds(60);
			int got = 0;
			while (got < n) {
				lock (gate) {
					while (got < n && rx.Count > 0) b[o + got++] = rx.Dequeue();
				}
				if (got > 0) return got;
				if (dead) return 0;
				int left = (int)(deadAt - DateTime.UtcNow).TotalMilliseconds;
				if (left <= 0) return 0;
				ev.WaitOne(Math.Min(200, left));
			}
			return got;
		}
		public override void Write(byte[] b, int o, int n) {
			byte[] p = new byte[n]; Buffer.BlockCopy(b, o, p, 0, n);
			Action<byte[]> w = OnWrite;
			if (w != null) w(p);
		}
		public void Push(byte[] d, int off, int n) {
			lock (gate) { for (int i = 0; i < n; i++) rx.Enqueue(d[off + i]); }
			ev.Set();
		}
	}

	internal sealed class OvpnSess {
		public string RemoteHost;
		public int RemotePort = 1194;
		public IPEndPoint Endpoint;
		public byte[] TlsCryptKey;
		public string CipherName = "AES-256-GCM";
		public int TunMtu = 1500;
		public string User = "", Pass = "";
		static readonly byte[] PingMagic = new byte[] {
			0x2A, 0x18, 0x7B, 0xF3, 0x64, 0x1E, 0xB4, 0xCB, 0x07, 0xED, 0x2C, 0x0E, 0x7A, 0x16, 0x4F, 0x08
		};
		byte[] locSid = new byte[8], remSid = new byte[8];
		byte[] encKe, encKa, decKe, decKa;
		uint wrapPid = 1;
		uint msgPid = 1;
		readonly Dictionary<uint, byte[]> sendTls = new Dictionary<uint, byte[]>();
		readonly Dictionary<uint, DateTime> sendAt = new Dictionary<uint, DateTime>();
		readonly Dictionary<uint, int> sendOp = new Dictionary<uint, int>();
		readonly List<uint> pendingAck = new List<uint>();
		uint nextIn;
		readonly Dictionary<uint, byte[]> ooo = new Dictionary<uint, byte[]>();
		readonly OvpnCtl ctl = new OvpnCtl();
		readonly object gate = new object();
		byte[] sendCk, sendIv, recvCk, recvIv;
		uint dataPid = 1;
		int peerId = 0xFFFFFF;
		volatile bool keysUp;
		SslStream ssl;
		X509Certificate2 caCert, clCert;
		byte[] kmClient, kmServer;

		public static string PickCipher(string cipher, string dataCiphers) {
			if (!string.IsNullOrEmpty(dataCiphers)) {
				if (dataCiphers.IndexOf("AES-256-GCM", StringComparison.OrdinalIgnoreCase) >= 0) return "AES-256-GCM";
				return null;
			}
			if (string.IsNullOrEmpty(cipher)) return "AES-256-GCM";
			return cipher.ToUpperInvariant();
		}
		public static bool TryParse(string text, out OvpnSess s, out string err) {
			s = null; err = "";
			if (string.IsNullOrEmpty(text)) { err = "empty config"; return false; }
			if (text.IndexOf("tls-crypt-v2", StringComparison.OrdinalIgnoreCase) >= 0) {
				err = "OpenVPN tls-crypt-v2 is not supported.";
				return false;
			}
			bool looks = text.IndexOf("remote ", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("remote\t", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("<tls-crypt>", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("\nclient", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!looks) { err = "not an OpenVPN config"; return false; }
			OvpnSess o = new OvpnSess();
			string proto = "udp";
			string host = null; int port = 1194;
			string dataCiphers = null;
			string cipherLine = null;
			string[] lines = text.Replace("\r", "").Split('\n');
			for (int i = 0; i < lines.Length; i++) {
				string ln = lines[i].Trim();
				if (ln.Length == 0 || ln[0] == '#' || ln[0] == ';' || ln[0] == '<') continue;
				string[] p = ln.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (p.Length == 0) continue;
				string k = p[0].ToLowerInvariant();
				if (k == "remote" && p.Length >= 2) {
					if (host == null) {
						host = p[1];
						if (p.Length >= 3) int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
						if (p.Length >= 4) proto = p[3].ToLowerInvariant();
					}
				} else if (k == "proto" && p.Length >= 2) proto = p[1].ToLowerInvariant();
				else if (k == "cipher" && p.Length >= 2) cipherLine = p[1];
				else if ((k == "data-ciphers" || k == "ncp-ciphers") && p.Length >= 2) dataCiphers = ln.Substring(p[0].Length).Trim();
				else if (k == "tun-mtu" && p.Length >= 2) int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out o.TunMtu);
				else if (k == "port" && p.Length >= 2 && host == null) int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
			}
			string picked = PickCipher(cipherLine, dataCiphers);
			if (picked == null) {
				err = "OpenVPN data-ciphers does not include AES-256-GCM.";
				return false;
			}
			o.CipherName = picked;
			string creds = Pem.Tag(text, "auth-user-pass");
			if (!string.IsNullOrEmpty(creds)) {
				string[] ul = creds.Replace("\r", "").Split('\n');
				List<string> got = new List<string>();
				for (int i = 0; i < ul.Length; i++) {
					string u = ul[i].Trim();
					if (u.Length > 0) got.Add(u);
				}
				if (got.Count >= 1) o.User = got[0];
				if (got.Count >= 2) o.Pass = got[1];
			}
			if (string.IsNullOrEmpty(host)) { err = "OpenVPN config missing remote"; return false; }
			if (proto.IndexOf("tcp", StringComparison.Ordinal) >= 0) {
				err = "OpenVPN TCP is not supported. Use UDP.";
				return false;
			}
			if (proto.IndexOf("udp6", StringComparison.Ordinal) >= 0) {
				err = "OpenVPN IPv6 is not supported.";
				return false;
			}
			string cryptBlock = Pem.Tag(text, "tls-crypt");
			if (cryptBlock == null && text.IndexOf("tls-crypt", StringComparison.OrdinalIgnoreCase) < 0) {
				err = "OpenVPN config needs tls-crypt.";
				return false;
			}
			o.TlsCryptKey = Pem.HexKey(cryptBlock != null ? cryptBlock : text);
			if (o.TlsCryptKey == null || o.TlsCryptKey.Length < 256) {
				err = "OpenVPN config needs a tls-crypt key.";
				return false;
			}
			string caPem = Pem.Tag(text, "ca");
			string certPem = Pem.Tag(text, "cert");
			string keyPem = Pem.Tag(text, "key");
			try { o.caCert = Pem.CertFromPem(caPem != null ? caPem : text); } catch { }
			if (o.caCert == null) { err = "OpenVPN config missing <ca>"; return false; }
			RSACryptoServiceProvider rsa = null;
			try { rsa = Pem.RsaFromPem(keyPem != null ? keyPem : text); } catch (Exception ex) { err = "client key: " + ex.Message; return false; }
			byte[] certDer = certPem != null ? Pem.Block(certPem, "CERTIFICATE") : null;
			if (certDer == null) certDer = ExtractNthCertDer(text, 1);
			try { o.clCert = Pem.CertWithKey(certDer, rsa); } catch (Exception ex) { err = "client cert: " + ex.Message; return false; }
			if (o.clCert == null || rsa == null) { err = "OpenVPN config missing <cert> and <key>"; return false; }
			o.RemoteHost = host;
			o.RemotePort = port > 0 ? port : 1194;
			s = o; return true;
		}
		static byte[] ExtractNthCertDer(string text, int skip) {
			int from = 0; int seen = 0;
			while (true) {
				int i = text.IndexOf("-----BEGIN CERTIFICATE-----", from, StringComparison.Ordinal);
				if (i < 0) return null;
				int j = text.IndexOf("-----END CERTIFICATE-----", i, StringComparison.Ordinal);
				if (j < 0) return null;
				j += "-----END CERTIFICATE-----".Length;
				if (seen == skip) return Pem.Block(text.Substring(i, j - i), "CERTIFICATE");
				seen++; from = j;
			}
		}
		public bool Resolve(out string err) {
			err = "";
			IPAddress ip = null;
			if (!IPAddress.TryParse(RemoteHost, out ip)) {
				try {
					IPAddress[] addrs = Dns.GetHostAddresses(RemoteHost);
					for (int i = 0; i < addrs.Length; i++)
						if (addrs[i].AddressFamily == AddressFamily.InterNetwork) { ip = addrs[i]; break; }
				} catch (Exception ex) { err = "OpenVPN remote DNS failed: " + ex.Message; return false; }
			}
			if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) {
				err = "OpenVPN remote DNS failed: " + RemoteHost; return false;
			}
			Endpoint = new IPEndPoint(ip, RemotePort);
			return true;
		}

		void KeysFromStatic() {
			encKe = new byte[32]; encKa = new byte[32]; decKe = new byte[32]; decKa = new byte[32];
			Buffer.BlockCopy(TlsCryptKey, 128, encKe, 0, 32);
			Buffer.BlockCopy(TlsCryptKey, 192, encKa, 0, 32);
			Buffer.BlockCopy(TlsCryptKey, 0, decKe, 0, 32);
			Buffer.BlockCopy(TlsCryptKey, 64, decKa, 0, 32);
		}
		static void Be32(byte[] d, int o, uint v) {
			d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
		}
		static uint Rb32(byte[] d, int o) {
			return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
		}
		byte[] Wrap(byte opcode, byte[] plain) {
			byte[] hdr = new byte[17];
			hdr[0] = opcode;
			Buffer.BlockCopy(locSid, 0, hdr, 1, 8);
			uint pid; uint ts;
			lock (gate) { pid = wrapPid++; ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
			Be32(hdr, 9, pid); Be32(hdr, 13, ts);
			byte[] macIn = new byte[17 + plain.Length];
			Buffer.BlockCopy(hdr, 0, macIn, 0, 17);
			Buffer.BlockCopy(plain, 0, macIn, 17, plain.Length);
			byte[] tag;
			using (HMACSHA256 h = new HMACSHA256(encKa)) tag = h.ComputeHash(macIn);
			byte[] iv = new byte[16]; Buffer.BlockCopy(tag, 0, iv, 0, 16);
			byte[] ct = AesGcmPt.CtrCrypt(encKe, iv, plain);
			byte[] o = new byte[17 + 32 + ct.Length];
			Buffer.BlockCopy(hdr, 0, o, 0, 17);
			Buffer.BlockCopy(tag, 0, o, 17, 32);
			Buffer.BlockCopy(ct, 0, o, 49, ct.Length);
			return o;
		}
		byte[] Unwrap(byte[] pkt) {
			if (pkt == null || pkt.Length < 49) return null;
			byte[] ct = new byte[pkt.Length - 49];
			Buffer.BlockCopy(pkt, 49, ct, 0, ct.Length);
			byte[] iv = new byte[16]; Buffer.BlockCopy(pkt, 17, iv, 0, 16);
			byte[] plain = AesGcmPt.CtrCrypt(decKe, iv, ct);
			byte[] macIn = new byte[17 + plain.Length];
			Buffer.BlockCopy(pkt, 0, macIn, 0, 17);
			Buffer.BlockCopy(plain, 0, macIn, 17, plain.Length);
			byte[] want;
			using (HMACSHA256 h = new HMACSHA256(decKa)) want = h.ComputeHash(macIn);
			int diff = 0;
			for (int i = 0; i < 32; i++) diff |= want[i] ^ pkt[17 + i];
			if (diff != 0) return null;
			return plain;
		}
		byte[] CtrlPlain(int opcode, uint thisPid, byte[] tls) {
			MemoryStream ms = new MemoryStream();
			lock (gate) {
				int n = pendingAck.Count; if (n > 4) n = 4;
				ms.WriteByte((byte)n);
				for (int i = 0; i < n; i++) {
					byte[] b = new byte[4]; Be32(b, 0, pendingAck[i]); ms.Write(b, 0, 4);
				}
				if (n > 0) ms.Write(remSid, 0, 8);
				if (n > 0) pendingAck.RemoveRange(0, n);
			}
			if (opcode != 5) {
				byte[] pid = new byte[4]; Be32(pid, 0, thisPid); ms.Write(pid, 0, 4);
				if (tls != null && tls.Length > 0) ms.Write(tls, 0, tls.Length);
			}
			return ms.ToArray();
		}
		void SendCtrl(int opcode, uint thisPid, byte[] tls) {
			byte op = (byte)(opcode << 3);
			byte[] plain = CtrlPlain(opcode, thisPid, tls);
			byte[] wire = Wrap(op, plain);
			if (opcode != 5) {
				lock (gate) {
					sendTls[thisPid] = tls == null ? new byte[0] : tls;
					sendAt[thisPid] = DateTime.UtcNow;
					sendOp[thisPid] = opcode;
				}
			}
			VpnHub.UdpSendRaw(wire);
		}
		void SendTls(byte[] tls) {
			if (tls == null || tls.Length == 0) return;
			int off = 0;
			while (off < tls.Length) {
				int n = tls.Length - off; if (n > 1150) n = 1150;
				byte[] chunk = new byte[n];
				Buffer.BlockCopy(tls, off, chunk, 0, n);
				uint pid;
				lock (gate) { pid = msgPid++; }
				SendCtrl(4, pid, chunk);
				off += n;
			}
		}
		public bool StartHs() {
			KeysFromStatic();
			new RNGCryptoServiceProvider().GetBytes(locSid);
			ctl.OnWrite = SendTls;
			SendCtrl(7, 0, null);
			Thread t = new Thread(Hs); t.IsBackground = true; t.Name = "pt-ovpn-hs"; t.Start();
			return true;
		}
		bool WaitSid(int ms) {
			DateTime d = DateTime.UtcNow.AddMilliseconds(ms);
			while (DateTime.UtcNow < d) {
				lock (gate) {
					bool ok = false;
					for (int i = 0; i < 8; i++) if (remSid[i] != 0) { ok = true; break; }
					if (ok) return true;
				}
				if (ctl.dead) return false;
				Thread.Sleep(50);
			}
			return false;
		}
		void Hs() {
			try {
				if (!WaitSid(15000)) { VpnHub.LastError = "OpenVPN no server reset"; return; }
				X509CertificateCollection cc = new X509CertificateCollection();
				if (clCert != null) cc.Add(clCert);
				ssl = new SslStream(ctl, false, Validate);
				ssl.AuthenticateAsClient(RemoteHost, cc, SslProtocols.Tls12, false);
				byte[] km = BuildKm2();
				ssl.Write(km, 0, km.Length);
				byte[] their = ReadKm2();
				if (their == null) { VpnHub.LastError = "OpenVPN no server keys"; return; }
				Derive();
				byte[] push = Encoding.ASCII.GetBytes("PUSH_REQUEST");
				byte[] pr = new byte[push.Length + 1]; Buffer.BlockCopy(push, 0, pr, 0, push.Length);
				ssl.Write(pr, 0, pr.Length);
				if (!ReadPush()) {
					if (string.IsNullOrEmpty(VpnHub.LastError)) VpnHub.LastError = "OpenVPN no PUSH_REPLY";
					return;
				}
				if (CipherName.IndexOf("AES-256-GCM", StringComparison.OrdinalIgnoreCase) < 0) {
					VpnHub.LastError = "OpenVPN data cipher " + CipherName + " is not supported.";
					return;
				}
				keysUp = true;
				VpnHub.NoteOvpnUp();
			} catch (Exception ex) { VpnHub.LastError = "OpenVPN TLS: " + ex.Message; ctl.dead = true; }
		}
		bool Validate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors e) {
			if (caCert == null || cert == null) return false;
			try {
				X509Certificate2 c2 = cert as X509Certificate2;
				if (c2 == null) c2 = new X509Certificate2(cert);
				X509Chain ch = new X509Chain();
				ch.ChainPolicy.ExtraStore.Add(caCert);
				ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
				ch.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
				ch.Build(c2);
				for (int i = 0; i < ch.ChainElements.Count; i++) {
					if (string.Equals(ch.ChainElements[i].Certificate.Thumbprint, caCert.Thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
				}
				return string.Equals(c2.Issuer, caCert.Subject, StringComparison.Ordinal);
			} catch { return false; }
		}
		byte[] BuildKm2() {
			byte[] src = new byte[112];
			new RNGCryptoServiceProvider().GetBytes(src);
			lock (gate) { kmClient = src; }
			string opt = "V4,dev-type tun,link-mtu 1569,tun-mtu 1500,proto UDPv4,cipher AES-256-GCM,auth SHA512,keysize 256,key-method 2,tls-client";
			string pi = "IV_VER=2.6.12\nIV_PLAT=win\nIV_NCP=2\nIV_TCPNL=1\nIV_PROTO=22\nIV_CIPHERS=AES-256-GCM\nIV_LZO_STUB=1\nIV_COMP_STUB=1\n";
			MemoryStream ms = new MemoryStream();
			ms.Write(new byte[4], 0, 4);
			ms.WriteByte(2);
			ms.Write(src, 0, 112);
			WriteOvStr(ms, opt);
			WriteOvStr(ms, User);
			WriteOvStr(ms, Pass);
			WriteOvStr(ms, pi);
			return ms.ToArray();
		}
		static void WriteOvStr(MemoryStream ms, string s) {
			if (s == null) s = "";
			byte[] b = Encoding.ASCII.GetBytes(s);
			byte[] n = new byte[2]; n[0] = (byte)((b.Length + 1) >> 8); n[1] = (byte)(b.Length + 1);
			ms.Write(n, 0, 2); ms.Write(b, 0, b.Length); ms.WriteByte(0);
		}
		byte[] ReadExactSsl(int n) {
			byte[] b = new byte[n]; int g = 0;
			DateTime d = DateTime.UtcNow.AddSeconds(25);
			while (g < n) {
				if (DateTime.UtcNow > d) return null;
				int r = ssl.Read(b, g, n - g);
				if (r <= 0) return null;
				g += r;
			}
			return b;
		}
		byte[] ReadKm2() {
			byte[] hdr = ReadExactSsl(5);
			if (hdr == null || hdr[4] != 2) return null;
			byte[] src = ReadExactSsl(64);
			if (src == null) return null;
			kmServer = src;
			SkipOvStr(); SkipOvStr(); SkipOvStr(); SkipOvStr();
			return src;
		}
		void SkipOvStr() {
			byte[] n = ReadExactSsl(2); if (n == null) return;
			int len = (n[0] << 8) | n[1];
			if (len > 0 && len < 4096) ReadExactSsl(len);
		}
		bool ReadPush() {
			DateTime d = DateTime.UtcNow.AddSeconds(20);
			MemoryStream ms = new MemoryStream();
			byte[] buf = new byte[2048];
			while (DateTime.UtcNow < d) {
				int r;
				try { r = ssl.Read(buf, 0, buf.Length); } catch { return false; }
				if (r <= 0) return false;
				ms.Write(buf, 0, r);
				byte[] all = ms.ToArray();
				int start = 0;
				for (int i = 0; i < all.Length; i++) {
					if (all[i] != 0) continue;
					string cmd = Encoding.ASCII.GetString(all, start, i - start);
					start = i + 1;
					if (cmd.StartsWith("PUSH_REPLY", StringComparison.Ordinal)) {
						ApplyPush(cmd);
						return true;
					}
					if (cmd.StartsWith("AUTH_FAILED", StringComparison.Ordinal)) {
						VpnHub.LastError = cmd; return false;
					}
				}
			}
			return false;
		}
		void ApplyPush(string cmd) {
			string[] parts = cmd.Split(',');
			for (int i = 0; i < parts.Length; i++) {
				string p = parts[i].Trim();
				if (p.StartsWith("ifconfig ", StringComparison.Ordinal)) {
					string[] a = p.Split(' ');
					if (a.Length >= 2) {
						try { VpnHub.SetTunnelIp(IPAddress.Parse(a[1])); } catch { }
					}
				} else if (p.StartsWith("dhcp-option DNS ", StringComparison.OrdinalIgnoreCase) || p.StartsWith("dhcp-option dns ", StringComparison.OrdinalIgnoreCase)) {
					string[] a = p.Split(' ');
					if (a.Length >= 3) { try { VpnHub.SetDnsIp(IPAddress.Parse(a[2])); } catch { } }
				} else if (p.StartsWith("peer-id ", StringComparison.Ordinal)) {
					int.TryParse(p.Substring(8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out peerId);
				} else if (p.StartsWith("cipher ", StringComparison.Ordinal)) {
					CipherName = p.Substring(7).Trim().ToUpperInvariant();
				} else if (p.StartsWith("ping ", StringComparison.Ordinal)) {
					int ka; if (int.TryParse(p.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ka) && ka > 0)
						VpnHub.SetKeepalive(ka);
				}
			}
		}
		void Derive() {
			byte[] pre = new byte[48]; Buffer.BlockCopy(kmClient, 0, pre, 0, 48);
			byte[] cr1 = new byte[32]; Buffer.BlockCopy(kmClient, 48, cr1, 0, 32);
			byte[] cr2 = new byte[32]; Buffer.BlockCopy(kmClient, 80, cr2, 0, 32);
			byte[] sr1 = new byte[32]; Buffer.BlockCopy(kmServer, 0, sr1, 0, 32);
			byte[] sr2 = new byte[32]; Buffer.BlockCopy(kmServer, 32, sr2, 0, 32);
			byte[] seed1 = Cat(cr1, sr1);
			byte[] master = Tls1Prf.Prf(pre, "OpenVPN master secret", seed1, 48);
			byte[] seed2 = new byte[32 + 32 + 8 + 8];
			Buffer.BlockCopy(cr2, 0, seed2, 0, 32);
			Buffer.BlockCopy(sr2, 0, seed2, 32, 32);
			Buffer.BlockCopy(locSid, 0, seed2, 64, 8);
			Buffer.BlockCopy(remSid, 0, seed2, 72, 8);
			byte[] kb = Tls1Prf.Prf(master, "OpenVPN key expansion", seed2, 256);
			sendCk = new byte[32]; recvCk = new byte[32]; sendIv = new byte[8]; recvIv = new byte[8];
			Buffer.BlockCopy(kb, 0, sendCk, 0, 32);
			Buffer.BlockCopy(kb, 64, sendIv, 0, 8);
			Buffer.BlockCopy(kb, 128, recvCk, 0, 32);
			Buffer.BlockCopy(kb, 192, recvIv, 0, 8);
		}
		static byte[] Cat(byte[] a, byte[] b) {
			byte[] c = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, c, 0, a.Length); Buffer.BlockCopy(b, 0, c, a.Length, b.Length); return c;
		}
		public void OnPacket(byte[] pkt) {
			if (pkt == null || pkt.Length < 1) return;
			int op = pkt[0] >> 3;
			if (op == 6 || op == 9) { OnData(pkt, op); return; }
			byte[] plain = Unwrap(pkt);
			if (plain == null) return;
			VpnHub.NoteRx();
			lock (gate) { Buffer.BlockCopy(pkt, 1, remSid, 0, 8); }
			int o = 0;
			if (o >= plain.Length) return;
			int nack = plain[o++];
			for (int i = 0; i < nack; i++) {
				if (o + 4 > plain.Length) return;
				uint aid = Rb32(plain, o); o += 4;
				lock (gate) { sendTls.Remove(aid); sendAt.Remove(aid); sendOp.Remove(aid); }
			}
			if (nack > 0) {
				if (o + 8 > plain.Length) return;
				o += 8;
			}
			if (op == 5) return;
			if (o + 4 > plain.Length) return;
			uint pid = Rb32(plain, o); o += 4;
			lock (gate) { pendingAck.Add(pid); }
			byte[] tls = null;
			if (o < plain.Length) {
				tls = new byte[plain.Length - o];
				Buffer.BlockCopy(plain, o, tls, 0, tls.Length);
			}
			lock (gate) {
				if (pid == nextIn) {
					if (tls != null && tls.Length > 0) ctl.Push(tls, 0, tls.Length);
					nextIn++;
					byte[] more;
					while (ooo.TryGetValue(nextIn, out more)) {
						ooo.Remove(nextIn);
						if (more != null && more.Length > 0) ctl.Push(more, 0, more.Length);
						nextIn++;
					}
				} else if (pid > nextIn && ooo.Count < 32 && !ooo.ContainsKey(pid)) ooo[pid] = tls;
			}
			SendCtrl(5, 0, null);
		}
		void OnData(byte[] pkt, int op) {
			if (!keysUp || recvCk == null) return;
			int hdr = op == 9 ? 4 : 1;
			if (pkt.Length < hdr + 4 + 16) return;
			uint pid = Rb32(pkt, hdr);
			byte[] nonce = new byte[12];
			Be32(nonce, 0, pid);
			Buffer.BlockCopy(recvIv, 0, nonce, 4, 8);
			byte[] ad = new byte[hdr + 4];
			Buffer.BlockCopy(pkt, 0, ad, 0, hdr + 4);
			int tagOff = hdr + 4;
			int ctLen = pkt.Length - tagOff - 16;
			if (ctLen < 0) return;
			byte[] ctTag = new byte[ctLen + 16];
			Buffer.BlockCopy(pkt, tagOff + 16, ctTag, 0, ctLen);
			Buffer.BlockCopy(pkt, tagOff, ctTag, ctLen, 16);
			byte[] inner = AesGcmPt.Open(recvCk, nonce, ad, ctTag);
			if (inner == null) return;
			VpnHub.NoteRx();
			if (inner.Length == 16) {
				int same = 0;
				for (int i = 0; i < 16; i++) if (inner[i] == PingMagic[i]) same++;
				if (same == 16) return;
			}
			VpnHub.OnInnerIp(inner);
		}
		public void SendInner(byte[] inner) {
			SendInner(inner, inner == null ? 0 : inner.Length);
		}
		public void SendInner(byte[] inner, int len) {
			if (!keysUp || sendCk == null || inner == null || len <= 0) return;
			uint pid;
			lock (gate) { pid = dataPid++; }
			byte[] nonce = new byte[12];
			Be32(nonce, 0, pid);
			Buffer.BlockCopy(sendIv, 0, nonce, 4, 8);
			byte[] hdr = new byte[4];
			hdr[0] = (byte)(9 << 3);
			hdr[1] = (byte)((peerId >> 16) & 255);
			hdr[2] = (byte)((peerId >> 8) & 255);
			hdr[3] = (byte)(peerId & 255);
			byte[] ad = new byte[8];
			Buffer.BlockCopy(hdr, 0, ad, 0, 4);
			Be32(ad, 4, pid);
			byte[] sealedB = AesGcmPt.Seal(sendCk, nonce, ad, inner, 0, len);
			byte[] pkt = new byte[8 + sealedB.Length];
			Buffer.BlockCopy(hdr, 0, pkt, 0, 4);
			Be32(pkt, 4, pid);
			Buffer.BlockCopy(sealedB, sealedB.Length - 16, pkt, 8, 16);
			Buffer.BlockCopy(sealedB, 0, pkt, 24, sealedB.Length - 16);
			VpnHub.UdpSendRaw(pkt);
		}
		public void SendPing() {
			if (!keysUp) return;
			SendInner(PingMagic);
		}
		public void Tick() {
			DateTime now = DateTime.UtcNow;
			List<uint> retry = new List<uint>();
			lock (gate) {
				foreach (KeyValuePair<uint, DateTime> kv in sendAt) {
					if ((now - kv.Value).TotalMilliseconds >= 1000) retry.Add(kv.Key);
				}
			}
			for (int i = 0; i < retry.Count; i++) {
				uint id = retry[i];
				int opcode; byte[] tls;
				lock (gate) {
					if (!sendTls.ContainsKey(id) || !sendOp.ContainsKey(id)) continue;
					opcode = sendOp[id]; tls = sendTls[id];
				}
				SendCtrl(opcode, id, tls);
			}
			bool needAck;
			lock (gate) needAck = pendingAck.Count > 0;
			if (needAck) SendCtrl(5, 0, null);
		}
		public void Close() {
			ctl.dead = true;
			try { ctl.ev.Set(); } catch { }
			try { if (ssl != null) ssl.Close(); } catch { }
		}
	}

	public static class VpnHub {
		public static volatile bool Require;
		public static string LastError = "";
		public static string Status = "off";
		public static string ProvenIp = "";
		public static bool ProbeDone;
		public static bool DroppedWhileUp;
		public static bool HandshakeTimedOut;
		public static string DirectIp = "";
		public static bool ProbeFailed;
		public static string EndpointHost {
			get { return endpoint == null ? "" : endpoint.ToString(); }
		}
		static int vpnKind;
		static string ovpnRaw;
		static OvpnSess ovpn;
		static byte[] priv, peerPub, psk;
		static uint localIp, dnsIp;
		static IPEndPoint endpoint;
		static int keepalive = 15, mtu = 1320;
		static UdpClient udp;
		static Thread thr;
		static volatile bool run, up;
		static byte[] sendKey, recvKey;
		static uint localIndex, remoteIndex, hsLocalIndex;
		static ulong sendCtr, recvCtr;
		static DateTime lastHs = DateTime.MinValue, lastHsOk = DateTime.MinValue, lastData = DateTime.MinValue, lastRecv = DateTime.MinValue, connectAt = DateTime.MinValue;
		static bool handshakeWarnSent;
		static byte[] hsCk, hsHash, ephPriv, ephPub, staticPub;
		static readonly object gate = new object();
		static readonly object udpGate = new object();
		static readonly byte[] NoPay = new byte[0];
		[ThreadStatic] static byte[] tsIp;
		[ThreadStatic] static byte[] tsWg;
		[ThreadStatic] static byte[] tsPlain;
		static readonly Dictionary<string, VpnTcp> tcps = new Dictionary<string, VpnTcp>();
		static readonly Dictionary<int, VpnUdpSock> udps = new Dictionary<int, VpnUdpSock>();
		static readonly Queue<VpnTcp> accepts = new Queue<VpnTcp>();
		static readonly AutoResetEvent acceptEv = new AutoResetEvent(false);
		static int listenPort = -1;
		static ushort nextEph = 40000;
		static uint ipId;
		static int loopTicks;
		public static bool TunnelOn { get { return up; } }
		public static bool HasConfig { get { return vpnKind != 0; } }
		public static string Kind { get { return vpnKind == 2 ? "OpenVPN" : (vpnKind == 1 ? "WireGuard" : ""); } }
		public static bool Blocked { get { return Require && HasConfig && !up; } }
		public static bool OsAllowed { get { return !(Require && HasConfig); } }
		public static bool HasDns { get { return dnsIp != 0; } }
		public static int Mtu { get { return mtu; } }
		internal static int SegMss() {
			int m = mtu - 40;
			if (m < 536) m = 536;
			if (m > 1440) m = 1440;
			return m;
		}
		static byte[] GrowBuf(ref byte[] slot, int n) {
			byte[] a = slot;
			if (a == null || a.Length < n) {
				int sz = 2048;
				while (sz < n) sz *= 2;
				a = new byte[sz];
				slot = a;
			}
			return a;
		}
		static byte[] IpBuf(int n) { return GrowBuf(ref tsIp, n); }
		static byte[] WgBuf(int n) { return GrowBuf(ref tsWg, n); }
		static byte[] PlainBuf(int n) { return GrowBuf(ref tsPlain, n < 1 ? 1 : n); }
		static void ArmMss(VpnTcp t, byte[] ip, int ihl, int doff) {
			int ours = SegMss();
			if (ours > VpnTcp.Mss) ours = VpnTcp.Mss;
			t.sndMss = ours;
			int pm = ParseMss(ip, ihl, doff);
			if (pm > 0) t.NotePeerMss(pm);
		}
		public static string LocalAddress {
			get { return localIp == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", (localIp >> 24) & 255, (localIp >> 16) & 255, (localIp >> 8) & 255, localIp & 255); }
		}

		static byte[] Hx(string s) {
			byte[] o = new byte[s.Length / 2];
			for (int i = 0; i < o.Length; i++)
				o[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			return o;
		}

		public static string SelfCheck() {
			byte[] z = Blake2s.Hash(new byte[0], 32);
			string hex = BitConverter.ToString(z).Replace("-", "").ToLowerInvariant();
			if (hex != "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9") return "blake2s " + hex;
			byte[] abc = Blake2s.Hash(Encoding.ASCII.GetBytes("abc"), 32);
			string abch = BitConverter.ToString(abc).Replace("-", "").ToLowerInvariant();
			if (abch != "508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982") return "blake2s-abc " + abch;
			byte[] sk = Hx("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
			byte[] su = Hx("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
			byte[] so = X25519.ScalarMult(sk, su);
			string soh = BitConverter.ToString(so).Replace("-", "").ToLowerInvariant();
			if (soh != "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552") return "x25519 " + soh;
			byte[] k = new byte[32];
			for (int i = 0; i < 32; i++) k[i] = (byte)i;
			byte[] pt = Encoding.ASCII.GetBytes("powertorrent-aead");
			byte[] sealedB = ChaChaPoly.Seal(k, 7, pt, Encoding.ASCII.GetBytes("ad"));
			byte[] open = ChaChaPoly.Open(k, 7, sealedB, Encoding.ASCII.GetBytes("ad"));
			if (open == null || open.Length != pt.Length) return "aead-open";
			for (int i = 0; i < pt.Length; i++) if (open[i] != pt[i]) return "aead-pt";
			if (ChaChaPoly.Open(k, 8, sealedB, Encoding.ASCII.GetBytes("ad")) != null) return "aead-ctr";
			byte[] rfcKey = Hx("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
			byte[] rfcNonce = Hx("070000004041424344454647");
			byte[] rfcAad = Hx("50515253c0c1c2c3c4c5c6c7");
			byte[] rfcPt = Hx("4c616469657320616e642047656e746c656d656e206f662074686520636c617373206f66202739393a204966204920636f756c64206f6666657220796f75206f6e6c79206f6e652074697020666f7220746865206675747572652c2073756e73637265656e20776f756c642062652069742e");
			byte[] rfcWant = Hx("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b61161ae10b594f09e26a7e902ecbd0600691");
			byte[] rfcGot = ChaChaPoly.SealNonce(rfcKey, rfcNonce, rfcPt, rfcAad);
			if (rfcGot == null || rfcGot.Length != rfcWant.Length) return "aead-rfc-len";
			for (int i = 0; i < rfcWant.Length; i++) if (rfcGot[i] != rfcWant[i]) return "aead-rfc " + i.ToString(CultureInfo.InvariantCulture);
			byte[] prf = Tls1Prf.Prf(Encoding.ASCII.GetBytes("tls1-prf-test-secret"), "tls1-prf-test", new byte[0], 8);
			byte[] wantPrf = new byte[] { 0x71, 0x44, 0xfe, 0x25, 0x40, 0x73, 0x75, 0x95 };
			if (prf == null || prf.Length != 8) return "tls1-prf-len";
			for (int i = 0; i < 8; i++) if (prf[i] != wantPrf[i]) return "tls1-prf";
			byte[] gk = new byte[32];
			byte[] gn = new byte[12];
			byte[] gcm = AesGcmPt.Seal(gk, gn, new byte[0], new byte[0]);
			byte[] gwant = Hx("530f8afbc74536b9a963b4f1c4cb738b");
			if (gcm == null || gcm.Length != 16) return "gcm-len";
			for (int i = 0; i < 16; i++) if (gcm[i] != gwant[i]) return "gcm-empty";
			byte[] gopen = AesGcmPt.Open(gk, gn, new byte[0], gcm);
			if (gopen == null || gopen.Length != 0) return "gcm-open-empty";
			byte[] gpt = Encoding.ASCII.GetBytes("powertorrent-gcm");
			byte[] gad = Encoding.ASCII.GetBytes("ad");
			byte[] gse = AesGcmPt.Seal(gk, gn, gad, gpt);
			byte[] gop = AesGcmPt.Open(gk, gn, gad, gse);
			if (gop == null || gop.Length != gpt.Length) return "gcm-rt";
			for (int i = 0; i < gpt.Length; i++) if (gop[i] != gpt[i]) return "gcm-pt";
			if (AesGcmPt.Open(gk, gn, Encoding.ASCII.GetBytes("xx"), gse) != null) return "gcm-ad";
			int hits = AesGcmPt.CngHits;
			byte[] gcm2 = AesGcmPt.Seal(gk, gn, new byte[0], new byte[0]);
			if (AesGcmPt.CngHits == hits || gcm2 == null) return "gcm-cng-seal";
			hits = AesGcmPt.CngHits;
			byte[] gopen2 = AesGcmPt.Open(gk, gn, new byte[0], gcm2);
			if (AesGcmPt.CngHits == hits || gopen2 == null) return "gcm-cng-open";
			byte[] noAd = ChaChaPoly.Seal(k, 7, pt, null);
			byte[] into = new byte[pt.Length + 16];
			int intoN = ChaChaPoly.SealInto(k, 7, pt, 0, pt.Length, into, 0);
			if (noAd == null || intoN != noAd.Length) return "aead-into-len";
			for (int i = 0; i < intoN; i++) if (into[i] != noAd[i]) return "aead-into";
			byte[] inplace = new byte[pt.Length + 16];
			Buffer.BlockCopy(pt, 0, inplace, 0, pt.Length);
			ChaChaPoly.SealInto(k, 7, inplace, 0, pt.Length, inplace, 0);
			for (int i = 0; i < intoN; i++) if (inplace[i] != noAd[i]) return "aead-inplace";
			byte[] back = new byte[pt.Length + 8];
			if (!ChaChaPoly.OpenInto(k, 7, into, 0, intoN, back, 3)) return "aead-openinto";
			for (int i = 0; i < pt.Length; i++) if (back[3 + i] != pt[i]) return "aead-openinto-pt";
			if (ParsePlainV4(Encoding.ASCII.GetBytes("203.0.113.5\n")) != "203.0.113.5") return "probe-parse";
			if (ParsePlainV4(Encoding.ASCII.GetBytes("10.1.1.1\n")) != null) return "probe-priv";
			string cc = TcpCcSelfCheck();
			if (cc != null) return cc;
			return null;
		}

		public static bool LoadConfig(string text, out string err) {
			err = "";
			if (string.IsNullOrEmpty(text)) { err = "empty config"; return false; }
			if (text.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase) >= 0)
				return LoadWireGuard(text, out err);
			OvpnSess parsed;
			if (OvpnSess.TryParse(text, out parsed, out err)) {
				StopCore(true);
				if (!parsed.Resolve(out err)) err = "";
				lock (gate) {
					vpnKind = 2; ovpnRaw = text; ovpn = parsed;
					priv = null; peerPub = null; psk = null; staticPub = null;
					endpoint = parsed.Endpoint;
					localIp = 0; dnsIp = 0;
					keepalive = 10; mtu = parsed.TunMtu > 576 ? Math.Min(parsed.TunMtu, 1400) : 1400;
				}
				Status = "configured OpenVPN " + parsed.RemoteHost;
				LastError = "";
				return true;
			}
			if (string.IsNullOrEmpty(err) || err == "not an OpenVPN config")
				err = "not a WireGuard or OpenVPN config";
			return false;
		}

		static bool LoadWireGuard(string text, out string err) {
			err = "";
			try {
				string privB = null, pubB = null, pskB = null, addr = null, ep = null, dns = null;
				int ka = 15, m = 1320;
				string[] lines = text.Replace("\r", "").Split('\n');
				for (int i = 0; i < lines.Length; i++) {
					string ln = lines[i].Trim();
					if (ln.Length == 0 || ln[0] == '#' || ln[0] == ';') continue;
					int eq = ln.IndexOf('='); if (eq < 1) continue;
					string k = ln.Substring(0, eq).Trim().ToLowerInvariant();
					string v = ln.Substring(eq + 1).Trim();
					if (k == "privatekey") privB = v;
					else if (k == "publickey") pubB = v;
					else if (k == "presharedkey") pskB = v;
					else if (k == "address") addr = v.Split(',')[0].Trim();
					else if (k == "endpoint") ep = v;
					else if (k == "dns") dns = v.Split(',')[0].Trim();
					else if (k == "mtu") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out m);
					else if (k == "persistentkeepalive") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ka);
				}
				if (string.IsNullOrEmpty(privB) || string.IsNullOrEmpty(pubB) || string.IsNullOrEmpty(ep) || string.IsNullOrEmpty(addr)) {
					err = "config missing PrivateKey, PublicKey, Endpoint, or Address"; return false;
				}
				byte[] np = Convert.FromBase64String(privB);
				byte[] npp = Convert.FromBase64String(pubB);
				if (np.Length != 32 || npp.Length != 32) { err = "keys must be 32 bytes"; return false; }
				byte[] npsk = new byte[32];
				if (!string.IsNullOrEmpty(pskB)) {
					byte[] p = Convert.FromBase64String(pskB);
					if (p.Length != 32) { err = "PresharedKey must be 32 bytes"; return false; }
					npsk = p;
				}
				int slash = addr.IndexOf('/'); if (slash > 0) addr = addr.Substring(0, slash);
				uint nip = IpToU(IPAddress.Parse(addr));
				uint ndns = 0;
				if (!string.IsNullOrEmpty(dns)) { try { ndns = IpToU(IPAddress.Parse(dns.Split(' ')[0])); } catch { } }
				int c = ep.LastIndexOf(':');
				if (c < 1) { err = "Endpoint must be ip:port"; return false; }
				IPEndPoint nep = new IPEndPoint(IPAddress.Parse(ep.Substring(0, c)), int.Parse(ep.Substring(c + 1), CultureInfo.InvariantCulture));
				StopCore(true);
				lock (gate) {
					vpnKind = 1; ovpnRaw = null; ovpn = null;
					priv = np; peerPub = npp; psk = npsk;
					staticPub = X25519.PublicFromPrivate(priv);
					localIp = nip; dnsIp = ndns; endpoint = nep;
					keepalive = ka < 1 ? 15 : ka; mtu = m < 576 ? 1320 : m;
				}
				Status = "configured " + addr; LastError = "";
				return true;
			} catch (Exception ex) { err = ex.Message; return false; }
		}

		public static void ClearConfig() {
			Stop();
			lock (gate) {
				vpnKind = 0; ovpnRaw = null; ovpn = null;
				priv = null; peerPub = null; psk = null; staticPub = null; endpoint = null; localIp = 0; dnsIp = 0;
			}
			Status = "off"; LastError = "";
		}

		public static bool BeginStart() {
			if (vpnKind == 0) { LastError = "no VPN config"; Status = "off"; return false; }
			StopCore(false);
			try {
				if (vpnKind == 2) {
					OvpnSess parsed;
					string e;
					if (!OvpnSess.TryParse(ovpnRaw, out parsed, out e)) { LastError = e; Status = "error"; return false; }
					if (!parsed.Resolve(out e)) { LastError = e; Status = "error"; return false; }
					ovpn = parsed; endpoint = parsed.Endpoint;
				}
				udp = new UdpClient(0, AddressFamily.InterNetwork);
				udp.Client.ReceiveTimeout = 50;
				TuneUdp(udp);
				run = true; up = false;
				DroppedWhileUp = false;
				HandshakeTimedOut = false;
				handshakeWarnSent = false;
				connectAt = DateTime.UtcNow;
				Status = "connecting"; LastError = "";
				if (vpnKind == 2) {
					thr = new Thread(LoopOvpn); thr.IsBackground = true; thr.Name = "pt-ovpn"; thr.Start();
				} else {
					thr = new Thread(Loop); thr.IsBackground = true; thr.Name = "pt-wg"; thr.Start();
				}
				return true;
			} catch (Exception ex) { LastError = ex.Message; Status = "error"; StopCore(true); return false; }
		}

		public static bool Start() {
			if (!BeginStart()) return false;
			int wait = vpnKind == 2 ? 40 : 25;
			DateTime dead = DateTime.UtcNow.AddSeconds(wait);
			while (!up && DateTime.UtcNow < dead) Thread.Sleep(50);
			if (!up) {
				if (string.IsNullOrEmpty(LastError)) LastError = "handshake timeout to " + endpoint;
				if (Require) HandshakeTimedOut = true;
				StopCore(true); return false;
			}
			Status = "up " + LocalAddress + " via " + endpoint; LastError = "";
			try { Session.RebindListen(); } catch { }
			return true;
		}

		public static void Stop() {
			bool warn = up && Require;
			StopCore(true);
			if (warn) DroppedWhileUp = true;
		}

		static void TuneUdp(UdpClient u) {
			if (u == null || u.Client == null) return;
			Socket s = u.Client;
			try { s.ReceiveTimeout = 200; } catch { }
			try { s.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
			try { s.SendBufferSize = 4 * 1024 * 1024; } catch { }
			try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 4 * 1024 * 1024); } catch { }
			try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 4 * 1024 * 1024); } catch { }
			try {
				byte[] outb = new byte[4];
				s.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, outb);
			} catch { }
		}

		static void StopCore(bool rebind) {
			run = false; up = false;
			lastHsOk = DateTime.MinValue;
			Interlocked.Increment(ref probeGen);
			ProvenIp = "";
			DirectIp = "";
			ProbeDone = false;
			ProbeFailed = false;
			try { if (ovpn != null) ovpn.Close(); } catch { }
			try { if (udp != null) udp.Close(); } catch { }
			udp = null;
			lock (gate) {
				foreach (VpnTcp t in new List<VpnTcp>(tcps.Values)) { t.state = 0; t.ev.Set(); }
				tcps.Clear(); udps.Clear(); accepts.Clear();
				sendKey = null; recvKey = null;
			}
			Status = HasConfig ? "configured" : "off";
			if (rebind) { try { Session.RebindListen(); } catch { } }
		}

		public static IPAddress Resolve(string host) {
			if (!up) return null;
			if (dnsIp == 0) return null;
			VpnUdpSock s = null;
			try {
				Random rng = new Random();
				ushort id = (ushort)rng.Next(1, 65535);
				MemoryStream ms = new MemoryStream();
				ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
				ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1);
				ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
				string[] lab = host.Split('.');
				for (int i = 0; i < lab.Length; i++) {
					byte[] b = Encoding.ASCII.GetBytes(lab[i]);
					ms.WriteByte((byte)b.Length); ms.Write(b, 0, b.Length);
				}
				ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(1);
				byte[] q = ms.ToArray();
				s = BindUdp(0);
				s.SendTo(q, new IPEndPoint(UToIp(dnsIp), 53));
				byte[] ans; IPEndPoint from;
				if (!s.Recv(4000, out ans, out from) || ans == null || ans.Length < 12) return null;
				if (((ans[0] << 8) | ans[1]) != id) return null;
				int qd = (ans[4] << 8) | ans[5], an = (ans[6] << 8) | ans[7];
				int o = 12;
				for (int i = 0; i < qd; i++) o = SkipName(ans, o) + 4;
				for (int i = 0; i < an; i++) {
					o = SkipName(ans, o);
					if (o + 10 > ans.Length) break;
					int typ = (ans[o] << 8) | ans[o + 1]; int rdlen = (ans[o + 8] << 8) | ans[o + 9]; o += 10;
					if (typ == 1 && rdlen == 4 && o + 4 <= ans.Length)
						return new IPAddress(new byte[] { ans[o], ans[o + 1], ans[o + 2], ans[o + 3] });
					o += rdlen;
				}
			} catch { }
			finally { if (s != null) DropUdp(s); }
			return null;
		}

		static int SkipName(byte[] d, int o) {
			while (o < d.Length) {
				int l = d[o];
				if (l == 0) return o + 1;
				if ((l & 0xC0) == 0xC0) return o + 2;
				o += 1 + l;
			}
			return o;
		}

		public static Stream OpenTcp(string host, int port, int timeoutMs) {
			if (!up) return null;
			VpnTcp t = ConnectTcp(host, port, timeoutMs);
			if (t == null) return null;
			return new VpnTcpStream(t);
		}
		internal static VpnTcp ConnectTcp(string host, int port, int timeoutMs) {
			if (!up) return null;
			IPAddress ip = Bt.ResolveV4(host); if (ip == null) return null;
			return ConnectTcp(ip, port, timeoutMs);
		}
		internal static VpnTcp ConnectTcp(IPAddress ip, int port, int timeoutMs) {
			if (!up || ip == null) return null;
			VpnTcp t = new VpnTcp();
			t.locIp = localIp; t.remIp = IpToU(ip); t.remPort = (ushort)port;
			lock (gate) { t.locPort = nextEph++; if (nextEph < 40000) nextEph = 40000; }
			t.iss = (uint)new Random().Next(); t.sndNxt = t.iss + 1; t.sndUna = t.iss + 1; t.state = 1;
			lock (gate) tcps[Key(t.remIp, t.remPort, t.locPort)] = t;
			TcpSend(t, 0x02, null, t.iss);
			DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < dead && t.state != 3 && t.state != 0) t.ev.WaitOne(50);
			if (t.state != 3) { DropTcp(t); return null; }
			return t;
		}

		public static void ListenTcp(int port) { listenPort = port; }
		internal static VpnTcp AcceptTcp(int timeoutMs) {
			if (accepts.Count > 0) { lock (gate) { if (accepts.Count > 0) return accepts.Dequeue(); } }
			acceptEv.WaitOne(timeoutMs);
			lock (gate) { return accepts.Count > 0 ? accepts.Dequeue() : null; }
		}
		internal static VpnUdpSock BindUdp(int port) {
			VpnUdpSock s = new VpnUdpSock();
			lock (gate) {
				if (port == 0) { port = nextEph++; if (nextEph < 40000) nextEph = 40000; }
				s.locPort = (ushort)port; udps[port] = s;
			}
			return s;
		}
		internal static void DropUdp(VpnUdpSock s) {
			if (s == null) return;
			lock (gate) udps.Remove(s.locPort);
		}
		internal static void UdpSend(VpnUdpSock s, byte[] data, IPEndPoint ep) {
			if (data == null) data = NoPay;
			uint dip = IpToU(ep.Address);
			byte[] pkt = IpBuf(28 + data.Length);
			int n = FillUdp(pkt, localIp, s.locPort, dip, (ushort)ep.Port, data);
			SendInner(pkt, n);
		}
		internal static void TcpSend(VpnTcp t, int flags, byte[] payload, uint seq) {
			if (payload == null) payload = NoPay;
			byte[] pkt = IpBuf(48 + payload.Length);
			int n = FillTcp(pkt, t.locIp, t.locPort, t.remIp, t.remPort, seq, t.rcvNxt, flags, payload);
			t.lastTx = DateTime.UtcNow;
			SendInner(pkt, n);
		}
		internal static void TcpPump(VpnTcp t) {
			if (t.state != 3) return;
			List<VpnSeg> toSend = new List<VpnSeg>();
			lock (t.gate) {
				int cap = t.FlightCap();
				int flightBytes = 0;
				for (int i = 0; i < t.flight.Count; i++) flightBytes += t.flight[i].data.Length;
				while (t.pending.Count > 0 && flightBytes < cap) {
					byte[] seg = t.pending.Dequeue();
					VpnSeg s = new VpnSeg();
					s.data = seg; s.seq = t.sndNxt; s.sent = DateTime.UtcNow; s.sends = 1;
					t.sndNxt += (uint)seg.Length;
					t.flight.Add(s);
					flightBytes += seg.Length;
					toSend.Add(s);
				}
			}
			for (int i = 0; i < toSend.Count; i++) TcpSend(t, 0x18, toSend[i].data, toSend[i].seq);
		}
		internal static void DropTcp(VpnTcp t) {
			lock (gate) tcps.Remove(Key(t.remIp, t.remPort, t.locPort));
		}

		public static bool HttpGet(string url, int timeoutMs, string ua, out int status, out byte[] body) {
			status = 0; body = null;
			if (string.IsNullOrEmpty(ua)) ua = "PowerTorrent/1.5";
			if (up) return HttpRequest(url, timeoutMs, -1, -1, ua, out status, out body);
			if (Require && HasConfig) return false;
			return HttpOsGetBytes(url, timeoutMs, ua, out status, out body);
		}
		static bool HttpOsGetBytes(string url, int timeoutMs, string ua, out int status, out byte[] body) {
			status = 0; body = null;
			if (Require && HasConfig) return false;
			try {
				try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; } catch { }
				HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
				req.Method = "GET";
				req.UserAgent = ua;
				req.Timeout = timeoutMs;
				req.ReadWriteTimeout = timeoutMs;
				req.KeepAlive = false;
				req.AllowAutoRedirect = true;
				try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
				using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) {
					status = (int)resp.StatusCode;
					using (Stream s = resp.GetResponseStream()) {
						if (s == null) return false;
						MemoryStream ms = new MemoryStream();
						byte[] buf = new byte[8192];
						int n, total = 0;
						while ((n = s.Read(buf, 0, buf.Length)) > 0) {
							ms.Write(buf, 0, n);
							total += n;
							if (total > 4 * 1024 * 1024) break;
						}
						body = ms.ToArray();
						return true;
					}
				}
			} catch (WebException ex) {
				try {
					HttpWebResponse r = ex.Response as HttpWebResponse;
					if (r != null) status = (int)r.StatusCode;
				} catch { }
				return false;
			} catch { return false; }
		}
		public static bool HttpRequest(string url, int timeoutMs, long rangeStart, long rangeEnd, out int status, out byte[] body) {
			return HttpRequest(url, timeoutMs, rangeStart, rangeEnd, "PowerTorrent/1.5", out status, out body);
		}
		public static bool HttpRequest(string url, int timeoutMs, long rangeStart, long rangeEnd, string ua, out int status, out byte[] body) {
			return HttpRequest(url, timeoutMs, rangeStart, rangeEnd, ua, false, out status, out body);
		}
		static bool HttpRequest(string url, int timeoutMs, long rangeStart, long rangeEnd, string ua, bool probeDns, out int status, out byte[] body) {
			status = 0; body = null;
			if (!up) return false;
			if (string.IsNullOrEmpty(ua)) ua = "PowerTorrent/1.5";
			Uri u; try { u = new Uri(url); } catch { return false; }
			int port = u.Port; if (port <= 0) port = string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
			IPAddress ip = probeDns ? ResolveProbe(u.Host) : Bt.ResolveV4(u.Host);
			if (ip == null) return false;
			VpnTcp t = ConnectTcp(ip, port, timeoutMs); if (t == null) return false;
			try {
				Stream raw = new VpnTcpStream(t);
				Stream s = raw;
				if (string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) {
					SslStream ssl = new SslStream(raw, false);
					ssl.AuthenticateAsClient(u.Host, null, SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls, false);
					s = ssl;
				}
				string path = string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
				StringBuilder sb = new StringBuilder();
				sb.Append("GET ").Append(path).Append(" HTTP/1.0\r\nHost: ").Append(u.Host).Append("\r\nUser-Agent: ").Append(ua).Append("\r\nAccept: */*\r\nConnection: close\r\n");
				if (rangeEnd >= rangeStart && rangeStart >= 0) sb.Append("Range: bytes=").Append(rangeStart.ToString(CultureInfo.InvariantCulture)).Append("-").Append(rangeEnd.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
				sb.Append("\r\n");
				byte[] req = Encoding.ASCII.GetBytes(sb.ToString());
				s.Write(req, 0, req.Length);
				MemoryStream ms = new MemoryStream();
				byte[] buf = new byte[4096];
				DateTime dead = DateTime.UtcNow.AddMilliseconds(timeoutMs);
				int hdrAt = -1;
				int contentLen = -1;
				while (DateTime.UtcNow < dead) {
					if (!t.Poll(200000)) { if (ms.Length > 0 && hdrAt >= 0 && contentLen < 0) break; continue; }
					int n = s.Read(buf, 0, buf.Length); if (n <= 0) break; ms.Write(buf, 0, n);
					if (hdrAt < 0) {
						byte[] sofar = ms.ToArray();
						string head = Encoding.ASCII.GetString(sofar);
						int sp = head.IndexOf("\r\n\r\n");
						if (sp >= 0) {
							hdrAt = sp + 4;
							int sp1 = head.IndexOf(' ');
							int sp2 = sp1 > 0 ? head.IndexOf(' ', sp1 + 1) : -1;
							if (sp1 > 0 && sp2 > sp1) int.TryParse(head.Substring(sp1 + 1, sp2 - sp1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out status);
							int cl = head.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
							if (cl >= 0 && cl < sp) {
								int cle = head.IndexOf("\r\n", cl);
								if (cle > cl) int.TryParse(head.Substring(cl + 15, cle - (cl + 15)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLen);
							}
						}
					}
					if (hdrAt >= 0 && contentLen >= 0 && (ms.Length - hdrAt) >= contentLen) break;
				}
				byte[] all = ms.ToArray();
				if (hdrAt < 0 || hdrAt > all.Length) return false;
				int blen = all.Length - hdrAt;
				if (contentLen >= 0 && blen > contentLen) blen = contentLen;
				body = Slice(all, hdrAt, blen);
				return true;
			} catch { return false; }
			finally { t.Close(); }
		}

		static int probeGen;
		static string HttpOsGet(string url, int timeoutMs) {
			return null;
		}
		static string ProbeCurl() {
			return null;
		}
		static IPAddress FirstOsV4(string host) {
			return null;
		}
		static string ParseHttpBodyIp(byte[] raw) {
			if (raw == null || raw.Length == 0) return null;
			string text = Encoding.ASCII.GetString(raw);
			int sp = text.IndexOf("\r\n\r\n");
			if (sp >= 0) return ParsePlainV4(Slice(raw, sp + 4, raw.Length - (sp + 4)));
			return ParsePlainV4(raw);
		}
		static string HttpOsPlain(IPAddress ip, int port, bool tls) {
			return null;
		}
		static string ProbeOsOnce() {
			return null;
		}
		static string ParsePlainV4(byte[] body) {
			if (body == null || body.Length == 0) return null;
			string s = Encoding.ASCII.GetString(body).Trim();
			if (s.Length == 0 || s[0] == '<') return null;
			int cut = s.Length;
			for (int i = 0; i < s.Length; i++) {
				char ch = s[i];
				if (ch == '\r' || ch == '\n' || ch == ' ' || ch == '<' || ch == ',') { cut = i; break; }
			}
			s = s.Substring(0, cut).Trim();
			IPAddress a;
			if (!IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork) return null;
			byte[] b = a.GetAddressBytes();
			int p = b[0];
			if (p == 0 || p == 10 || p == 127 || p == 169 || p >= 224) return null;
			if (p == 172 && b[1] >= 16 && b[1] <= 31) return null;
			if (p == 192 && b[1] == 168) return null;
			return a.ToString();
		}
		static IPAddress ResolveProbe(string host) {
			IPAddress parsed;
			if (IPAddress.TryParse(host, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork) return parsed;
			if (dnsIp != 0) {
				IPAddress via = Resolve(host);
				if (via != null) return via;
			}
			return FirstOsV4(host);
		}
		static string ProbeOnce() {
			if (!up) return null;
			string[] urls = new string[] { "http://ip.me/", "https://ip.me/" };
			for (int u = 0; u < urls.Length && up; u++) {
				int st; byte[] body;
				if (!HttpRequest(urls[u], 10000, -1, -1, "curl/8.4.0", true, out st, out body) || body == null) continue;
				string ip = ParsePlainV4(body);
				if (ip != null) return ip;
			}
			return null;
		}
		static void ProbeWorker(object state) {
			int g = (int)state;
			string vpnIp = null;
			string osIp = null;
			for (int i = 0; i < 4 && up && g == probeGen; i++) {
				try {
					if (vpnIp == null) vpnIp = ProbeOnce();
					if (osIp == null) osIp = ProbeOsOnce();
					if (g != probeGen) return;
					if (vpnIp != null) ProvenIp = vpnIp;
					if (osIp != null) DirectIp = osIp;
					if (vpnIp != null && osIp != null) {
						ProbeFailed = string.Equals(vpnIp, osIp, StringComparison.Ordinal);
						ProbeDone = true;
						if (!ProbeFailed) Status = "up " + vpnIp + " via " + endpoint;
						else LastError = "VPN IP matches outside IP";
						return;
					}
				} catch { }
				Thread.Sleep(1500);
			}
			if (g == probeGen) {
				if (vpnIp != null) ProvenIp = vpnIp;
				if (osIp != null) DirectIp = osIp;
				ProbeDone = true;
			}
		}
		static void BeginProbe() {
			int g = Interlocked.Increment(ref probeGen);
			ProbeDone = false;
			ProbeFailed = false;
			DirectIp = "";
			Thread t = new Thread(ProbeWorker);
			t.IsBackground = true;
			t.Name = "pt-ipme";
			t.Start(g);
		}

		static byte[] Slice(byte[] a, int o, int n) { byte[] b = new byte[n]; Buffer.BlockCopy(a, o, b, 0, n); return b; }
		static uint IpToU(IPAddress a) { byte[] b = a.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
		static IPAddress UToIp(uint u) { return new IPAddress(new byte[] { (byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u }); }
		static string Key(uint ip, ushort rp, ushort lp) { return ip.ToString("x") + ":" + rp.ToString(CultureInfo.InvariantCulture) + ":" + lp.ToString(CultureInfo.InvariantCulture); }

		internal static void NoteRx() { lastRecv = DateTime.UtcNow; }
		internal static void SetTunnelIp(IPAddress a) {
			if (a != null && a.AddressFamily == AddressFamily.InterNetwork) localIp = IpToU(a);
		}
		internal static void SetDnsIp(IPAddress a) {
			if (a != null && a.AddressFamily == AddressFamily.InterNetwork) dnsIp = IpToU(a);
		}
		internal static void SetKeepalive(int s) { if (s > 0) keepalive = s; }
		internal static void NoteOvpnUp() {
			if (localIp == 0) { LastError = "OpenVPN PUSH missing ifconfig"; return; }
			if (dnsIp == 0) dnsIp = 0x01010101u;
			up = true;
			lastData = DateTime.UtcNow; lastRecv = DateTime.UtcNow;
			Status = "up " + LocalAddress + " via " + endpoint; LastError = "";
			HandshakeTimedOut = false;
			try { Session.RebindListen(); } catch { }
			BeginProbe();
		}

		static bool RecvPkt(out byte[] pkt) {
			pkt = null;
			UdpClient u = udp;
			if (u == null) return false;
			IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
			try {
				pkt = u.Receive(ref any);
				return pkt != null && pkt.Length > 0;
			} catch (SocketException) {
				return false;
			}
		}
		static int UdpQueued() {
			try {
				UdpClient u = udp;
				if (u == null || u.Client == null) return 0;
				return u.Available;
			} catch { return 0; }
		}

		static void LoopOvpn() {
			OvpnSess o = ovpn;
			if (o == null) return;
			o.StartHs();
			while (run) {
				try {
					o.Tick();
					if (!up && Require && !handshakeWarnSent && connectAt != DateTime.MinValue && (DateTime.UtcNow - connectAt).TotalSeconds >= 25) {
						HandshakeTimedOut = true; handshakeWarnSent = true;
					}
					if (run && up && (DateTime.UtcNow - lastRecv).TotalSeconds > 180) {
						up = false; Status = "reconnecting"; LastError = "VPN dropped";
						if (Require) DroppedWhileUp = true;
						try { Session.RebindListen(); } catch { }
					}
					if (up && keepalive > 0 && (DateTime.UtcNow - lastData).TotalSeconds >= keepalive) {
						o.SendPing(); lastData = DateTime.UtcNow;
					}
					byte[] pkt;
					if (!RecvPkt(out pkt)) { TickTcp(); continue; }
					int n = 0;
					while (pkt != null) {
						o.OnPacket(pkt);
						n++;
						if (n >= 48 || UdpQueued() <= 0) break;
						if (!RecvPkt(out pkt)) break;
					}
					if ((++loopTicks & 3) == 0) TickTcp();
				} catch { if (!run) break; }
			}
		}

		static void Loop() {
			Initiate();
			while (run) {
				try {
					DateTime now = DateTime.UtcNow;
					if (!up) {
						if (Require && !handshakeWarnSent && connectAt != DateTime.MinValue && (now - connectAt).TotalSeconds >= 25) {
							HandshakeTimedOut = true; handshakeWarnSent = true;
						}
						if (lastHs == DateTime.MinValue || (now - lastHs).TotalSeconds >= 5) Initiate();
					} else {
						if (lastHsOk != DateTime.MinValue && (now - lastHsOk).TotalSeconds >= 120 && (now - lastHs).TotalSeconds >= 5)
							Initiate();
						if (run && lastHsOk != DateTime.MinValue && (now - lastHsOk).TotalSeconds >= 180) {
							up = false; Status = "reconnecting"; LastError = "VPN dropped";
							if (Require) DroppedWhileUp = true;
							try { Session.RebindListen(); } catch { }
						}
					}
					if (up && keepalive > 0 && (now - lastData).TotalSeconds >= keepalive) { WgSend(new byte[0]); lastData = DateTime.UtcNow; }
					byte[] pkt;
					if (!RecvPkt(out pkt)) { TickTcp(); continue; }
					int n = 0;
					while (pkt != null) {
						if (pkt.Length >= 4) {
							uint typ = BitConverter.ToUInt32(pkt, 0);
							if (typ == 2) HandleResp(pkt);
							else if (typ == 3) LastError = "server cookie (endpoint under load)";
							else if (typ == 4) HandleData(pkt);
						}
						n++;
						if (n >= 48 || UdpQueued() <= 0) break;
						if (!RecvPkt(out pkt)) break;
					}
					if ((++loopTicks & 3) == 0) TickTcp();
				} catch { if (!run) break; }
			}
		}

		static void Initiate() {
			try {
				ephPriv = X25519.RandomPrivate(); ephPub = X25519.PublicFromPrivate(ephPriv);
				byte[] idx = new byte[4]; new RNGCryptoServiceProvider().GetBytes(idx);
				hsLocalIndex = BitConverter.ToUInt32(idx, 0);
				byte[] cons = Encoding.ASCII.GetBytes("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s");
				byte[] ident = Encoding.ASCII.GetBytes("WireGuard v1 zx2c4 Jason@zx2c4.com");
				hsCk = Blake2s.Hash(cons, 32);
				hsHash = WgKdf.HashCat(WgKdf.HashCat(hsCk, ident), peerPub);
				hsHash = WgKdf.HashCat(hsHash, ephPub);
				byte[] ck = new byte[32]; WgKdf.Kdf1(hsCk, ephPub, ck); hsCk = ck;
				byte[] key = new byte[32]; byte[] nck = new byte[32];
				WgKdf.Kdf2(hsCk, X25519.ScalarMult(ephPriv, peerPub), nck, key); hsCk = nck;
				byte[] encSt = ChaChaPoly.Seal(key, 0, staticPub, hsHash);
				hsHash = WgKdf.HashCat(hsHash, encSt);
				WgKdf.Kdf2(hsCk, X25519.ScalarMult(priv, peerPub), nck, key); hsCk = nck;
				byte[] ts = Tai();
				byte[] encTs = ChaChaPoly.Seal(key, 0, ts, hsHash);
				hsHash = WgKdf.HashCat(hsHash, encTs);
				byte[] msg = new byte[148];
				msg[0] = 1; BitConverter.GetBytes(hsLocalIndex).CopyTo(msg, 4);
				Buffer.BlockCopy(ephPub, 0, msg, 8, 32);
				Buffer.BlockCopy(encSt, 0, msg, 40, 48);
				Buffer.BlockCopy(encTs, 0, msg, 88, 28);
				byte[] mac1key = Blake2s.Hash(Cat(Encoding.ASCII.GetBytes("mac1----"), peerPub), 32);
				byte[] pre = new byte[116]; Buffer.BlockCopy(msg, 0, pre, 0, 116);
				byte[] mac1 = Blake2s.Mac16(mac1key, pre);
				Buffer.BlockCopy(mac1, 0, msg, 116, 16);
				UdpSendRaw(msg);
				lastHs = DateTime.UtcNow;
			} catch (Exception ex) { LastError = "initiate: " + ex.Message; }
		}

		static void HandleResp(byte[] pkt) {
			if (pkt.Length < 92 || hsCk == null || staticPub == null) return;
			byte[] mac1key = Blake2s.Hash(Cat(Encoding.ASCII.GetBytes("mac1----"), staticPub), 32);
			byte[] pre = new byte[60]; Buffer.BlockCopy(pkt, 0, pre, 0, 60);
			byte[] want = Blake2s.Mac16(mac1key, pre);
			for (int i = 0; i < 16; i++) if (want[i] != pkt[60 + i]) return;
			uint theirIdx = BitConverter.ToUInt32(pkt, 4);
			uint toUs = BitConverter.ToUInt32(pkt, 8);
			if (toUs != hsLocalIndex) return;
			byte[] theirEph = new byte[32]; Buffer.BlockCopy(pkt, 12, theirEph, 0, 32);
			byte[] encEmpty = new byte[16]; Buffer.BlockCopy(pkt, 44, encEmpty, 0, 16);
			byte[] hash = WgKdf.HashCat(hsHash, theirEph);
			byte[] ck = new byte[32]; WgKdf.Kdf1(hsCk, theirEph, ck);
			byte[] tmp = new byte[32];
			WgKdf.Kdf1(ck, X25519.ScalarMult(ephPriv, theirEph), tmp); ck = tmp;
			WgKdf.Kdf1(ck, X25519.ScalarMult(priv, theirEph), tmp); ck = tmp;
			byte[] t2 = new byte[32], key = new byte[32], nck = new byte[32];
			WgKdf.Kdf3(ck, psk, nck, t2, key); ck = nck;
			hash = WgKdf.HashCat(hash, t2);
			byte[] empty = ChaChaPoly.Open(key, 0, encEmpty, hash);
			if (empty == null) return;
			hsHash = WgKdf.HashCat(hash, encEmpty);
			byte[] t1 = new byte[32]; WgKdf.Kdf2(ck, new byte[0], t1, tmp);
			bool firstUp;
			lock (gate) {
				firstUp = !up;
				sendKey = t1; recvKey = tmp; localIndex = hsLocalIndex; remoteIndex = theirIdx;
				sendCtr = 0; recvCtr = 0; up = true;
				lastData = DateTime.UtcNow; lastRecv = DateTime.UtcNow; lastHsOk = DateTime.UtcNow;
			}
			Status = "up " + LocalAddress + " via " + endpoint; LastError = "";
			HandshakeTimedOut = false;
			try { Session.RebindListen(); } catch { }
			WgSend(new byte[0]);
			if (firstUp || string.IsNullOrEmpty(ProvenIp)) BeginProbe();
		}

		static void HandleData(byte[] pkt) {
			if (!up || pkt.Length < 32 || recvKey == null) return;
			uint recvIdx = BitConverter.ToUInt32(pkt, 4);
			if (recvIdx != localIndex) return;
			ulong ctr = BitConverter.ToUInt64(pkt, 8);
			int cLen = pkt.Length - 16;
			if (cLen < 16) return;
			int plen = cLen - 16;
			byte[] plain = PlainBuf(plen);
			if (!ChaChaPoly.OpenInto(recvKey, ctr, pkt, 16, cLen, plain, 0)) return;
			if (ctr + 1 > recvCtr) recvCtr = ctr + 1;
			OnInnerIp(plain, plen);
		}

		internal static void OnInnerIp(byte[] inner) {
			if (inner == null) return;
			OnInnerIp(inner, inner.Length);
		}
		internal static void OnInnerIp(byte[] inner, int len) {
			lastData = DateTime.UtcNow; lastRecv = DateTime.UtcNow;
			if (inner == null || len < 20 || inner.Length < len) return;
			if ((inner[0] >> 4) != 4) return;
			int ihl = (inner[0] & 0xF) * 4;
			if (ihl < 20 || len < ihl) return;
			int proto = inner[9];
			uint src = ((uint)inner[12] << 24) | ((uint)inner[13] << 16) | ((uint)inner[14] << 8) | inner[15];
			int tot = (inner[2] << 8) | inner[3]; if (tot > len) tot = len; if (tot < ihl) return;
			if (proto == 17 && tot >= ihl + 8) {
				int sp = (inner[ihl] << 8) | inner[ihl + 1];
				int dp = (inner[ihl + 2] << 8) | inner[ihl + 3];
				int ul = (inner[ihl + 4] << 8) | inner[ihl + 5];
				int pay = ul - 8; if (pay < 0) return;
				if (ihl + 8 + pay > tot) pay = tot - ihl - 8;
				if (pay < 0) return;
				byte[] data = new byte[pay]; Buffer.BlockCopy(inner, ihl + 8, data, 0, pay);
				VpnUdpSock s; lock (gate) { udps.TryGetValue(dp, out s); }
				if (s != null) {
					lock (s.gate) { s.rx.Enqueue(data); s.from.Enqueue(new IPEndPoint(UToIp(src), sp)); }
					s.ev.Set();
				}
			} else if (proto == 6 && tot >= ihl + 20) OnTcp(inner, ihl, tot, src);
		}

		static void OnTcp(byte[] ip, int ihl, int tot, uint src) {
			int sp = (ip[ihl] << 8) | ip[ihl + 1];
			int dp = (ip[ihl + 2] << 8) | ip[ihl + 3];
			uint seq = ((uint)ip[ihl + 4] << 24) | ((uint)ip[ihl + 5] << 16) | ((uint)ip[ihl + 6] << 8) | ip[ihl + 7];
			uint ack = ((uint)ip[ihl + 8] << 24) | ((uint)ip[ihl + 9] << 16) | ((uint)ip[ihl + 10] << 8) | ip[ihl + 11];
			int doff = ((ip[ihl + 12] >> 4) & 0xF) * 4;
			int flags = ip[ihl + 13];
			int payOff = ihl + doff; int payLen = tot - payOff; if (payLen < 0) payLen = 0;
			string k = Key(src, (ushort)sp, (ushort)dp);
			VpnTcp t; lock (gate) { tcps.TryGetValue(k, out t); }
			if (t == null) {
				if ((flags & 2) != 0 && dp == listenPort && listenPort > 0) {
					t = new VpnTcp(); t.locIp = localIp; t.remIp = src; t.locPort = (ushort)dp; t.remPort = (ushort)sp;
					t.rcvNxt = seq + 1; t.iss = (uint)new Random().Next(); t.sndNxt = t.iss + 1; t.sndUna = t.iss + 1; t.state = 2;
					t.sndWScale = ParseWScale(ip, ihl, doff);
					ArmMss(t, ip, ihl, doff);
					int rw = (ip[ihl + 14] << 8) | ip[ihl + 15]; t.peerWnd = (uint)rw;
					lock (gate) tcps[k] = t;
					TcpSend(t, 0x12, null, t.iss);
				}
				return;
			}
			bool sendSynAckAck = false;
			bool pump = false;
			bool needAck = false;
			VpnSeg fastRt = null;
			lock (t.gate) {
				if ((flags & 4) != 0) { t.rst = true; t.state = 0; t.ev.Set(); return; }
				int rawW = (ip[ihl + 14] << 8) | ip[ihl + 15];
				bool countDup = t.lastRawWnd == rawW && payLen == 0 && (flags & 2) == 0;
				t.lastRawWnd = rawW;
				if ((flags & 2) != 0) {
					t.sndWScale = ParseWScale(ip, ihl, doff);
					ArmMss(t, ip, ihl, doff);
					t.peerWnd = (uint)rawW;
				} else if (t.sndWScale > 0) t.peerWnd = ((uint)rawW) << t.sndWScale;
				else t.peerWnd = (uint)rawW;
				if (t.state == 1 && (flags & 18) == 18) {
					t.rcvNxt = seq + 1; t.state = 3; t.sndUna = t.iss + 1; sendSynAckAck = true;
				}
				if ((flags & 16) != 0) {
					t.OnCumulativeAck(ack, countDup);
					if (t.rexmit != null) fastRt = t.rexmit;
					if (t.wantPump) pump = true;
					if (t.state == 2 && (flags & 2) == 0) {
						t.state = 3; lock (gate) accepts.Enqueue(t); acceptEv.Set();
					}
				}
				if (payLen > 0 && t.state == 3) {
					uint pend = seq + (uint)payLen;
					if (seq <= t.rcvNxt && pend > t.rcvNxt) {
						int skip = (int)(t.rcvNxt - seq);
						int keep = payLen - skip;
						byte[] chunk = new byte[keep];
						Buffer.BlockCopy(ip, payOff + skip, chunk, 0, keep);
						t.rxq.Enqueue(chunk); t.rxAvail += keep;
						t.rcvNxt += (uint)keep;
						byte[] more;
						while (t.ooo.TryGetValue(t.rcvNxt, out more)) {
							t.ooo.Remove(t.rcvNxt);
							t.rxq.Enqueue(more); t.rxAvail += more.Length;
							t.rcvNxt += (uint)more.Length;
						}
						t.ev.Set();
						needAck = true;
					} else if ((int)(seq - t.rcvNxt) > 0 && (seq - t.rcvNxt) < 4194304u && t.ooo.Count < 256) {
						if (!t.ooo.ContainsKey(seq)) {
							byte[] chunk = new byte[payLen];
							Buffer.BlockCopy(ip, payOff, chunk, 0, payLen);
							t.ooo[seq] = chunk;
						}
						needAck = true;
					} else if ((int)(seq - t.rcvNxt) <= 0) {
						needAck = true;
					}
				}
				if ((flags & 1) != 0 && t.state == 3) {
					uint fseq = seq + (uint)payLen;
					if (fseq == t.rcvNxt) {
						t.rcvNxt++; needAck = true; t.state = 4; t.ev.Set();
					} else if (fseq > t.rcvNxt) { t.finSeen = true; t.finSeq = fseq; }
				}
				if (t.finSeen && t.state == 3 && t.rcvNxt == t.finSeq) {
					t.rcvNxt++; needAck = true; t.state = 4; t.ev.Set();
				}
			}
			if (sendSynAckAck) { TcpSend(t, 0x10, null, t.sndNxt); t.ev.Set(); }
			else if (needAck) TcpSend(t, 0x10, null, t.sndNxt);
			if (fastRt != null) TcpSend(t, 0x18, fastRt.data, fastRt.seq);
			if (pump) TcpPump(t);
		}

		static void TickTcp() {
			List<VpnTcp> list; lock (gate) list = new List<VpnTcp>(tcps.Values);
			DateTime now = DateTime.UtcNow;
			for (int i = 0; i < list.Count; i++) {
				VpnTcp t = list[i];
				VpnSeg oldest = null;
				lock (t.gate) {
					if (t.OnRetransmitTimeout(now)) oldest = t.rexmit;
				}
				if (oldest != null) TcpSend(t, 0x18, oldest.data, oldest.seq);
				TcpPump(t);
			}
		}

		internal static void UdpSendRaw(byte[] msg) {
			if (msg == null) return;
			UdpSendRaw(msg, msg.Length);
		}
		static void UdpSendRaw(byte[] msg, int len) {
			UdpClient u = udp;
			IPEndPoint ep = endpoint;
			if (u == null || ep == null || msg == null || len <= 0) return;
			lock (udpGate) {
				try { u.Send(msg, len, ep); } catch { }
			}
		}

		static void SendInner(byte[] inner) {
			if (inner == null) return;
			SendInner(inner, inner.Length);
		}
		static void SendInner(byte[] inner, int len) {
			if (!up || inner == null || len <= 0) return;
			if (vpnKind == 2) {
				OvpnSess o = ovpn;
				if (o != null) o.SendInner(inner, len);
				lastData = DateTime.UtcNow;
				return;
			}
			WgSend(inner, len);
		}

		static void WgSend(byte[] inner) {
			if (inner == null) return;
			WgSend(inner, inner.Length);
		}
		static void WgSend(byte[] inner, int len) {
			if (sendKey == null || udp == null || inner == null || len < 0) return;
			int pad = (16 - (len % 16)) % 16;
			int plainLen = len + pad;
			int msgLen = 16 + plainLen + 16;
			byte[] msg = WgBuf(msgLen);
			if (len > 0) Buffer.BlockCopy(inner, 0, msg, 16, len);
			for (int i = 0; i < pad; i++) msg[16 + len + i] = 0;
			ulong ctr; uint ridx;
			lock (gate) { ctr = sendCtr++; ridx = remoteIndex; }
			ChaChaPoly.SealInto(sendKey, ctr, msg, 16, plainLen, msg, 16);
			msg[0] = 4; msg[1] = 0; msg[2] = 0; msg[3] = 0;
			msg[4] = (byte)ridx; msg[5] = (byte)(ridx >> 8); msg[6] = (byte)(ridx >> 16); msg[7] = (byte)(ridx >> 24);
			msg[8] = (byte)ctr; msg[9] = (byte)(ctr >> 8); msg[10] = (byte)(ctr >> 16); msg[11] = (byte)(ctr >> 24);
			msg[12] = (byte)(ctr >> 32); msg[13] = (byte)(ctr >> 40); msg[14] = (byte)(ctr >> 48); msg[15] = (byte)(ctr >> 56);
			UdpSendRaw(msg, msgLen);
			lastData = DateTime.UtcNow;
		}

		static int FillUdp(byte[] p, uint sip, ushort sp, uint dip, ushort dp, byte[] data) {
			if (data == null) data = NoPay;
			int ul = 8 + data.Length;
			int tot = 20 + ul;
			p[0] = 0x45; p[1] = 0; p[2] = (byte)(tot >> 8); p[3] = (byte)tot;
			uint id = ++ipId; p[4] = (byte)(id >> 8); p[5] = (byte)id;
			p[6] = 0; p[7] = 0; p[8] = 64; p[9] = 17; p[10] = 0; p[11] = 0;
			PutIp(p, 12, sip); PutIp(p, 16, dip);
			PutCsum(p, 0, 20, 10);
			p[20] = (byte)(sp >> 8); p[21] = (byte)sp; p[22] = (byte)(dp >> 8); p[23] = (byte)dp;
			p[24] = (byte)(ul >> 8); p[25] = (byte)ul; p[26] = 0; p[27] = 0;
			if (data.Length > 0) Buffer.BlockCopy(data, 0, p, 28, data.Length);
			PutUdpCsum(p, sip, dip, ul);
			return tot;
		}
		static int ParseOpt(byte[] ip, int ihl, int doff, int kind, int need) {
			int o = ihl + 20, end = ihl + doff;
			while (o < end) {
				int k = ip[o];
				if (k == 0) break;
				if (k == 1) { o++; continue; }
				if (o + 1 >= end) break;
				int l = ip[o + 1]; if (l < 2 || o + l > end) break;
				if (k == kind && l == need) return o;
				o += l;
			}
			return -1;
		}
		static int ParseWScale(byte[] ip, int ihl, int doff) {
			int o = ParseOpt(ip, ihl, doff, 3, 3);
			return o < 0 ? 0 : ip[o + 2];
		}
		static int ParseMss(byte[] ip, int ihl, int doff) {
			int o = ParseOpt(ip, ihl, doff, 2, 4);
			if (o < 0 || o + 3 >= ip.Length) return 0;
			return (ip[o + 2] << 8) | ip[o + 3];
		}
		static int FillTcp(byte[] p, uint sip, ushort sp, uint dip, ushort dp, uint seq, uint ack, int flags, byte[] payload) {
			if (payload == null) payload = NoPay;
			bool syn = (flags & 2) != 0;
			int hl = syn ? 28 : 20;
			int tot = 20 + hl + payload.Length;
			p[0] = 0x45; p[1] = 0; p[2] = (byte)(tot >> 8); p[3] = (byte)tot;
			uint id = ++ipId; p[4] = (byte)(id >> 8); p[5] = (byte)id;
			p[6] = 0; p[7] = 0; p[8] = 64; p[9] = 6; p[10] = 0; p[11] = 0;
			PutIp(p, 12, sip); PutIp(p, 16, dip);
			PutCsum(p, 0, 20, 10);
			int o = 20;
			p[o] = (byte)(sp >> 8); p[o + 1] = (byte)sp; p[o + 2] = (byte)(dp >> 8); p[o + 3] = (byte)dp;
			p[o + 4] = (byte)(seq >> 24); p[o + 5] = (byte)(seq >> 16); p[o + 6] = (byte)(seq >> 8); p[o + 7] = (byte)seq;
			p[o + 8] = (byte)(ack >> 24); p[o + 9] = (byte)(ack >> 16); p[o + 10] = (byte)(ack >> 8); p[o + 11] = (byte)ack;
			p[o + 12] = (byte)((hl / 4) << 4); p[o + 13] = (byte)flags;
			p[o + 14] = 0xFF; p[o + 15] = 0xFF;
			p[o + 16] = 0; p[o + 17] = 0; p[o + 18] = 0; p[o + 19] = 0;
			if (syn) {
				int mss = SegMss();
				p[o + 20] = 2; p[o + 21] = 4; p[o + 22] = (byte)(mss >> 8); p[o + 23] = (byte)mss;
				p[o + 24] = 1; p[o + 25] = 3; p[o + 26] = 3; p[o + 27] = (byte)VpnTcp.WScale;
			}
			if (payload.Length > 0) Buffer.BlockCopy(payload, 0, p, 20 + hl, payload.Length);
			PutTcpCsum(p, sip, dip, hl + payload.Length);
			return tot;
		}
		static void PutIp(byte[] p, int o, uint ip) { p[o] = (byte)(ip >> 24); p[o + 1] = (byte)(ip >> 16); p[o + 2] = (byte)(ip >> 8); p[o + 3] = (byte)ip; }
		static void PutCsum(byte[] p, int o, int n, int csumOff) {
			p[csumOff] = 0; p[csumOff + 1] = 0;
			ushort s = Csum(p, o, n); p[csumOff] = (byte)(s >> 8); p[csumOff + 1] = (byte)s;
		}
		static uint CsumAcc(uint s, byte[] d, int o, int n) {
			int i = 0;
			while (i + 1 < n) { s += (uint)((d[o + i] << 8) | d[o + i + 1]); i += 2; }
			if (i < n) s += (uint)(d[o + i] << 8);
			return s;
		}
		static ushort CsumFold(uint s) {
			while ((s >> 16) != 0) s = (s & 0xffff) + (s >> 16);
			return (ushort)(~s);
		}
		static void PutUdpCsum(byte[] p, uint sip, uint dip, int ul) {
			p[26] = 0; p[27] = 0;
			uint s = (sip >> 16) + (sip & 0xffff);
			s += (dip >> 16) + (dip & 0xffff);
			s += 17u + (uint)ul;
			s = CsumAcc(s, p, 20, ul);
			ushort c = CsumFold(s);
			if (c == 0) c = 0xFFFF;
			p[26] = (byte)(c >> 8); p[27] = (byte)c;
		}
		static void PutTcpCsum(byte[] p, uint sip, uint dip, int tl) {
			p[36] = 0; p[37] = 0;
			uint s = (sip >> 16) + (sip & 0xffff);
			s += (dip >> 16) + (dip & 0xffff);
			s += 6u + (uint)tl;
			s = CsumAcc(s, p, 20, tl);
			ushort c = CsumFold(s);
			p[36] = (byte)(c >> 8); p[37] = (byte)c;
		}
		static ushort Csum(byte[] d, int o, int n) {
			return CsumFold(CsumAcc(0, d, o, n));
		}
		static string TcpCcSelfCheck() {
			VpnTcp t = new VpnTcp();
			if (t.cwnd != VpnTcp.Mss * VpnTcp.IwSegs) return "cc-iw";
			if (t.ssthresh != VpnTcp.CwndMax) return "cc-ssthresh";
			if (t.FlightCap() != t.cwnd) return "cc-cap";
			t.peerWnd = 20000;
			if (t.FlightCap() != 20000) return "cc-peer";
			t.OnNewAck(100000);
			if (t.cwnd != VpnTcp.Mss * VpnTcp.IwSegs + 2 * VpnTcp.Mss) return "cc-ss";
			t.ssthresh = t.cwnd;
			int c0 = t.cwnd;
			t.OnNewAck(VpnTcp.Mss);
			int inc = (VpnTcp.Mss * VpnTcp.Mss) / c0;
			if (inc < 1) inc = 1;
			if (t.cwnd != c0 + inc) return "cc-ca";
			VpnTcp r = new VpnTcp();
			r.NoteRtt(1);
			if (r.rtoMs != 50 || r.rtoCur != 50) return "cc-floor";
			r.NoteRtt(80);
			if (r.srttMs < 1 || r.rtoMs < 50 || r.rtoMs > 3000) return "cc-rto";
			VpnTcp g = new VpnTcp();
			g.cwnd = 5 * 1024 * 1024;
			g.ssthresh = VpnTcp.CwndMax;
			g.OnNewAck(VpnTcp.Mss);
			if (g.cwnd != 5 * 1024 * 1024 + VpnTcp.Mss) return "cc-grow";
			g.cwnd = VpnTcp.CwndMax - 100;
			g.OnNewAck(VpnTcp.Mss);
			if (g.cwnd != VpnTcp.CwndMax) return "cc-max";
			VpnTcp fr = new VpnTcp();
			fr.state = 3;
			fr.sndUna = 1000;
			fr.sndNxt = 1040;
			for (int i = 0; i < 4; i++) {
				VpnSeg s = new VpnSeg();
				s.data = new byte[10];
				s.seq = (uint)(1000 + i * 10);
				s.sent = DateTime.UtcNow;
				s.sends = 1;
				fr.flight.Add(s);
			}
			fr.lastRawWnd = 1000;
			fr.OnCumulativeAck(1000, true);
			fr.OnCumulativeAck(1000, true);
			fr.OnCumulativeAck(1000, true);
			if (!fr.fastRec || fr.rexmit == null || fr.rexmit.seq != 1000) return "cc-fr-enter";
			if (fr.ssthresh != 2 * VpnTcp.Mss) return "cc-fr-ss";
			if (fr.cwnd != fr.ssthresh + 3 * VpnTcp.Mss) return "cc-fr-cwnd";
			fr.OnCumulativeAck(1020, true);
			if (!fr.fastRec || fr.rexmit == null || fr.rexmit.seq != 1020) return "cc-fr-partial";
			fr.OnCumulativeAck(1040, true);
			if (fr.fastRec || fr.flight.Count != 0) return "cc-fr-exit";
			if (fr.cwnd != fr.ssthresh) return "cc-fr-deflate";
			VpnTcp to = new VpnTcp();
			to.state = 3;
			to.sndUna = 5;
			to.sndNxt = 15;
			to.rtoCur = 250;
			VpnSeg ts = new VpnSeg();
			ts.data = new byte[10];
			ts.seq = 5;
			ts.sends = 1;
			ts.sent = DateTime.UtcNow.AddMilliseconds(-400);
			to.flight.Add(ts);
			if (!to.OnRetransmitTimeout(DateTime.UtcNow)) return "cc-to";
			if (to.cwnd != VpnTcp.Mss || to.fastRec || to.rexmit == null || to.rexmit.sends != 2) return "cc-to-cut";
			int ss = to.ssthresh;
			int r1 = to.rtoCur;
			if (!to.OnRetransmitTimeout(DateTime.UtcNow.AddSeconds(30))) return "cc-to2";
			if (to.ssthresh != ss || to.rtoCur <= r1 || to.cwnd != VpnTcp.Mss) return "cc-to-hold";
			byte[] opt = new byte[28];
			opt[20] = 2; opt[21] = 4; opt[22] = 0x05; opt[23] = 0x78;
			opt[24] = 1; opt[25] = 3; opt[26] = 3; opt[27] = 7;
			if (ParseMss(opt, 0, 28) != 1400) return "mss";
			if (ParseWScale(opt, 0, 28) != 7) return "wscale";
			if (SegMss() < 536 || SegMss() > 1440) return "seg-mss";
			byte[] pkt = new byte[64];
			int tn = FillTcp(pkt, 0x0a000001, 1000, 0x0a000002, 80, 1, 0, 0x10, new byte[] { 1, 2, 3, 4 });
			uint sum = (0x0a000001u >> 16) + (0x0a000001u & 0xffff);
			sum += (0x0a000002u >> 16) + (0x0a000002u & 0xffff);
			sum += 6u + (uint)(tn - 20);
			sum = CsumAcc(sum, pkt, 20, tn - 20);
			if (CsumFold(sum) != 0) return "tcp-csum";
			int un = FillUdp(pkt, 0x0a000001, 1000, 0x0a000002, 53, new byte[] { 9, 8, 7 });
			uint us = (0x0a000001u >> 16) + (0x0a000001u & 0xffff);
			us += (0x0a000002u >> 16) + (0x0a000002u & 0xffff);
			us += 17u + (uint)(un - 20);
			us = CsumAcc(us, pkt, 20, un - 20);
			if (CsumFold(us) != 0) return "udp-csum";
			return null;
		}
		static byte[] Tai() {
			byte[] t = new byte[12];
			DateTime n = DateTime.UtcNow;
			ulong sec = (ulong)((n - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds) + 0x400000000000000aUL;
			uint nano = (uint)n.Millisecond * 1000000U;
			for (int i = 0; i < 8; i++) t[i] = (byte)(sec >> (56 - 8 * i));
			t[8] = (byte)(nano >> 24); t[9] = (byte)(nano >> 16); t[10] = (byte)(nano >> 8); t[11] = (byte)nano;
			return t;
		}
		static byte[] Cat(byte[] a, byte[] b) {
			byte[] c = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, c, 0, a.Length); Buffer.BlockCopy(b, 0, c, a.Length, b.Length); return c;
		}
	}

	internal static class Dht {
		sealed class Node {
			public byte[] Id;
			public IPEndPoint Ep;
			public DateTime Last;
			public int Fail;
		}
		sealed class Ann {
			public byte[] Ip;
			public int Port;
			public DateTime At;
		}
		sealed class Pend {
			public IPEndPoint Ep;
			public DateTime Deadline;
			public Job Job;
			public byte[] Ih;
		}
		public sealed class Job {
			public byte[] Ih;
			public int Port;
			public Action<string> OnPeer;
			public Action<int> OnDone;
			public DateTime Next;
			public int Found;
			public int Announced;
			public bool Busy;
			public bool Retry;
		}
		sealed class Pkt {
			public IPEndPoint From;
			public byte[] Data;
		}

		static readonly object gate = new object();
		static readonly List<Node> nodes = new List<Node>();
		static readonly Dictionary<string, List<Ann>> store = new Dictionary<string, List<Ann>>();
		static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
		static readonly Dictionary<string, Pend> pending = new Dictionary<string, Pend>();
		static readonly Queue<Pkt> inbox = new Queue<Pkt>();
		static byte[] id = new byte[20];
		static byte[] sec0 = new byte[16];
		static byte[] sec1 = new byte[16];
		static DateTime secAt = DateTime.MinValue;
		static Thread thr;
		static volatile bool run;
		static UdpClient ownOs;
		static VpnUdpSock ownVpn;
		static int ownPort = -1;
		static bool ownIsVpn;
		static string voteIp = "";
		static int voteN;
		static string appliedIp = "";

		public static void OnPacket(byte[] pkt, IPEndPoint from) {
			if (!run || pkt == null || from == null || pkt.Length < 10 || pkt[0] != (byte)'d') return;
			Pkt p = new Pkt();
			p.From = from;
			p.Data = pkt;
			lock (inbox) {
				inbox.Enqueue(p);
				while (inbox.Count > 200) inbox.Dequeue();
			}
		}

		public static void Want(byte[] ih, int port, Action<string> onPeer, Action<int> onDone) {
			if (ih == null || ih.Length != 20) return;
			Ensure();
			string key = Hex(ih);
			lock (gate) {
				Job j;
				if (!jobs.TryGetValue(key, out j)) {
					j = new Job();
					j.Ih = (byte[])ih.Clone();
					j.Next = DateTime.UtcNow.AddSeconds(2 * jobs.Count);
					jobs[key] = j;
				}
				if (port > 0) j.Port = port;
				if (onPeer != null) j.OnPeer = onPeer;
				if (onDone != null) j.OnDone = onDone;
			}
		}

		public static void Unwant(byte[] ih) {
			if (ih == null || ih.Length != 20) return;
			lock (gate) jobs.Remove(Hex(ih));
		}

		public static void Attach(int port) {
			Ensure();
			bool vpn = VpnHub.TunnelOn;
			if (VpnHub.Blocked || port <= 0) { CloseOwn(); return; }
			if (Session.Utp != null) { CloseOwn(); return; }
			if (ownPort == port && ownIsVpn == vpn && (ownVpn != null || ownOs != null)) return;
			CloseOwn();
			try {
				if (vpn) ownVpn = VpnHub.BindUdp(port);
				else ownOs = new UdpClient(port);
				ownPort = port;
				ownIsVpn = vpn;
			} catch {
				ownPort = -1;
			}
		}

		public static string SelfCheck() {
			byte[] nine = Encoding.ASCII.GetBytes("123456789");
			uint c = Crc32c(nine, nine.Length);
			if (c != 0xE3069283u) return "crc32c";
			IPAddress ip = IPAddress.Parse("1.2.3.4");
			byte[] ih = new byte[20];
			ih[19] = 9;
			byte[] sec = Encoding.ASCII.GetBytes("0123456789abcdef");
			byte[] t1 = MakeToken(ip, ih, sec);
			byte[] t2 = MakeToken(ip, ih, sec);
			if (t1 == null || t1.Length != 4 || !TokenEq(t1, t2)) return "token";
			t2[0] ^= 1;
			if (TokenEq(t1, t2)) return "token-flip";
			byte[] target = new byte[20];
			target[0] = 0x10;
			byte[] far = new byte[20];
			byte[] near = new byte[20];
			near[0] = 0x10;
			if (XorCmp(near, far, target) >= 0) return "xor";
			return null;
		}

		static void Ensure() {
			lock (gate) {
				if (thr != null) return;
				RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
				rng.GetBytes(id);
				rng.GetBytes(sec0);
				rng.GetBytes(sec1);
				rng.Dispose();
				secAt = DateTime.UtcNow;
				run = true;
				thr = new Thread(Loop);
				thr.IsBackground = true;
				thr.Name = "pt-dht";
				thr.Start();
			}
		}

		static void CloseOwn() {
			try { if (ownOs != null) ownOs.Close(); } catch { }
			try { if (ownVpn != null) VpnHub.DropUdp(ownVpn); } catch { }
			ownOs = null;
			ownVpn = null;
			ownPort = -1;
		}

		static void Loop() {
			string[] boots = new string[] {
				"router.bittorrent.com:6881",
				"dht.transmissionbt.com:6881",
				"router.utorrent.com:6881",
				"dht.libtorrent.org:25401"
			};
			DateTime nextBoot = DateTime.MinValue;
			while (run) {
				try {
					if (VpnHub.Blocked) { Thread.Sleep(400); continue; }
					PumpOnce(40);
					Expire();
					DateTime now = DateTime.UtcNow;
					if ((now - secAt).TotalMinutes >= 5) {
						sec1 = sec0;
						sec0 = new byte[16];
						RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
						rng.GetBytes(sec0);
						rng.Dispose();
						secAt = now;
					}
					int ncount;
					lock (gate) ncount = nodes.Count;
					if (ncount < 8 && now >= nextBoot) {
						Bootstrap(boots);
						nextBoot = now.AddSeconds(20);
					}
					Job job = null;
					lock (gate) {
						foreach (KeyValuePair<string, Job> kv in jobs) {
							if (!kv.Value.Busy && kv.Value.Next <= now) { job = kv.Value; job.Busy = true; break; }
						}
					}
					if (job != null) {
						try { Lookup(job); } catch { }
						int found = job.Found;
						bool retry = job.Retry;
						job.Found = 0;
						job.Retry = false;
						lock (gate) {
							job.Busy = false;
							job.Next = DateTime.UtcNow.AddSeconds(retry ? 8 : 15 * 60);
						}
						if (found > 0 && job.OnDone != null) {
							try { job.OnDone(found); } catch { }
						}
					} else Thread.Sleep(50);
				} catch {
					Thread.Sleep(200);
				}
			}
		}

		static void Bootstrap(string[] boots) {
			for (int i = 0; i < boots.Length; i++) {
				int colon = boots[i].LastIndexOf(':');
				if (colon <= 0) continue;
				int p;
				if (!int.TryParse(boots[i].Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) continue;
				IPAddress ip = Bt.ResolveV4(boots[i].Substring(0, colon));
				if (ip == null) continue;
				byte[] tid = NewTid();
				Dictionary<string, Be> a = new Dictionary<string, Be>();
				a["target"] = Be.Blob((byte[])id.Clone());
				Pend pend = new Pend();
				pend.Ep = new IPEndPoint(ip, p);
				pend.Deadline = DateTime.UtcNow.AddSeconds(4);
				lock (gate) pending[TidKey(tid)] = pend;
				SendTo(EncodeQuery("find_node", a, tid), pend.Ep);
			}
		}

		static void Lookup(Job job) {
			HashSet<string> asked = new HashSet<string>();
			job.Announced = 0;
			if (Closest(job.Ih, 1).Count == 0) {
				DateTime waitNodes = DateTime.UtcNow.AddSeconds(4);
				while (run && DateTime.UtcNow < waitNodes && Closest(job.Ih, 1).Count == 0) PumpOnce(100);
			}
			if (Closest(job.Ih, 1).Count == 0) { job.Retry = true; return; }
			int queried = 0;
			DateTime dead = DateTime.UtcNow.AddSeconds(8);
			while (run && DateTime.UtcNow < dead && queried < 24) {
				List<Node> closest = Closest(job.Ih, 16);
				int sent = 0;
				for (int i = 0; i < closest.Count && sent < 3 && queried < 24; i++) {
					string k = closest[i].Ep.ToString();
					if (!asked.Add(k)) continue;
					byte[] tid = NewTid();
					Dictionary<string, Be> a = new Dictionary<string, Be>();
					a["info_hash"] = Be.Blob(job.Ih);
					Pend pend = new Pend();
					pend.Ep = closest[i].Ep;
					pend.Deadline = DateTime.UtcNow.AddSeconds(3);
					pend.Job = job;
					pend.Ih = job.Ih;
					lock (gate) pending[TidKey(tid)] = pend;
					SendTo(EncodeQuery("get_peers", a, tid), closest[i].Ep);
					sent++;
					queried++;
				}
				if (sent == 0) break;
				DateTime round = DateTime.UtcNow.AddMilliseconds(1500);
				while (run && DateTime.UtcNow < round) {
					PumpOnce(80);
					if (PendingJob(job) == 0) break;
				}
				Expire();
			}
		}

		static int PendingJob(Job job) {
			int n = 0;
			lock (gate) {
				foreach (KeyValuePair<string, Pend> kv in pending) if (kv.Value.Job == job) n++;
			}
			return n;
		}

		static void PumpOnce(int waitMs) {
			DrainOwn();
			DateTime dead = DateTime.UtcNow.AddMilliseconds(waitMs);
			while (true) {
				Pkt p = null;
				lock (inbox) { if (inbox.Count > 0) p = inbox.Dequeue(); }
				if (p == null) break;
				try { Handle(p.Data, p.From); } catch { }
			}
			int left = (int)(dead - DateTime.UtcNow).TotalMilliseconds;
			if (left > 0) Thread.Sleep(Math.Min(left, 40));
		}

		static void DrainOwn() {
			try {
				if (ownOs != null && ownOs.Client != null && ownOs.Client.Poll(0, SelectMode.SelectRead)) {
					IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
					byte[] pkt = ownOs.Receive(ref ep);
					OnPacket(pkt, ep);
				}
			} catch { }
			try {
				if (ownVpn != null) {
					byte[] d;
					IPEndPoint f;
					if (ownVpn.Recv(0, out d, out f) && d != null && f != null) OnPacket(d, f);
				}
			} catch { }
		}

		static void Expire() {
			List<Pend> dead = new List<Pend>();
			List<string> keys = new List<string>();
			DateTime now = DateTime.UtcNow;
			lock (gate) {
				foreach (KeyValuePair<string, Pend> kv in pending) {
					if (kv.Value.Deadline >= now) continue;
					keys.Add(kv.Key);
					dead.Add(kv.Value);
				}
				for (int i = 0; i < keys.Count; i++) pending.Remove(keys[i]);
				for (int i = 0; i < dead.Count; i++) NoteFail(dead[i].Ep);
			}
		}

		static void NoteFail(IPEndPoint ep) {
			if (ep == null) return;
			for (int i = nodes.Count - 1; i >= 0; i--) {
				if (!nodes[i].Ep.Address.Equals(ep.Address) || nodes[i].Ep.Port != ep.Port) continue;
				nodes[i].Fail++;
				if (nodes[i].Fail >= 3) nodes.RemoveAt(i);
				return;
			}
		}

		static void Handle(byte[] pkt, IPEndPoint from) {
			if (VpnHub.Blocked) return;
			Be root = Benc.Decode(pkt);
			if (root == null || root.Dict == null) return;
			string y = root.Str("y");
			Be t = root.Get("t");
			if (t == null || t.Bytes == null || t.Bytes.Length == 0 || t.Bytes.Length > 16) return;
			Be ipf = root.Get("ip");
			if (ipf != null && ipf.Bytes != null) NoteExternal(ipf.Bytes);
			if (y == "r") {
				Pend pend;
				lock (gate) {
					string k = TidKey(t.Bytes);
					if (!pending.TryGetValue(k, out pend)) return;
					pending.Remove(k);
				}
				TakeReply(root.Get("r"), from, pend);
				return;
			}
			if (y != "q") return;
			string q = root.Str("q");
			Be a = root.Get("a");
			if (a == null) return;
			byte[] nid = ArgBytes(a, "id", 20);
			if (nid != null) RememberNode(nid, from, true);
			if (q == "ping") {
				Reply(t.Bytes, new Dictionary<string, Be>(), from);
			} else if (q == "find_node") {
				byte[] target = ArgBytes(a, "target", 20);
				if (target == null) { ReplyErr(t.Bytes, 203, "missing target", from); return; }
				Dictionary<string, Be> r = new Dictionary<string, Be>();
				r["nodes"] = Be.Blob(NodesBlob(target));
				Reply(t.Bytes, r, from);
			} else if (q == "get_peers") {
				byte[] ih = ArgBytes(a, "info_hash", 20);
				if (ih == null) { ReplyErr(t.Bytes, 203, "missing info_hash", from); return; }
				Dictionary<string, Be> r = new Dictionary<string, Be>();
				r["nodes"] = Be.Blob(NodesBlob(ih));
				r["token"] = Be.Blob(MakeToken(from.Address, ih, sec0));
				List<Be> vals = PeerValues(ih);
				if (vals.Count > 0) r["values"] = Be.FromList(vals);
				Reply(t.Bytes, r, from);
			} else if (q == "announce_peer") {
				byte[] ih = ArgBytes(a, "info_hash", 20);
				Be tok = a.Get("token");
				if (ih == null || tok == null || tok.Bytes == null) { ReplyErr(t.Bytes, 203, "missing token", from); return; }
				if (!TokenOk(from.Address, ih, tok.Bytes)) { ReplyErr(t.Bytes, 203, "invalid token", from); return; }
				int port = (int)a.GetInt("port", 0);
				if (a.GetInt("implied_port", 0) != 0) port = from.Port;
				if (port <= 0 || port > 65535) { ReplyErr(t.Bytes, 203, "invalid port", from); return; }
				if (from.Address.AddressFamily == AddressFamily.InterNetwork)
					StorePeer(ih, from.Address.GetAddressBytes(), port);
				Reply(t.Bytes, new Dictionary<string, Be>(), from);
			} else {
				byte[] target = ArgBytes(a, "target", 20);
				if (target == null) target = ArgBytes(a, "info_hash", 20);
				if (target == null) { ReplyErr(t.Bytes, 204, "unknown method", from); return; }
				Dictionary<string, Be> r = new Dictionary<string, Be>();
				r["nodes"] = Be.Blob(NodesBlob(target));
				Reply(t.Bytes, r, from);
			}
		}

		static void TakeReply(Be r, IPEndPoint from, Pend pend) {
			if (r == null) return;
			byte[] nid = ArgBytes(r, "id", 20);
			if (nid != null) RememberNode(nid, from, true);
			Be nb = r.Get("nodes");
			if (nb != null && nb.Bytes != null) IngestNodes(nb.Bytes);
			if (pend == null || pend.Job == null || pend.Ih == null) return;
			Be tok = r.Get("token");
			if (tok != null && tok.Bytes != null && tok.Bytes.Length > 0 && tok.Bytes.Length <= 64 && pend.Job.Port > 0 && pend.Job.Announced < 8) {
				pend.Job.Announced++;
				Dictionary<string, Be> a = new Dictionary<string, Be>();
				a["info_hash"] = Be.Blob(pend.Ih);
				a["port"] = Be.Int(pend.Job.Port);
				a["token"] = Be.Blob(tok.Bytes);
				byte[] tid = NewTid();
				try { SendTo(EncodeQuery("announce_peer", a, tid), from); } catch { }
			}
			Be values = r.Get("values");
			if (values == null || values.List == null) return;
			int added = 0;
			for (int i = 0; i < values.List.Count; i++) {
				byte[] v = values.List[i].Bytes;
				if (v == null) continue;
				List<string> tmp = new List<string>();
				if (v.Length == 6) Bt.AddCompactPeers(v, tmp);
				else if (v.Length == 18) Bt.AddCompact6(v, tmp);
				else if (v.Length > 6 && v.Length % 6 == 0) Bt.AddCompactPeers(v, tmp);
				for (int k = 0; k < tmp.Count; k++) {
					if (pend.Job.OnPeer != null) {
						try { pend.Job.OnPeer(tmp[k]); } catch { }
					}
					added++;
					StoreEndpoint(pend.Ih, tmp[k]);
				}
			}
			if (added > 0) pend.Job.Found += added;
		}

		static void StoreEndpoint(byte[] ih, string ep) {
			int colon = ep.LastIndexOf(':');
			if (colon <= 0) return;
			string h = ep.Substring(0, colon);
			if (h.Length >= 2 && h[0] == '[') h = h.Substring(1, h.Length - 2);
			int p;
			if (!int.TryParse(ep.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) return;
			IPAddress ip;
			if (!IPAddress.TryParse(h, out ip)) return;
			if (ip.AddressFamily != AddressFamily.InterNetwork) return;
			StorePeer(ih, ip.GetAddressBytes(), p);
		}

		static void IngestNodes(byte[] raw) {
			if (raw == null) return;
			int n = raw.Length / 26;
			for (int i = 0; i < n; i++) {
				int o = i * 26;
				byte[] nid = new byte[20];
				Buffer.BlockCopy(raw, o, nid, 0, 20);
				if (raw[o + 20] == 0 || raw[o + 20] == 127) continue;
				int p = (raw[o + 24] << 8) | raw[o + 25];
				if (p <= 0) continue;
				IPAddress ip = new IPAddress(new byte[] { raw[o + 20], raw[o + 21], raw[o + 22], raw[o + 23] });
				RememberNode(nid, new IPEndPoint(ip, p), false);
			}
		}

		static void RememberNode(byte[] nid, IPEndPoint ep, bool heard) {
			if (nid == null || nid.Length != 20 || ep == null || ep.Port <= 0 || ep.Address == null) return;
			if (ep.Address.AddressFamily != AddressFamily.InterNetwork) return;
			byte[] raw = ep.Address.GetAddressBytes();
			if (raw.Length != 4 || raw[0] == 0 || raw[0] == 127 || raw[0] == 255) return;
			lock (gate) {
				for (int i = 0; i < nodes.Count; i++) {
					if (nodes[i].Ep.Address.Equals(ep.Address) && nodes[i].Ep.Port == ep.Port) {
						nodes[i].Id = (byte[])nid.Clone();
						nodes[i].Last = DateTime.UtcNow;
						if (heard) nodes[i].Fail = 0;
						return;
					}
				}
				Node n = new Node();
				n.Id = (byte[])nid.Clone();
				n.Ep = ep;
				n.Last = DateTime.UtcNow;
				if (nodes.Count < 128) { nodes.Add(n); return; }
				int worst = 0;
				for (int i = 1; i < nodes.Count; i++) {
					if (nodes[i].Fail > nodes[worst].Fail) worst = i;
					else if (nodes[i].Fail == nodes[worst].Fail && nodes[i].Last < nodes[worst].Last) worst = i;
				}
				if (nodes[worst].Fail > 0 || (DateTime.UtcNow - nodes[worst].Last).TotalMinutes > 15)
					nodes[worst] = n;
			}
		}

		static List<Node> Closest(byte[] target, int k) {
			List<Node> all = new List<Node>();
			lock (gate) {
				for (int i = 0; i < nodes.Count; i++) all.Add(nodes[i]);
			}
			all.Sort(delegate(Node x, Node y) { return XorCmp(x.Id, y.Id, target); });
			if (all.Count > k) all.RemoveRange(k, all.Count - k);
			return all;
		}

		static byte[] NodesBlob(byte[] target) {
			List<Node> c = Closest(target, 8);
			List<byte> buf = new List<byte>();
			for (int i = 0; i < c.Count; i++) {
				if (c[i].Id == null || c[i].Id.Length != 20 || c[i].Ep == null) continue;
				byte[] raw = c[i].Ep.Address.GetAddressBytes();
				if (raw.Length != 4) continue;
				for (int k = 0; k < 20; k++) buf.Add(c[i].Id[k]);
				for (int k = 0; k < 4; k++) buf.Add(raw[k]);
				buf.Add((byte)(c[i].Ep.Port >> 8));
				buf.Add((byte)c[i].Ep.Port);
			}
			return buf.ToArray();
		}

		static List<Be> PeerValues(byte[] ih) {
			List<Be> vals = new List<Be>();
			List<Ann> list = null;
			lock (gate) {
				List<Ann> got;
				if (store.TryGetValue(Hex(ih), out got)) list = new List<Ann>(got);
			}
			if (list == null) return vals;
			DateTime now = DateTime.UtcNow;
			for (int i = 0; i < list.Count && vals.Count < 16; i++) {
				if ((now - list[i].At).TotalMinutes > 30) continue;
				if (list[i].Ip == null || list[i].Ip.Length != 4) continue;
				byte[] c = new byte[6];
				Buffer.BlockCopy(list[i].Ip, 0, c, 0, 4);
				c[4] = (byte)(list[i].Port >> 8);
				c[5] = (byte)list[i].Port;
				vals.Add(Be.Blob(c));
			}
			return vals;
		}

		static void StorePeer(byte[] ih, byte[] ip, int port) {
			if (ih == null || ip == null || ip.Length != 4 || port <= 0) return;
			string key = Hex(ih);
			lock (gate) {
				List<Ann> list;
				if (!store.TryGetValue(key, out list)) {
					if (store.Count > 200) {
						string drop = null;
						foreach (KeyValuePair<string, List<Ann>> kv in store) { drop = kv.Key; break; }
						if (drop != null) store.Remove(drop);
					}
					list = new List<Ann>();
					store[key] = list;
				}
				for (int i = list.Count - 1; i >= 0; i--) {
					if ((DateTime.UtcNow - list[i].At).TotalMinutes > 30) list.RemoveAt(i);
				}
				for (int i = 0; i < list.Count; i++) {
					if (list[i].Port == port && list[i].Ip != null && list[i].Ip.Length == 4
						&& list[i].Ip[0] == ip[0] && list[i].Ip[1] == ip[1] && list[i].Ip[2] == ip[2] && list[i].Ip[3] == ip[3]) {
						list[i].At = DateTime.UtcNow;
						return;
					}
				}
				if (list.Count >= 30) list.RemoveAt(0);
				Ann a = new Ann();
				a.Ip = (byte[])ip.Clone();
				a.Port = port;
				a.At = DateTime.UtcNow;
				list.Add(a);
			}
		}

		static void Reply(byte[] tid, Dictionary<string, Be> r, IPEndPoint to) {
			byte[] mine = id;
			r["id"] = Be.Blob((byte[])mine.Clone());
			Dictionary<string, Be> root = new Dictionary<string, Be>();
			root["r"] = Be.FromDict(r);
			root["t"] = Be.Blob(tid);
			root["y"] = Be.Blob(Encoding.ASCII.GetBytes("r"));
			SendTo(Benc.Encode(Be.FromDict(root)), to);
		}

		static void ReplyErr(byte[] tid, int code, string msg, IPEndPoint to) {
			List<Be> e = new List<Be>();
			e.Add(Be.Int(code));
			e.Add(Be.Blob(Encoding.UTF8.GetBytes(msg)));
			Dictionary<string, Be> root = new Dictionary<string, Be>();
			root["e"] = Be.FromList(e);
			root["t"] = Be.Blob(tid);
			root["y"] = Be.Blob(Encoding.ASCII.GetBytes("e"));
			SendTo(Benc.Encode(Be.FromDict(root)), to);
		}

		static byte[] EncodeQuery(string qname, Dictionary<string, Be> a, byte[] tid) {
			byte[] mine = id;
			a["id"] = Be.Blob((byte[])mine.Clone());
			Dictionary<string, Be> root = new Dictionary<string, Be>();
			root["a"] = Be.FromDict(a);
			root["q"] = Be.Blob(Encoding.ASCII.GetBytes(qname));
			root["t"] = Be.Blob(tid);
			root["y"] = Be.Blob(Encoding.ASCII.GetBytes("q"));
			return Benc.Encode(Be.FromDict(root));
		}

		static void SendTo(byte[] msg, IPEndPoint dest) {
			if (msg == null || dest == null || VpnHub.Blocked) return;
			try {
				UtpHub h = Session.Utp;
				if (h != null) { h.SendRaw(msg, dest); return; }
				if (ownVpn != null) { ownVpn.SendTo(msg, dest); return; }
				if (ownOs != null) ownOs.Send(msg, msg.Length, dest);
			} catch { }
		}

		static byte[] ArgBytes(Be a, string key, int n) {
			if (a == null) return null;
			Be v = a.Get(key);
			if (v == null || v.Bytes == null || v.Bytes.Length != n) return null;
			return v.Bytes;
		}

		static byte[] NewTid() {
			byte[] t = new byte[2];
			RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
			rng.GetBytes(t);
			rng.Dispose();
			return t;
		}

		static string TidKey(byte[] t) { return BitConverter.ToString(t); }
		static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

		static void NoteExternal(byte[] compact) {
			if (compact == null || compact.Length < 4) return;
			string s = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", compact[0], compact[1], compact[2], compact[3]);
			if (s == appliedIp) return;
			if (s == voteIp) voteN++;
			else { voteIp = s; voteN = 1; }
			if (voteN < 2) return;
			IPAddress addr = IPAddress.Parse(s);
			uint crc = Crc32c(addr.GetAddressBytes(), 4);
			byte[] nid = new byte[20];
			byte[] cur = id;
			nid[0] = (byte)(crc >> 24);
			nid[1] = (byte)(crc >> 16);
			nid[2] = (byte)(((crc >> 8) & 0xF8u) | (uint)(cur[2] & 7));
			Buffer.BlockCopy(cur, 3, nid, 3, 17);
			id = nid;
			appliedIp = s;
		}

		public static uint Crc32c(byte[] data, int len) {
			uint crc = 0xFFFFFFFFu;
			int n = data == null ? 0 : data.Length;
			if (len < n) n = len;
			for (int i = 0; i < n; i++) {
				crc ^= data[i];
				for (int b = 0; b < 8; b++) {
					if ((crc & 1) != 0) crc = (crc >> 1) ^ 0x82F63B78u;
					else crc >>= 1;
				}
			}
			return ~crc;
		}

		static byte[] MakeToken(IPAddress addr, byte[] ih, byte[] secret) {
			byte[] ipb = addr.GetAddressBytes();
			byte[] buf = new byte[ipb.Length + secret.Length + ih.Length];
			Buffer.BlockCopy(ipb, 0, buf, 0, ipb.Length);
			Buffer.BlockCopy(secret, 0, buf, ipb.Length, secret.Length);
			Buffer.BlockCopy(ih, 0, buf, ipb.Length + secret.Length, ih.Length);
			byte[] h;
			using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) h = sha.ComputeHash(buf);
			byte[] t = new byte[4];
			Buffer.BlockCopy(h, 0, t, 0, 4);
			return t;
		}

		static bool TokenOk(IPAddress addr, byte[] ih, byte[] tok) {
			return TokenEq(tok, MakeToken(addr, ih, sec0)) || TokenEq(tok, MakeToken(addr, ih, sec1));
		}

		static bool TokenEq(byte[] a, byte[] b) {
			if (a == null || b == null || a.Length != b.Length) return false;
			int d = 0;
			for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
			return d == 0;
		}

		static int XorCmp(byte[] x, byte[] y, byte[] target) {
			for (int i = 0; i < 20; i++) {
				int dx = (x[i] ^ target[i]) & 0xff;
				int dy = (y[i] ^ target[i]) & 0xff;
				if (dx != dy) return dx - dy;
			}
			return 0;
		}
	}

	public static class Session {
		public const int MaxTorrents = 100;
		public const int GlobalMaxPeers = 500;
		static readonly object gate = new object();
		static readonly List<Engine> engines = new List<Engine>();
		static TcpListener listener;
		static Thread acceptThread;
		static UtpHub utpHub;
		static volatile bool listenRun;
		static bool vpnBound;
		static int boundPort;
		static int peerThreads;
		static readonly Semaphore hashSem = new Semaphore(2, 2);
		static readonly Semaphore announceSem = new Semaphore(16, 16);

		static Session() {
			try {
				int w, io;
				ThreadPool.GetMinThreads(out w, out io);
				if (w < 32) ThreadPool.SetMinThreads(32, Math.Max(io, 16));
			} catch { }
		}

		public static int TorrentCount { get { lock (gate) return engines.Count; } }
		public static int BoundPort { get { lock (gate) return boundPort; } }
		internal static UtpHub Utp { get { return utpHub; } }

		public static bool Register(Engine e) {
			if (e == null) return false;
			lock (gate) {
				if (engines.Count >= MaxTorrents) return false;
				byte[] h = e.InfoHashBytes;
				if (h != null && h.Length == 20) {
					for (int i = 0; i < engines.Count; i++) {
						byte[] oh = engines[i].InfoHashBytes;
						if (oh == null || oh.Length != 20) continue;
						int k = 0;
						for (; k < 20; k++) if (oh[k] != h[k]) break;
						if (k == 20) return false;
					}
				}
				engines.Add(e);
				return true;
			}
		}

		public static void Unregister(Engine e) {
			lock (gate) {
				engines.Remove(e);
				if (engines.Count == 0) StopListen_NoLock();
			}
		}

		public static Engine FindByHash(byte[] hash) {
			if (hash == null || hash.Length != 20) return null;
			lock (gate) {
				for (int i = 0; i < engines.Count; i++) {
					byte[] h = engines[i].InfoHashBytes;
					if (h == null || h.Length != 20) continue;
					int k = 0;
					for (; k < 20; k++) if (h[k] != hash[k]) break;
					if (k == 20) return engines[i];
				}
			}
			return null;
		}

		public static byte[][] AllInfoHashes() {
			lock (gate) {
				List<byte[]> list = new List<byte[]>(engines.Count);
				for (int i = 0; i < engines.Count; i++) {
					byte[] h = engines[i].InfoHashBytes;
					if (h != null && h.Length == 20) list.Add(h);
				}
				return list.ToArray();
			}
		}

		public static bool AnyEncrypt() {
			lock (gate) {
				for (int i = 0; i < engines.Count; i++)
					if (engines[i].Settings != null && engines[i].Settings.EnableEncrypt) return true;
			}
			return false;
		}

		public static int IndexOf(Engine e) {
			lock (gate) return engines.IndexOf(e);
		}

		public static int PeerLimit(Engine e, int want) {
			if (want <= 0) want = 80;
			int n;
			lock (gate) n = engines.Count;
			if (n < 1) n = 1;
			int share = Math.Max(6, GlobalMaxPeers / n);
			return Math.Min(want, share);
		}

		// libtorrent unchoke_slots_limit is 8 for the session. Spread them across running torrents.
		public static int UnchokeSlots(Engine e) {
			int n = 0;
			int idx = -1;
			lock (gate) {
				for (int i = 0; i < engines.Count; i++) {
					Engine en = engines[i];
					if (en == null || !en.KeepGoing || en.IsPaused) continue;
					if (en == e) idx = n;
					n++;
				}
			}
			if (n < 1) n = 1;
			if (idx < 0) idx = 0;
			int share = (8 / n) + (idx < (8 % n) ? 1 : 0);
			if (share < 1) share = 1;
			return share;
		}

		public static bool TryBeginPeer() {
			lock (gate) {
				if (peerThreads >= GlobalMaxPeers) return false;
				peerThreads++;
				return true;
			}
		}

		public static void EndPeer() {
			lock (gate) {
				if (peerThreads > 0) peerThreads--;
			}
		}

		public static bool AcquireHash(Engine e) {
			while (e != null && e.KeepGoing) {
				if (hashSem.WaitOne(200)) return true;
			}
			return false;
		}
		public static void ReleaseHash() {
			try { hashSem.Release(); } catch { }
		}

		public static void AcquireAnnounce() {
			try { announceSem.WaitOne(); } catch { }
		}
		public static void ReleaseAnnounce() {
			try { announceSem.Release(); } catch { }
		}

		public static void ResetTransport() {
			Engine[] snap;
			lock (gate) snap = engines.ToArray();
			for (int i = 0; i < snap.Length; i++) {
				try { snap[i].DropPeers(); } catch { }
			}
			RebindListen();
		}

		public static void RebindListen() {
			int port = 6881;
			bool utp = false;
			lock (gate) {
				if (boundPort > 0) port = boundPort;
				for (int i = 0; i < engines.Count; i++) {
					if (engines[i].Settings == null) continue;
					if (engines[i].Settings.EnableUtp) utp = true;
					if (boundPort <= 0 && engines[i].Settings.ListenPort > 0) port = engines[i].Settings.ListenPort;
				}
				if (engines.Count == 0) { StopListen_NoLock(); try { Dht.Attach(0); } catch { } return; }
			}
			int bound = EnsureListen(port, utp);
			try { Dht.Attach(bound); } catch { }
		}

		public static int EnsureListen(int port, bool enableUtp) {
			lock (gate) {
				if (VpnHub.Blocked || (!VpnHub.TunnelOn && !VpnHub.OsAllowed)) { StopListen_NoLock(); return 0; }
				if (port <= 0) port = 6881;
				if (VpnHub.TunnelOn) {
					if (vpnBound && boundPort > 0) {
						if (enableUtp && utpHub == null) {
							utpHub = new UtpHub();
							if (!utpHub.Start(boundPort)) utpHub = null;
						}
						return boundPort;
					}
					StopListen_NoLock();
					VpnHub.ListenTcp(port);
					vpnBound = true;
					boundPort = port;
					listenRun = true;
					acceptThread = new Thread(AcceptLoopVpn);
					acceptThread.IsBackground = true;
					acceptThread.Name = "pt-accept-wg";
					acceptThread.Start();
					if (enableUtp) {
						utpHub = new UtpHub();
						if (!utpHub.Start(boundPort)) utpHub = null;
					}
					return boundPort;
				}
				if (!VpnHub.OsAllowed) { StopListen_NoLock(); return 0; }
				if (vpnBound) StopListen_NoLock();
				if (listener != null) {
					if (enableUtp && utpHub == null && boundPort > 0) {
						utpHub = new UtpHub();
						if (!utpHub.Start(boundPort)) utpHub = null;
					}
					return boundPort;
				}
				for (int i = 0; i < 10; i++) {
					try {
						TcpListener l = new TcpListener(IPAddress.Any, port + i);
						l.Start();
						listener = l;
						boundPort = ((IPEndPoint)l.LocalEndpoint).Port;
						listenRun = true;
						acceptThread = new Thread(AcceptLoop);
						acceptThread.IsBackground = true;
						acceptThread.Name = "pt-accept";
						acceptThread.Start();
						if (enableUtp) {
							utpHub = new UtpHub();
							if (!utpHub.Start(boundPort)) utpHub = null;
						}
						return boundPort;
					} catch {
						listener = null;
					}
				}
				boundPort = port;
				return 0;
			}
		}

		static void StopListen_NoLock() {
			listenRun = false;
			try { if (listener != null) listener.Stop(); } catch { }
			listener = null;
			try { VpnHub.ListenTcp(-1); } catch { }
			vpnBound = false;
			try { if (utpHub != null) utpHub.Stop(); } catch { }
			utpHub = null;
			boundPort = 0;
		}

		static void AcceptLoop() {
			while (listenRun && listener != null) {
				try {
					if (!listener.Server.Poll(500000, SelectMode.SelectRead)) continue;
					TcpClient c = listener.AcceptTcpClient();
					if (!TryBeginPeer()) {
						try { c.Close(); } catch { }
						continue;
					}
					Thread t = new Thread(delegate(object state) {
						try { DispatchTcp((TcpClient)state); }
						finally { EndPeer(); }
					});
					t.IsBackground = true;
					t.Name = "pt-in";
					t.Start(c);
				} catch {
					if (!listenRun) break;
				}
			}
		}

		static void AcceptLoopVpn() {
			while (listenRun) {
				try {
					VpnTcp v = VpnHub.AcceptTcp(500);
					if (v == null) continue;
					if (!TryBeginPeer()) {
						try { v.Close(); } catch { }
						continue;
					}
					Thread t = new Thread(delegate(object state) {
						try { DispatchVpnTcp((VpnTcp)state); }
						finally { EndPeer(); }
					});
					t.IsBackground = true;
					t.Name = "pt-in-wg";
					t.Start(v);
				} catch {
					if (!listenRun) break;
				}
			}
		}

		static void DispatchVpnTcp(VpnTcp v) {
			TcpPeerIo io = null;
			try {
				io = new TcpPeerIo(v);
				Engine e;
				if (!io.AcceptRouted(AnyEncrypt(), out e) || e == null) {
					io.Close();
					return;
				}
				if (!e.IsRunning || e.IsPaused) {
					io.Close();
					return;
				}
				int lim = PeerLimit(e, e.Settings.MaxPeers);
				if (e.ActivePeerThreads >= lim) {
					io.Close();
					return;
				}
				e.RunIncoming(io);
			} catch {
				try { if (io != null) io.Close(); else v.Close(); } catch { }
			}
		}

		static void DispatchTcp(TcpClient c) {
			TcpPeerIo io = null;
			try {
				io = new TcpPeerIo(c);
				Engine e;
				if (!io.AcceptRouted(AnyEncrypt(), out e) || e == null) {
					io.Close();
					return;
				}
				if (!e.IsRunning || e.IsPaused) {
					io.Close();
					return;
				}
				int lim = PeerLimit(e, e.Settings.MaxPeers);
				if (e.ActivePeerThreads >= lim) {
					io.Close();
					return;
				}
				e.RunIncoming(io);
			} catch {
				try { if (io != null) io.Close(); else c.Close(); } catch { }
			}
		}

		internal static void DispatchUtp(UtpConn c) {
			if (!TryBeginPeer()) {
				try { c.Close(); } catch { }
				return;
			}
			Thread t = new Thread(delegate() {
				try { DispatchUtpCore(c); }
				finally { EndPeer(); }
			});
			t.IsBackground = true;
			t.Name = "pt-utp-in";
			t.Start();
		}

		static void DispatchUtpCore(UtpConn c) {
			UtpPeerIo io = new UtpPeerIo(c);
			try {
				byte[] hs = new byte[68];
				if (!io.ReadExact(hs, 0, 68, 15000) || hs[0] != 19) {
					io.Close();
					return;
				}
				byte[] ih = new byte[20];
				Buffer.BlockCopy(hs, 28, ih, 0, 20);
				Engine e = FindByHash(ih);
				if (e == null || !e.IsRunning || e.IsPaused) {
					io.Close();
					return;
				}
				if (e.ActivePeerThreads >= PeerLimit(e, e.Settings.MaxPeers)) {
					io.Close();
					return;
				}
				io.Unread(hs, 0, 68);
				e.RunIncoming(io);
			} catch {
				try { io.Close(); } catch { }
			}
		}
	}

	public sealed class Engine {
		EngineSettings settings;
		EngineStatus status = new EngineStatus();
		Meta meta;
		PieceMgr pieces;
		byte[] infoHash;
		byte[] peerId;
		volatile bool running;
		volatile bool paused;
		object peerLock = new object();
		List<PeerWorker> peers = new List<PeerWorker>();
		object poolLock = new object();
		Queue<string> pool = new Queue<string>();
		HashSet<string> seen = new HashSet<string>();
		object logLock = new object();
		List<string> logs = new List<string>();
		int logLevel;
		int boundPort;
		long sessionDown;
		long sessionUp;
		double downBps;
		double upBps;
		long lastDl;
		long lastUl;
		DateTime lastSp = DateTime.UtcNow;
		double[] spdWin = new double[30];
		int spdN;
		int spdI;
		double spdSum;
		double etaSmooth = -1;
		int idleTicks;
		double activeSecs;
		DateTime lastRecycle = DateTime.MinValue;
		int activePeerThreads;
		Thread coordThread;
		Thread dhtThread;
		bool sessionReg;
		int announceInterval = 1800;
		int annBusy;
		DateTime nextRechoke = DateTime.MinValue;
		DateTime lastRechoke = DateTime.MinValue;
		DateTime optSince = DateTime.MinValue;
		PeerWorker optPeer;
		int rechokeNudge;
		bool toldSeeding;
		DateTime lastResumeSave = DateTime.MinValue;
		DateTime nextPex = DateTime.MinValue;
		Dictionary<string, byte> pexHave = new Dictionary<string, byte>();
		byte[] pexFull;
		byte[] pexDiff;
		bool pexDiffEmpty = true;
		List<Trk> trk = new List<Trk>();
		bool trkReady;
		object trkLock = new object();
		DateTime lastAnnScan = DateTime.MinValue;
		bool startedAnnounced;
		int trackerSeeds;
		object statusLock = new object();
		byte[] rawInfo;
		byte[] fetchedInfo;
		volatile bool infoReady;
		object metaLock = new object();
		int mdSize = -1;
		byte[][] mdPieces;
		bool[] mdHave;
		int mdGot;
		List<string> magnetTrackers = new List<string>();
		List<string> magnetWebseeds = new List<string>();
		byte[] v2Hash;
		bool v2Only;
		public int AfterHash;
		bool hashedThisRun;

		sealed class Trk {
			public string Url;
			public DateTime Next;
			public int Fail;
			public int Interval = 1800;
		}
		sealed class AnnOut {
			public bool Ok;
			public int Interval;
		}

		sealed class FileEnt {
			public string Path;
			public string RelPath;
			public long Length;
			public long Offset;
			public int Index;
			public byte[] PiecesRoot;
			public byte[] LayerHashes;
		}

		sealed class Meta {
			public string Name;
			public string Comment;
			public string CreatedBy;
			public byte[] InfoHash;
			public string InfoHashHex;
			public int PieceLength;
			public int PieceCount;
			public long TotalSize;
			public byte[] PieceHashes;
			public List<string> Trackers = new List<string>();
			public List<string> Webseeds = new List<string>();
			public List<FileEnt> Files = new List<FileEnt>();
			public bool IsMulti;
			public byte[] RawInfo;
			public bool V2;
			public bool Private;
			public byte[] V2Hash;
			public List<List<string>> Tiers = new List<List<string>>();
			public Dictionary<string, byte[]> PieceLayers = new Dictionary<string, byte[]>();

			public static Meta Stub(string name, byte[] hash, List<string> trackers, List<string> webseeds) {
				Meta m = new Meta();
				m.InfoHash = hash;
				m.InfoHashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
				if (string.IsNullOrEmpty(name)) name = "magnet-" + m.InfoHashHex.Substring(0, 12);
				m.Name = Bt.SafeName(name);
				if (trackers != null) {
					HashSet<string> seenT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					for (int i = 0; i < trackers.Count; i++) AddTracker(m, seenT, trackers[i]);
					if (m.Trackers.Count > 0) {
						List<string> tier = new List<string>(m.Trackers);
						Shuffle(tier);
						m.Tiers.Add(tier);
					}
				}
				if (webseeds != null) {
					for (int i = 0; i < webseeds.Count; i++) AddWeb(m, webseeds[i]);
				}
				return m;
			}

			public static Meta FromInfoBytes(byte[] rawInfo, string savePath) {
				Be info = Benc.Decode(rawInfo);
				if (info == null || info.Dict == null) throw new Exception("invalid info dict");
				Meta m = new Meta();
				m.RawInfo = rawInfo;
				FillInfo(m, info, savePath);
				if (m.V2 && m.PieceHashes == null) {
					byte[] h256 = Crypto.Sha256All(rawInfo);
					m.V2Hash = h256;
					m.InfoHash = new byte[20];
					Buffer.BlockCopy(h256, 0, m.InfoHash, 0, 20);
				} else {
					using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
						m.InfoHash = sha.ComputeHash(rawInfo);
					}
				}
				m.InfoHashHex = BitConverter.ToString(m.InfoHash).Replace("-", "").ToLowerInvariant();
				return m;
			}

			public static Meta Load(byte[] torrentBytes, string savePath) {
				Be root = Benc.Decode(torrentBytes);
				if (root.Dict == null) throw new Exception("torrent is not a dictionary");
				Be info = root.Get("info");
				if (info == null || info.Dict == null) throw new Exception("torrent missing info dict");

				byte[] rawInfo = Benc.SliceDictValue(torrentBytes, "info");
				if (rawInfo == null) rawInfo = Benc.Encode(info);

				Meta m = new Meta();
				m.RawInfo = rawInfo;
				FillInfo(m, info, savePath);
				if (m.V2 && m.PieceHashes == null) {
					byte[] h256 = Crypto.Sha256All(rawInfo);
					m.V2Hash = h256;
					m.InfoHash = new byte[20];
					Buffer.BlockCopy(h256, 0, m.InfoHash, 0, 20);
				} else {
					using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
						m.InfoHash = sha.ComputeHash(rawInfo);
					}
					if (m.V2) m.V2Hash = Crypto.Sha256All(rawInfo);
				}
				m.InfoHashHex = BitConverter.ToString(m.InfoHash).Replace("-", "").ToLowerInvariant();
				Be layers = root.Get("piece layers");
				if (layers != null && layers.Pairs != null) {
					for (int i = 0; i < layers.Pairs.Count; i++) {
						byte[] k = layers.Pairs[i].Key;
						Be v = layers.Pairs[i].Value;
						if (k != null && k.Length == 32 && v != null && v.Bytes != null)
							m.PieceLayers[BitConverter.ToString(k).Replace("-", "")] = v.Bytes;
					}
				}
				ApplyLayers(m);
				m.Comment = root.Str("comment") ?? "";
				m.CreatedBy = root.Str("created by") ?? "";

				HashSet<string> seenT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string ann = root.Str("announce");
				Be al = root.Get("announce-list");
				if (al != null && al.List != null) {
					for (int t = 0; t < al.List.Count; t++) {
						Be tier = al.List[t];
						List<string> urls = new List<string>();
						if (tier.List != null) {
							for (int u = 0; u < tier.List.Count; u++) {
								if (tier.List[u].Bytes == null) continue;
								string uurl = Encoding.UTF8.GetString(tier.List[u].Bytes).Trim();
								if (uurl.Length == 0) continue;
								bool dup = false;
								for (int k = 0; k < urls.Count; k++) if (string.Equals(urls[k], uurl, StringComparison.OrdinalIgnoreCase)) dup = true;
								if (!dup) urls.Add(uurl);
							}
						} else if (tier.Bytes != null) {
							string uurl = Encoding.UTF8.GetString(tier.Bytes).Trim();
							if (uurl.Length > 0) urls.Add(uurl);
						}
						if (urls.Count == 0) continue;
						Shuffle(urls);
						m.Tiers.Add(urls);
						for (int u = 0; u < urls.Count; u++) AddTracker(m, seenT, urls[u]);
					}
				}
				if (!string.IsNullOrEmpty(ann)) {
					ann = ann.Trim();
					bool have = false;
					for (int t = 0; t < m.Tiers.Count && !have; t++) {
						for (int u = 0; u < m.Tiers[t].Count; u++) {
							if (string.Equals(m.Tiers[t][u], ann, StringComparison.OrdinalIgnoreCase)) have = true;
						}
					}
					if (!have && ann.Length > 0) {
						List<string> one = new List<string>();
						one.Add(ann);
						m.Tiers.Insert(0, one);
					}
					AddTracker(m, seenT, ann);
				}
				if (m.Tiers.Count == 0 && m.Trackers.Count > 0) {
					List<string> one = new List<string>(m.Trackers);
					Shuffle(one);
					m.Tiers.Add(one);
				}

				Be ul = root.Get("url-list");
				if (ul != null) {
					if (ul.Bytes != null) AddWeb(m, Encoding.UTF8.GetString(ul.Bytes));
					else if (ul.List != null) {
						for (int i = 0; i < ul.List.Count; i++) {
							if (ul.List[i].Bytes != null) AddWeb(m, Encoding.UTF8.GetString(ul.List[i].Bytes));
						}
					}
				}
				return m;
			}

			static void FillInfo(Meta m, Be info, string savePath) {
				m.Name = info.Str("name");
				if (string.IsNullOrEmpty(m.Name)) m.Name = "download";
				m.Name = Bt.SafeName(m.Name);

				long pl = info.GetInt("piece length", 0);
				if (pl < 16 || pl > 32L * 1024 * 1024) throw new Exception("invalid piece length");
				m.PieceLength = (int)pl;

				Be piecesBe = info.Get("pieces");
				bool haveV1 = piecesBe != null && piecesBe.Bytes != null && piecesBe.Bytes.Length % 20 == 0 && piecesBe.Bytes.Length > 0;
				if (haveV1) m.PieceHashes = piecesBe.Bytes;
				if (info.GetInt("meta version", 1) >= 2) m.V2 = true;
				if (info.GetInt("private", 0) != 0) m.Private = true;

				string saveRoot = savePath;
				if (string.IsNullOrEmpty(saveRoot)) saveRoot = Directory.GetCurrentDirectory();

				Be tree = info.Get("file tree");
				if (tree != null && tree.Dict != null) {
					m.V2 = true;
					long[] off = new long[1];
					WalkTree(tree, "", saveRoot, m, off);
					m.TotalSize = off[0];
					m.IsMulti = m.Files.Count > 1;
					if (m.IsMulti) {
						for (int fi = 0; fi < m.Files.Count; fi++) {
							m.Files[fi].RelPath = m.Name + "/" + m.Files[fi].RelPath;
							m.Files[fi].Path = Path.Combine(saveRoot, m.Files[fi].RelPath.Replace('/', Path.DirectorySeparatorChar));
						}
					} else if (m.Files.Count == 1) {
						if (string.IsNullOrEmpty(m.Files[0].RelPath)) m.Files[0].RelPath = m.Name;
						m.Files[0].Path = Path.Combine(saveRoot, m.Files[0].RelPath.Replace('/', Path.DirectorySeparatorChar));
					}
				} else {
				Be filesBe = info.Get("files");
				if (filesBe != null && filesBe.List != null) {
					m.IsMulti = true;
					long off = 0;
					for (int fi = 0; fi < filesBe.List.Count; fi++) {
						Be f = filesBe.List[fi];
						if (f.Dict == null) throw new Exception("invalid file entry");
						long len = f.GetInt("length", -1);
						if (len < 0) throw new Exception("file missing length");
						Be pathBe = f.Get("path");
						if (pathBe == null || pathBe.List == null || pathBe.List.Count == 0)
							throw new Exception("file missing path");
						string rel = m.Name;
						string abs = Path.Combine(saveRoot, m.Name);
						for (int p = 0; p < pathBe.List.Count; p++) {
							if (pathBe.List[p].Bytes == null) throw new Exception("invalid path element");
							string part = Bt.SafeName(Encoding.UTF8.GetString(pathBe.List[p].Bytes));
							rel = rel + "/" + part;
							abs = Path.Combine(abs, part);
						}
						FileEnt e = new FileEnt();
						e.Path = abs;
						e.RelPath = rel;
						e.Length = len;
						e.Offset = off;
						e.Index = fi;
						m.Files.Add(e);
						off += len;
					}
					m.TotalSize = off;
				} else {
					m.IsMulti = false;
					long len = info.GetInt("length", -1);
					if (len < 0) throw new Exception("torrent missing length");
					FileEnt e = new FileEnt();
					e.Path = Path.Combine(saveRoot, m.Name);
					e.RelPath = m.Name;
					e.Length = len;
					e.Offset = 0;
					e.Index = 0;
					m.Files.Add(e);
					m.TotalSize = len;
				}
				}

				if (m.TotalSize == 0) m.PieceCount = 0;
				else m.PieceCount = (int)((m.TotalSize + m.PieceLength - 1) / m.PieceLength);
				if (m.PieceHashes != null && m.PieceHashes.Length != m.PieceCount * 20)
					throw new Exception("pieces length does not match file size");
				if (m.PieceHashes == null && !m.V2)
					throw new Exception("invalid pieces field");
				ApplyLayers(m);
			}

			static void WalkTree(Be node, string relBase, string saveRoot, Meta m, long[] off) {
				if (node == null || node.Dict == null) return;
				Be leaf = node.Get("");
				if (leaf != null && leaf.Dict != null) {
					long len = leaf.GetInt("length", 0);
					Be pr = leaf.Get("pieces root");
					FileEnt e = new FileEnt();
					e.RelPath = relBase.Replace('\\', '/');
					e.Path = Path.Combine(saveRoot, e.RelPath.Replace('/', Path.DirectorySeparatorChar));
					e.Length = len;
					e.Offset = off[0];
					e.Index = m.Files.Count;
					if (pr != null) e.PiecesRoot = pr.Bytes;
					m.Files.Add(e);
					off[0] += len;
					return;
				}
				foreach (KeyValuePair<string, Be> kv in node.Dict) {
					if (kv.Key == "") continue;
					string child = relBase.Length == 0 ? Bt.SafeName(kv.Key) : relBase + "/" + Bt.SafeName(kv.Key);
					WalkTree(kv.Value, child, saveRoot, m, off);
				}
			}

			static void ApplyLayers(Meta m) {
				if (m.PieceLayers == null || m.PieceLayers.Count == 0) return;
				for (int i = 0; i < m.Files.Count; i++) {
					FileEnt f = m.Files[i];
					if (f.PiecesRoot == null || f.PiecesRoot.Length != 32) continue;
					string k = BitConverter.ToString(f.PiecesRoot).Replace("-", "");
					byte[] layers;
					if (m.PieceLayers.TryGetValue(k, out layers)) f.LayerHashes = layers;
				}
			}

			static void Shuffle(List<string> xs) {
				Random rng = new Random(Guid.NewGuid().GetHashCode());
				for (int i = xs.Count - 1; i > 0; i--) {
					int j = rng.Next(i + 1);
					string tmp = xs[i];
					xs[i] = xs[j];
					xs[j] = tmp;
				}
			}
			static void AddTracker(Meta m, HashSet<string> seenT, string url) {
				if (string.IsNullOrEmpty(url)) return;
				url = url.Trim();
				if (url.Length == 0) return;
				if (!seenT.Add(url)) return;
				m.Trackers.Add(url);
			}
			static void AddWeb(Meta m, string url) {
				if (string.IsNullOrEmpty(url)) return;
				url = url.Trim();
				if (url.Length == 0) return;
				m.Webseeds.Add(url);
			}
		}

		sealed class PieceMgr {
			Engine eng;
			Meta m;
			FileStream[] streams;
			long[] fileOldLen;
			object gate = new object();
			bool[] done;
			int[] avail;
			byte[][] buf;
			byte[][] st;
			int[][] hits;
			int n;
			int pieceLen;
			long total;
			int doneCount;
			long verified;
			long pending;
			int inFlight;
			int maxInFlight;
			const int BS = 16384;
			List<int> open;
			int[] posInOpen;
			[ThreadStatic] static SHA1CryptoServiceProvider tlsSha1;

			public PieceMgr(Engine eng, Meta m) {
				this.eng = eng;
				this.m = m;
				n = m.PieceCount;
				pieceLen = m.PieceLength;
				total = m.TotalSize;
				done = new bool[Math.Max(n, 0)];
				avail = new int[Math.Max(n, 0)];
				buf = new byte[Math.Max(n, 0)][];
				st = new byte[Math.Max(n, 0)][];
				hits = new int[Math.Max(n, 0)][];
				open = new List<int>(Math.Max(n, 0));
				posInOpen = new int[Math.Max(n, 0)];
				for (int i = 0; i < n; i++) {
					posInOpen[i] = i;
					open.Add(i);
				}
				RefreshBudget();
			}

			public void RefreshBudget() {
				int denom = Math.Max(16384, pieceLen);
				int nT = Math.Max(1, Session.TorrentCount);
				long share = (48L * 1024 * 1024) / nT;
				int byMem = (int)(share / denom);
				int cap = nT > 25 ? 16 : (nT > 10 ? 32 : 96);
				if (VpnHub.TunnelOn) {
					share = (96L * 1024 * 1024) / nT;
					byMem = (int)(share / denom);
					cap = Math.Max(cap, 64);
				}
				maxInFlight = Math.Max(4, Math.Min(cap, Math.Max(4, byMem)));
			}

			public bool IsComplete { get { return n == 0 || doneCount >= n; } }
			public int DoneCount { get { return doneCount; } }
			public bool IsEndgame() {
				lock (gate) {
					return RemainingUnrequested() == 0 && (n - doneCount) > 0;
				}
			}

			bool FinishingNoLock() {
				int left = n - doneCount;
				if (left <= 0) return false;
				if (left <= maxInFlight * 2) return true;
				if (total > 0 && (total - verified) * 20L < total) return true;
				return false;
			}

			public void Open() { Open(true); }
			public void Open(bool create) {
				if (streams != null) Close();
				streams = new FileStream[m.Files.Count];
				fileOldLen = new long[m.Files.Count];
				for (int i = 0; i < m.Files.Count; i++) {
					FileEnt f = m.Files[i];
					long old = 0;
					try { if (File.Exists(f.Path)) old = new FileInfo(f.Path).Length; } catch { }
					fileOldLen[i] = old;
					if (!create && old <= 0) {
						streams[i] = null;
						continue;
					}
					string dir = Path.GetDirectoryName(f.Path);
					if (create && !string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
					FileMode mode = create ? FileMode.OpenOrCreate : FileMode.Open;
					FileAccess acc = create ? FileAccess.ReadWrite : FileAccess.Read;
					try {
						streams[i] = new FileStream(f.Path, mode, acc, FileShare.ReadWrite, 2 * 1024 * 1024, FileOptions.SequentialScan);
						if (create && streams[i].Length != f.Length) streams[i].SetLength(f.Length);
					} catch {
						streams[i] = null;
					}
				}
			}

			public void Close() {
				if (streams == null) return;
				for (int i = 0; i < streams.Length; i++) {
					if (streams[i] != null) {
						try { streams[i].Flush(); streams[i].Close(); } catch { }
						streams[i] = null;
					}
				}
			}

			public int PieceSize(int idx) {
				if (idx < 0 || idx >= n) return 0;
				if (idx < n - 1) return pieceLen;
				long rem = total - (long)idx * (long)pieceLen;
				return (int)rem;
			}

			int BlockCount(int idx) {
				int sz = PieceSize(idx);
				if (sz <= 0) return 0;
				return (sz + BS - 1) / BS;
			}

			int BlockLen(int idx, int b) {
				int sz = PieceSize(idx);
				int off = b * BS;
				int left = sz - off;
				if (left <= 0) return 0;
				return left > BS ? BS : left;
			}
			public int BlockLength(int piece, int begin) {
				if (piece < 0 || piece >= n) return 16384;
				int b = begin / BS;
				int nbyte = BlockLen(piece, b);
				return nbyte > 0 ? nbyte : 16384;
			}

			void IO(long offset, byte[] buffer, int bufOff, int len, bool write) {
				int remain = len;
				for (int fi = 0; fi < m.Files.Count && remain > 0; fi++) {
					FileEnt f = m.Files[fi];
					long fe = f.Offset + f.Length;
					if (offset >= fe || offset < f.Offset) continue;
					long local = offset - f.Offset;
					int nbyte = (int)Math.Min((long)remain, f.Length - local);
					FileStream s = streams[f.Index];
					if (s == null) throw new IOException("missing");
					lock (s) {
						s.Seek(local, SeekOrigin.Begin);
						if (write) {
							s.Write(buffer, bufOff, nbyte);
						} else {
							int need = nbyte;
							int o = bufOff;
							while (need > 0) {
								int g = s.Read(buffer, o, need);
								if (g <= 0) throw new IOException("short read");
								need -= g;
								o += g;
							}
						}
					}
					offset += nbyte;
					bufOff += nbyte;
					remain -= nbyte;
				}
				if (remain != 0) throw new IOException("io span");
			}

			public void HashCheck() {
				if (n == 0) return;
				if (!Session.AcquireHash(eng)) return;
				try {
				RefreshBudget();
				int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
				int nT = Session.TorrentCount;
				if (nT > 8) workers = 1;
				else if (nT > 3) workers = Math.Min(2, workers);
				if (n < 24) workers = 1;
				if (workers == 1) {
					byte[] tmp = new byte[pieceLen];
					for (int i = 0; i < n && eng.running; i++) {
						if (!eng.running) return;
						HashOne(i, tmp);
						if (i == 0 || (i & 15) == 0) eng.OnHashProgress(i + 1, n);
					}
				} else {
					int next = 0;
					int hashed = 0;
					object progLock = new object();
					Thread[] th = new Thread[workers];
					for (int w = 0; w < workers; w++) {
						th[w] = new Thread(delegate() {
							byte[] tmp = new byte[pieceLen];
							while (eng.running) {
								int i;
								lock (progLock) {
									if (next >= n) return;
									i = next++;
								}
								if (!eng.running) return;
								HashOne(i, tmp);
								int h = Interlocked.Increment(ref hashed);
								if (h == 1 || (h & 15) == 0) eng.OnHashProgress(h, n);
							}
						});
						th[w].IsBackground = true;
						th[w].Name = "pt-hash";
						th[w].Start();
					}
					for (int w = 0; w < workers; w++) {
						try { th[w].Join(); } catch { }
					}
				}
				RebuildOpen();
				eng.OnHashProgress(n, n);
				} finally {
					Session.ReleaseHash();
				}
			}

			bool PieceMayHaveData(int idx) {
				if (fileOldLen == null) return true;
				long start = (long)idx * (long)pieceLen;
				int sz = PieceSize(idx);
				long end = start + sz;
				for (int fi = 0; fi < m.Files.Count; fi++) {
					FileEnt f = m.Files[fi];
					long fe = f.Offset + f.Length;
					if (end <= f.Offset || start >= fe) continue;
					long localStart = start > f.Offset ? start - f.Offset : 0;
					if (fileOldLen[fi] > localStart) return true;
				}
				return false;
			}

			void HashOne(int i, byte[] tmp) {
				if (done[i]) return;
				if (!PieceMayHaveData(i)) return;
				int sz = PieceSize(i);
				bool readOk = true;
				try { IO((long)i * (long)pieceLen, tmp, 0, sz, false); }
				catch { readOk = false; }
				if (readOk && VerifyPieceData(i, tmp, sz)) {
					done[i] = true;
					Interlocked.Increment(ref doneCount);
					Interlocked.Add(ref verified, sz);
				}
			}

			void RebuildOpen() {
				open.Clear();
				for (int i = 0; i < n; i++) {
					if (!done[i]) {
						posInOpen[i] = open.Count;
						open.Add(i);
					} else posInOpen[i] = -1;
				}
			}

			public void ImportBitfield(byte[] bf) {
				lock (gate) {
					doneCount = 0;
					verified = 0;
					for (int i = 0; i < n; i++) {
						bool have = false;
						if (bf != null) {
							int bi = i / 8;
							if (bi < bf.Length) have = (bf[bi] & (1 << (7 - (i % 8)))) != 0;
						}
						done[i] = have;
						if (have) {
							doneCount++;
							verified += PieceSize(i);
						}
					}
					RebuildOpen();
				}
			}

			public void DropPiecesTouching(int fileIndex) {
				if (m == null || fileIndex < 0 || fileIndex >= m.Files.Count) return;
				FileEnt f = m.Files[fileIndex];
				if (f.Length <= 0 || pieceLen <= 0) return;
				long start = f.Offset;
				long end = start + f.Length;
				int p0 = (int)(start / pieceLen);
				int p1 = (int)((end - 1) / pieceLen);
				if (p0 < 0) p0 = 0;
				if (p1 >= n) p1 = n - 1;
				lock (gate) {
					for (int p = p0; p <= p1; p++) {
						if (!done[p]) continue;
						done[p] = false;
						doneCount--;
						verified -= PieceSize(p);
					}
					if (doneCount < 0) doneCount = 0;
					if (verified < 0) verified = 0;
					RebuildOpen();
				}
			}

			void RemoveOpen(int piece) {
				if (piece < 0 || piece >= n) return;
				int at = posInOpen[piece];
				if (at < 0) return;
				int lastIdx = open.Count - 1;
				int last = open[lastIdx];
				open[at] = last;
				posInOpen[last] = at;
				open.RemoveAt(lastIdx);
				posInOpen[piece] = -1;
			}

			bool VerifyPieceData(int piece, byte[] data, int sz) {
				if (m.PieceHashes != null && m.PieceHashes.Length >= (piece + 1) * 20) {
					byte[] h;
					SHA1CryptoServiceProvider sha = tlsSha1;
					if (sha == null) {
						sha = new SHA1CryptoServiceProvider();
						tlsSha1 = sha;
					}
					h = sha.ComputeHash(data, 0, sz);
					int ho = piece * 20;
					for (int k = 0; k < 20; k++) if (h[k] != m.PieceHashes[ho + k]) return false;
					return true;
				}
				if (m.V2) {
					byte[] layer = Crypto.MerklePiece(data, sz);
					long off = (long)piece * (long)pieceLen;
					for (int fi = 0; fi < m.Files.Count; fi++) {
						FileEnt f = m.Files[fi];
						if (off < f.Offset || off >= f.Offset + f.Length) continue;
						if (f.LayerHashes != null && f.LayerHashes.Length >= 32) {
							int local = (int)((off - f.Offset) / pieceLen);
							int lo = local * 32;
							if (lo + 32 > f.LayerHashes.Length) return false;
							for (int k = 0; k < 32; k++) if (layer[k] != f.LayerHashes[lo + k]) return false;
							return true;
						}
						if (f.PiecesRoot != null && f.Length <= pieceLen) {
							for (int k = 0; k < 32; k++) if (layer[k] != f.PiecesRoot[k]) return false;
							return true;
						}
						return false;
					}
					return false;
				}
				return false;
			}

			static bool PeerHolds(List<int> haveP, List<int> haveB, int p, int begin) {
				if (haveP == null || haveB == null) return false;
				for (int i = 0; i < haveP.Count; i++) {
					if (haveP[i] == p && haveB[i] == begin) return true;
				}
				return false;
			}

			bool HasFreeBlock(int idx, bool endgame, bool uniqueOnly, List<int> haveP, List<int> haveB) {
				int bc = BlockCount(idx);
				if (st[idx] == null) return true;
				for (int b = 0; b < bc; b++) {
					if (st[idx][b] == 0) return true;
					if (!uniqueOnly && endgame && st[idx][b] == 1 && hits[idx] != null && hits[idx][b] < 3
						&& !PeerHolds(haveP, haveB, idx, b * BS)) return true;
				}
				return false;
			}

			int RemainingUnrequested() {
				int c = 0;
				int cnt = open.Count;
				for (int k = 0; k < cnt; k++) {
					int i = open[k];
					int bc = BlockCount(i);
					if (st[i] == null) { c += bc; continue; }
					for (int b = 0; b < bc; b++) if (st[i][b] == 0) c++;
				}
				return c;
			}

			int Pick(BitArray has, bool sequential, bool endgame, bool uniqueOnly, List<int> haveP, List<int> haveB) {
				int best = -1;
				int bestScore = int.MaxValue;
				int cnt = open.Count;
				for (int k = 0; k < cnt; k++) {
					int i = open[k];
					if (has != null && (i >= has.Length || !has[i])) continue;
					if (!HasFreeBlock(i, endgame, uniqueOnly, haveP, haveB)) continue;
					if (st[i] == null && !FinishingNoLock() && inFlight >= maxInFlight && (n - doneCount) > maxInFlight) continue;
					if (sequential) return i;
					int score = (st[i] != null) ? avail[i] - 100000 : avail[i];
					if (score < bestScore) { bestScore = score; best = i; }
				}
				return best;
			}

			public bool TryClaim(BitArray has, bool sequential, bool endgame, out int piece, out int begin, out int length) {
				return TryClaim(has, sequential, endgame, false, null, null, out piece, out begin, out length);
			}

			public bool TryClaim(BitArray has, bool sequential, bool endgame, bool uniqueOnly, List<int> haveP, List<int> haveB, out int piece, out int begin, out int length) {
				piece = -1;
				begin = 0;
				length = 0;
				lock (gate) {
					if (endgame && !uniqueOnly && RemainingUnrequested() > 0) return false;
					int pick = Pick(has, sequential, endgame, uniqueOnly, haveP, haveB);
					if (pick < 0) return false;
					int bc = BlockCount(pick);
					if (st[pick] == null) {
						st[pick] = new byte[bc];
						hits[pick] = new int[bc];
						buf[pick] = new byte[PieceSize(pick)];
						inFlight++;
					}
					for (int b = 0; b < bc; b++) {
						if (st[pick][b] == 0 && !PeerHolds(haveP, haveB, pick, b * BS)) {
							st[pick][b] = 1;
							hits[pick][b] = 1;
							piece = pick;
							begin = b * BS;
							length = BlockLen(pick, b);
							return true;
						}
					}
					if (endgame && !uniqueOnly) {
						int best = -1, bestH = int.MaxValue;
						for (int b = 0; b < bc; b++) {
							if (st[pick][b] != 1 || hits[pick] == null) continue;
							if (PeerHolds(haveP, haveB, pick, b * BS)) continue;
							if (hits[pick][b] < bestH) { bestH = hits[pick][b]; best = b; }
						}
						if (best >= 0 && bestH < 3) {
							hits[pick][best]++;
							piece = pick;
							begin = best * BS;
							length = BlockLen(pick, best);
							return true;
						}
					}
					return false;
				}
			}

			public void Unclaim(int piece, int begin) {
				lock (gate) {
					if (piece < 0 || piece >= n || st[piece] == null) return;
					int b = begin / BS;
					if (b < 0 || b >= st[piece].Length || st[piece][b] != 1) return;
					if (hits[piece] != null && hits[piece][b] > 0) hits[piece][b]--;
					if (hits[piece] == null || hits[piece][b] <= 0) st[piece][b] = 0;
				}
			}

			public bool BlockDone(int piece, int begin) {
				lock (gate) {
					if (piece < 0 || piece >= n) return true;
					if (done[piece]) return true;
					if (st[piece] == null) return false;
					int b = begin / BS;
					if (b < 0 || b >= st[piece].Length) return true;
					return st[piece][b] == 2;
				}
			}

			void AbortPiece(int piece, int sz) {
				pending -= sz;
				if (pending < 0) pending = 0;
				st[piece] = null;
				hits[piece] = null;
				buf[piece] = null;
				inFlight--;
			}

			// 0 = rejected, 1 = block stored, 2 = piece verified and written
			public int Submit(int piece, int begin, byte[] data, int off, int len) {
				byte[] pieceData = null;
				int sz = 0;
				lock (gate) {
					if (piece < 0 || piece >= n || done[piece]) return 0;
					if (st[piece] == null || buf[piece] == null) return 0;
					int b = begin / BS;
					if (b < 0 || b >= st[piece].Length) return 0;
					int expect = BlockLen(piece, b);
					if (len != expect) return 0;
					if (st[piece][b] == 2) return 0;
					Buffer.BlockCopy(data, off, buf[piece], begin, len);
					st[piece][b] = 2;
					pending += len;
					eng.AddSessionDown(len);
					int bc = st[piece].Length;
					for (int k = 0; k < bc; k++) if (st[piece][k] != 2) return 1;
					sz = PieceSize(piece);
					pieceData = buf[piece];
					buf[piece] = null;
				}
				bool ok = VerifyPieceData(piece, pieceData, sz);
				bool sole = true;
				lock (gate) {
					if (hits[piece] != null) {
						for (int b = 0; b < hits[piece].Length; b++) if (hits[piece][b] != 1) { sole = false; break; }
					}
				}
				if (!ok) {
					eng.Log(1, "piece " + piece.ToString(CultureInfo.InvariantCulture) + " hash mismatch, retrying");
					lock (gate) AbortPiece(piece, sz);
					return sole ? -1 : 0;
				}
				try {
					IO((long)piece * (long)pieceLen, pieceData, 0, sz, true);
				} catch (Exception ex) {
					eng.Log(0, "write failed: " + ex.Message);
					lock (gate) AbortPiece(piece, sz);
					return 0;
				}
				lock (gate) {
					if (done[piece]) return 2;
					done[piece] = true;
					doneCount++;
					verified += sz;
					pending -= sz;
					if (pending < 0) pending = 0;
					st[piece] = null;
					hits[piece] = null;
					inFlight--;
					RemoveOpen(piece);
				}
				return 2;
			}

			public bool Has(int piece) {
				if (piece < 0 || piece >= n) return false;
				return done[piece];
			}

			public byte[] ReadBlock(int piece, int begin, int length) {
				lock (gate) {
					if (!Has(piece)) return null;
					int sz = PieceSize(piece);
					if (begin < 0 || length <= 0 || begin + (long)length > sz) return null;
				}
				byte[] data = new byte[length];
				try { IO((long)piece * (long)pieceLen + begin, data, 0, length, false); }
				catch { return null; }
				return data;
			}

			public double Availability() {
				lock (gate) {
					int cnt = open.Count;
					if (cnt == 0) return 0;
					int min = int.MaxValue;
					int atMin = 0;
					for (int k = 0; k < cnt; k++) {
						int a = avail[open[k]];
						if (a < min) { min = a; atMin = 1; }
						else if (a == min) atMin++;
					}
					if (min == int.MaxValue) return 0;
					return min + (double)(cnt - atMin) / (double)cnt;
				}
			}

			public void AddAvail(BitArray has) {
				lock (gate) {
					if (has == null) return;
					int lim = Math.Min(n, has.Length);
					for (int i = 0; i < lim; i++) if (has[i]) avail[i]++;
				}
			}
			public void SubAvail(BitArray has) {
				lock (gate) {
					if (has == null) return;
					int lim = Math.Min(n, has.Length);
					for (int i = 0; i < lim; i++) if (has[i] && avail[i] > 0) avail[i]--;
				}
			}
			public void AddAvailOne(int idx) {
				lock (gate) {
					if (idx >= 0 && idx < n) avail[idx]++;
				}
			}

			public bool PeerHasSomethingWeNeed(BitArray has) {
				if (has == null) return false;
				int lim = Math.Min(n, has.Length);
				for (int i = 0; i < lim; i++) if (!done[i] && has[i]) return true;
				return false;
			}

			public int RemainingBlocks() {
				lock (gate) {
					int c = 0;
					int cnt = open.Count;
					for (int k = 0; k < cnt; k++) {
						int i = open[k];
						int bc = BlockCount(i);
						if (st[i] == null) { c += bc; continue; }
						for (int b = 0; b < bc; b++) if (st[i][b] != 2) c++;
					}
					return c;
				}
			}

			public byte[] MakeBitfield() {
				int blen = (n + 7) / 8;
				byte[] bf = new byte[blen];
				for (int i = 0; i < n; i++) {
					if (done[i]) bf[i / 8] |= (byte)(1 << (7 - (i % 8)));
				}
				return bf;
			}

			public void Snapshot(out int dc, out int tot, out long ver, out long totb) {
				dc = doneCount;
				tot = n;
				ver = verified + pending;
				totb = total;
			}

			public void FillFileProgress(FileRow[] rows) {
				if (rows == null || m == null || m.Files == null) return;
				int nf = rows.Length;
				if (nf > m.Files.Count) nf = m.Files.Count;
				lock (gate) {
					for (int fi = 0; fi < nf; fi++) {
						FileRow row = rows[fi];
						if (row == null) continue;
						FileEnt f = m.Files[fi];
						long len = f.Length;
						if (len <= 0) {
							row.Progress = 100;
							row.ProgressText = "100.0%";
							continue;
						}
						if (n <= 0) {
							row.Progress = 0;
							row.ProgressText = "0.0%";
							continue;
						}
						long got = 0;
						long start = f.Offset;
						long end = start + len;
						int p0 = (int)(start / pieceLen);
						int p1 = (int)((end - 1) / pieceLen);
						if (p0 < 0) p0 = 0;
						if (p1 >= n) p1 = n - 1;
						for (int p = p0; p <= p1; p++) {
							long ps = (long)p * (long)pieceLen;
							int psz = PieceSize(p);
							long pe = ps + psz;
							if (done[p]) {
								long a = start > ps ? start : ps;
								long b = end < pe ? end : pe;
								if (b > a) got += b - a;
								continue;
							}
							if (st[p] == null) continue;
							int bc = st[p].Length;
							for (int bi = 0; bi < bc; bi++) {
								if (st[p][bi] != 2) continue;
								long bs = ps + (long)bi * BS;
								int bl = BlockLen(p, bi);
								long be = bs + bl;
								long a = start > bs ? start : bs;
								long b = end < be ? end : be;
								if (b > a) got += b - a;
							}
						}
						if (got > len) got = len;
						double pct = 100.0 * got / len;
						row.Progress = pct;
						row.ProgressText = pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
					}
				}
			}
		}

		sealed class PeerWorker {
			Engine eng;
			string host;
			int port;
			TcpClient tcp;
			PeerIo io;
			object wlock = new object();
			object claimLock = new object();
			bool incoming;
			bool amChoked = true;
			BitArray their;
			List<int> claimsPiece = new List<int>();
			List<int> claimsBegin = new List<int>();
			List<DateTime> claimAt = new List<DateTime>();
			DateTime lastRecv;
			DateTime lastSend;
			bool addedAvail;
			bool theyExt;
			int theirUtMeta;
			int theirUtPex;
			byte[] pendingBitfield;
			List<int> pendingHaves = new List<int>();
			int lastMetaIdx = -1;
			DateTime lastMetaAt = DateTime.MinValue;
			int theyHave;
			bool theySeed;
			long peerGot;
			long peerGotLast;
			DateTime peerRateAt = DateTime.UtcNow;
			double peerBps;
			int snubHits;
			bool snubbed;
			bool amChoking = true;
			bool theyInterested;
			int theirListenPort;
			int strikes;
			long peerSent;
			long peerSentLast;
			DateTime peerUpAt = DateTime.UtcNow;
			double peerUpBps;
			IPAddress pexAddr;
			bool pexInitialSent;
			DateTime lastPexAt = DateTime.MinValue;
			DateTime lastPexRx = DateTime.MinValue;
			public bool IsSeed { get { return theySeed; } }
			public bool Interested { get { return theyInterested; } }
			public bool Choking { get { return amChoking; } }
			public double DownRate { get { return peerBps; } }
			public double UpRate { get { return peerUpBps; } }

			void NotePex(IPEndPoint ep) {
				if (ep == null || ep.Address == null) return;
				host = ep.Address.ToString();
				port = ep.Port;
				pexAddr = ep.Address;
			}

			public PeerWorker(Engine eng, PeerIo ready) {
				this.eng = eng;
				this.io = ready;
				incoming = true;
				host = "incoming";
				port = 0;
				if (ready != null) NotePex(ready.Remote);
			}
			public PeerWorker(Engine eng, string host, int port) {
				this.eng = eng;
				this.host = host;
				this.port = port;
				incoming = false;
				IPAddress ip;
				if (IPAddress.TryParse(host, out ip)) pexAddr = ip;
			}
			public PeerWorker(Engine eng, TcpClient c) {
				this.eng = eng;
				this.tcp = c;
				incoming = true;
				try {
					IPEndPoint ep = (IPEndPoint)c.Client.RemoteEndPoint;
					host = ep.Address.ToString();
					port = ep.Port;
				} catch {
					host = "incoming";
					port = 0;
				}
			}
			public PeerWorker(Engine eng, UtpConn uc) {
				this.eng = eng;
				this.io = new UtpPeerIo(uc);
				incoming = true;
				host = uc.Remote.Address.ToString();
				port = uc.Remote.Port;
			}

			public void Run() {
				try {
					if (!Connect()) {
						eng.Log(2, "connect failed " + host + ":" + port.ToString(CultureInfo.InvariantCulture));
						return;
					}
					string via = (io != null) ? io.Transport : "TCP";
					eng.Log(1, "connected " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + " via " + via);
					lock (eng.peerLock) eng.peers.Add(this);
					if (theyExt) SendExtHandshake();
					if (eng.pieces != null) {
						SendBitfield();
						SendInterested();
					}
					eng.NudgeRechoke();
					lastRecv = DateTime.UtcNow;
					byte[] lenBuf = new byte[4];
					while (eng.running && !eng.paused) {
						bool readable = false;
						try {
							readable = io.PollRead(VpnHub.TunnelOn ? 15000 : 50000);
							if (!readable && io.Available > 0) readable = true;
						} catch { break; }
						if (!readable) {
							Pump();
							if ((DateTime.UtcNow - lastRecv).TotalSeconds > 180) break;
							continue;
						}
						if (io.Available == 0) {
							if (!io.PollRead(0) || io.Available == 0) break;
						}
						if (!ReadExact(lenBuf, 0, 4, 20000)) break;
						int mlen = Bt.R32(lenBuf, 0);
						if (mlen == 0) { lastRecv = DateTime.UtcNow; continue; }
						if (mlen < 0 || mlen > 262144) break;
						byte[] msg = new byte[mlen];
						if (!ReadExact(msg, 0, mlen, 20000)) break;
						lastRecv = DateTime.UtcNow;
						Handle(msg);
						Pump();
					}
				} catch (Exception ex) {
					eng.Log(3, "peer " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + " " + ex.Message);
				} finally {
					Cleanup();
				}
			}

			public void Kill() {
				try { if (io != null) io.Close(); } catch { }
				try { if (tcp != null) tcp.Close(); } catch { }
			}

			public void SendHavePublic(int piece) {
				try { SendHave(piece); } catch { }
			}

			public void OnEngineHasInfo() {
				try {
					their = new BitArray(Math.Max(eng.meta.PieceCount, 0));
					theyHave = 0;
					theySeed = false;
					if (pendingBitfield != null) ApplyBitfield(pendingBitfield);
					pendingBitfield = null;
					for (int i = 0; i < pendingHaves.Count; i++) {
						int idx = pendingHaves[i];
						if (idx >= 0 && idx < their.Length && !their[idx]) {
							their[idx] = true;
							theyHave++;
							if (eng.pieces != null) eng.pieces.AddAvailOne(idx);
						}
					}
					if (eng.meta != null && eng.meta.PieceCount > 0 && theyHave >= eng.meta.PieceCount) theySeed = true;
					pendingHaves.Clear();
					SendBitfield();
					SendInterested();
					eng.NudgeRechoke();
					if (theyExt) SendExtHandshake();
				} catch { }
			}

			bool Connect() {
				if (VpnHub.Blocked) return false;
				if (io != null) return Handshake();
				if (incoming && tcp != null) {
					TcpPeerIo tio = new TcpPeerIo(tcp);
					if (!tio.AcceptMseOrPlain(eng.infoHash, eng.settings.EnableEncrypt)) return false;
					io = tio;
					NotePex(tio.Remote);
					return Handshake();
				}
				object dialGate = new object();
				TcpPeerIo tcpWin = null;
				bool tcpFinished = false;
				int pick = 0;
				Thread th = new Thread(delegate() {
					TcpPeerIo d = TcpPeerIo.Dial(host, port, 4000);
					lock (dialGate) {
						tcpFinished = true;
						if (d == null) return;
						if (pick == 2) { try { d.Close(); } catch { } return; }
						tcpWin = d;
					}
				});
				th.IsBackground = true;
				th.Name = "pt-dial";
				th.Start();
				UtpConn utp = null;
				bool allowUtp = eng.settings.EnableUtp && Session.Utp != null && (VpnHub.TunnelOn || VpnHub.OsAllowed);
				if (allowUtp) {
					try { utp = Session.Utp.Connect(host, port, 1200); } catch { utp = null; }
				}
				DateTime until = DateTime.UtcNow.AddMilliseconds(utp != null ? 400 : 3800);
				while (DateTime.UtcNow < until) {
					bool done;
					lock (dialGate) done = tcpFinished;
					if (done) break;
					Thread.Sleep(20);
				}
				TcpPeerIo got = null;
				lock (dialGate) {
					if (tcpWin != null) {
						pick = 1;
						got = tcpWin;
						tcpWin = null;
					} else if (utp != null) pick = 2;
				}
				if (got != null) {
					if (utp != null) { try { utp.Close(); } catch { } }
					return FinishTcp(got);
				}
				if (pick == 2 && utp != null) {
					io = new UtpPeerIo(utp);
					if (utp.Remote != null) NotePex(utp.Remote);
					return Handshake();
				}
				try { th.Join(4200); } catch { }
				lock (dialGate) {
					if (tcpWin != null) { got = tcpWin; tcpWin = null; }
				}
				if (got != null) return FinishTcp(got);
				return false;
			}

			bool FinishTcp(TcpPeerIo dio) {
				if (dio == null) return false;
				NotePex(dio.Remote);
				if (eng.settings.EnableEncrypt) {
					if (dio.TryMseOutgoing(eng.infoHash)) {
						io = dio;
						return Handshake();
					}
					try { dio.Close(); } catch { }
					dio = TcpPeerIo.Dial(host, port, 4000);
					if (dio == null) return false;
					NotePex(dio.Remote);
				}
				io = dio;
				return Handshake();
			}

			bool Handshake() {
				byte[] hs = new byte[68];
				hs[0] = 19;
				byte[] proto = Encoding.ASCII.GetBytes("BitTorrent protocol");
				Buffer.BlockCopy(proto, 0, hs, 1, 19);
				hs[25] = (byte)(hs[25] | 0x10);
				Buffer.BlockCopy(eng.infoHash, 0, hs, 28, 20);
				Buffer.BlockCopy(eng.peerId, 0, hs, 48, 20);
				byte[] theirs = new byte[68];
				if (incoming) {
					if (!ReadExact(theirs, 0, 68, 15000)) return false;
					if (!CheckHs(theirs)) return false;
					WriteAll(hs);
				} else {
					WriteAll(hs);
					if (!ReadExact(theirs, 0, 68, 15000)) return false;
					if (!CheckHs(theirs)) return false;
				}
				theyExt = (theirs[25] & 0x10) != 0;
				lastRecv = DateTime.UtcNow;
				lastSend = DateTime.UtcNow;
				int pc = (eng.meta != null) ? eng.meta.PieceCount : 0;
				their = new BitArray(Math.Max(pc, 0));
				return true;
			}

			bool CheckHs(byte[] theirs) {
				if (theirs[0] != 19) return false;
				if (Encoding.ASCII.GetString(theirs, 1, 19) != "BitTorrent protocol") return false;
				for (int i = 0; i < 20; i++) if (theirs[28 + i] != eng.infoHash[i]) return false;
				return true;
			}

			void WriteAll(byte[] msg) {
				if (msg == null || io == null) return;
				lock (wlock) {
					io.Write(msg, msg.Length);
					lastSend = DateTime.UtcNow;
				}
			}

			bool ReadExact(byte[] buf, int off, int n, int timeoutMs) {
				if (io == null) return false;
				return io.ReadExact(buf, off, n, timeoutMs);
			}

			void SendInterested() {
				byte[] msg = new byte[5];
				Bt.W32(msg, 0, 1);
				msg[4] = 2;
				WriteAll(msg);
			}
			void SendUnchoke() {
				byte[] msg = new byte[5];
				Bt.W32(msg, 0, 1);
				msg[4] = 1;
				WriteAll(msg);
			}
			void SendChoke() {
				byte[] msg = new byte[5];
				Bt.W32(msg, 0, 1);
				msg[4] = 0;
				WriteAll(msg);
			}
			void SendNotInterested() {
				byte[] msg = new byte[5];
				Bt.W32(msg, 0, 1);
				msg[4] = 3;
				WriteAll(msg);
			}
			public void SetChoked(bool choke) {
				if (choke == amChoking) return;
				amChoking = choke;
				try {
					if (choke) SendChoke();
					else SendUnchoke();
				} catch { }
			}
			public void MarkNotInterested() {
				try { SendNotInterested(); } catch { }
			}
			void NoteUp(int n) {
				peerSent += n;
				DateTime now = DateTime.UtcNow;
				double dt = (now - peerUpAt).TotalSeconds;
				if (dt < 0.4) return;
				double inst = (peerSent - peerSentLast) / Math.Max(dt, 0.001);
				if (peerUpBps < 32) peerUpBps = inst;
				else peerUpBps = peerUpBps * 0.5 + inst * 0.5;
				peerSentLast = peerSent;
				peerUpAt = now;
			}
			public string PexEndpoint() {
				if (pexAddr == null) return null;
				int p = incoming ? theirListenPort : port;
				if (p <= 0 || p > 65535) return null;
				string num = p.ToString(CultureInfo.InvariantCulture);
				if (pexAddr.AddressFamily == AddressFamily.InterNetwork) return pexAddr.ToString() + ":" + num;
				return "[" + pexAddr.ToString() + "]:" + num;
			}
			public byte PexFlags() {
				byte f = 0;
				if (io != null && io.Transport != null && io.Transport.IndexOf("MSE", StringComparison.Ordinal) >= 0) f |= 1;
				if (theySeed) f |= 2;
				if (io != null && io.Transport == "uTP") f |= 4;
				return f;
			}
			public void SendPex(byte[] fullPayload, byte[] diffPayload, bool diffEmpty) {
				if (theirUtPex <= 0) return;
				DateTime now = DateTime.UtcNow;
				if ((now - lastPexAt).TotalSeconds < 60) return;
				lastPexAt = now;
				try {
					if (!pexInitialSent) {
						if (fullPayload != null) SendExt(theirUtPex, fullPayload);
						pexInitialSent = true;
					} else if (!diffEmpty && diffPayload != null) {
						SendExt(theirUtPex, diffPayload);
					}
				} catch { }
			}
			void SendHave(int piece) {
				byte[] msg = new byte[9];
				Bt.W32(msg, 0, 5);
				msg[4] = 4;
				Bt.W32(msg, 5, piece);
				WriteAll(msg);
			}
			void SendExt(int extId, byte[] payload) {
				byte[] msg = new byte[6 + payload.Length];
				Bt.W32(msg, 0, 2 + payload.Length);
				msg[4] = 20;
				msg[5] = (byte)extId;
				Buffer.BlockCopy(payload, 0, msg, 6, payload.Length);
				WriteAll(msg);
			}

			void SendExtHandshake() {
				Dictionary<string, Be> m = new Dictionary<string, Be>();
				m["ut_metadata"] = Be.Int(1);
				if (eng.meta == null || !eng.meta.Private) m["ut_pex"] = Be.Int(2);
				Dictionary<string, Be> root = new Dictionary<string, Be>();
				root["m"] = Be.FromDict(m);
				if (eng.boundPort > 0) root["p"] = Be.Int(eng.boundPort);
				root["v"] = Be.Blob(Encoding.UTF8.GetBytes("PowerTorrent/1.5"));
				if (eng.settings.EnableEncrypt) root["e"] = Be.Int(1);
				if (eng.rawInfo != null) root["metadata_size"] = Be.Int(eng.rawInfo.Length);
				SendExt(0, Benc.Encode(Be.FromDict(root)));
			}

			void SendMetaRequest(int index) {
				if (theirUtMeta <= 0) return;
				Dictionary<string, Be> d = new Dictionary<string, Be>();
				d["msg_type"] = Be.Int(0);
				d["piece"] = Be.Int(index);
				SendExt(theirUtMeta, Benc.Encode(Be.FromDict(d)));
			}

			void ServeMetaPiece(int index) {
				if (theirUtMeta <= 0) return;
				byte[] data = eng.MetaPieceBytes(index);
				if (data == null) {
					Dictionary<string, Be> rej = new Dictionary<string, Be>();
					rej["msg_type"] = Be.Int(2);
					rej["piece"] = Be.Int(index);
					SendExt(theirUtMeta, Benc.Encode(Be.FromDict(rej)));
					return;
				}
				Dictionary<string, Be> d = new Dictionary<string, Be>();
				d["msg_type"] = Be.Int(1);
				d["piece"] = Be.Int(index);
				d["total_size"] = Be.Int(eng.rawInfo.Length);
				byte[] hdr = Benc.Encode(Be.FromDict(d));
				byte[] payload = new byte[hdr.Length + data.Length];
				Buffer.BlockCopy(hdr, 0, payload, 0, hdr.Length);
				Buffer.BlockCopy(data, 0, payload, hdr.Length, data.Length);
				SendExt(theirUtMeta, payload);
			}

			void ApplyBitfield(byte[] bf) {
				if (eng.pieces == null || their == null) return;
				int bits = bf.Length;
				if (addedAvail) eng.pieces.SubAvail(their);
				int pc = eng.meta.PieceCount;
				their = new BitArray(Math.Max(pc, 0));
				theyHave = 0;
				for (int i = 0; i < pc; i++) {
					int bi = i / 8;
					if (bi >= bits) break;
					int bit = 7 - (i % 8);
					if ((bf[bi] & (1 << bit)) != 0) {
						their[i] = true;
						theyHave++;
					}
				}
				theySeed = pc > 0 && theyHave >= pc;
				eng.pieces.AddAvail(their);
				addedAvail = true;
			}

			void SendBitfield() {
				if (eng.pieces == null) return;
				byte[] bf = eng.pieces.MakeBitfield();
				bool any = false;
				for (int i = 0; i < bf.Length; i++) if (bf[i] != 0) { any = true; break; }
				if (!any) return;
				byte[] msg = new byte[5 + bf.Length];
				Bt.W32(msg, 0, 1 + bf.Length);
				msg[4] = 5;
				Buffer.BlockCopy(bf, 0, msg, 5, bf.Length);
				WriteAll(msg);
			}
			void SendRequest(int piece, int begin, int len) {
				byte[] msg = new byte[17];
				Bt.W32(msg, 0, 13);
				msg[4] = 6;
				Bt.W32(msg, 5, piece);
				Bt.W32(msg, 9, begin);
				Bt.W32(msg, 13, len);
				WriteAll(msg);
			}
			void SendCancel(int piece, int begin, int len) {
				byte[] msg = new byte[17];
				Bt.W32(msg, 0, 13);
				msg[4] = 8;
				Bt.W32(msg, 5, piece);
				Bt.W32(msg, 9, begin);
				Bt.W32(msg, 13, len);
				WriteAll(msg);
			}
			public void CancelBlock(int piece, int begin) {
				lock (claimLock) {
					for (int i = claimsPiece.Count - 1; i >= 0; i--) {
						if (claimsPiece[i] != piece || claimsBegin[i] != begin) continue;
						int len = 16384;
						try { if (eng.pieces != null) len = eng.pieces.BlockLength(piece, begin); } catch { }
						try { SendCancel(piece, begin, len); } catch { }
						claimsPiece.RemoveAt(i);
						claimsBegin.RemoveAt(i);
						claimAt.RemoveAt(i);
						return;
					}
				}
			}
			void SendKeepAlive() {
				WriteAll(new byte[4]);
			}
			void SendPieceMsg(int index, int begin, byte[] data) {
				byte[] msg = new byte[13 + data.Length];
				Bt.W32(msg, 0, 9 + data.Length);
				msg[4] = 7;
				Bt.W32(msg, 5, index);
				Bt.W32(msg, 9, begin);
				Buffer.BlockCopy(data, 0, msg, 13, data.Length);
				WriteAll(msg);
				eng.AddSessionUp(data.Length);
				NoteUp(data.Length);
			}

			void Handle(byte[] msg) {
				if (msg.Length < 1) return;
				int id = msg[0];
				if (id == 0) {
					amChoked = true;
					DropClaims();
				} else if (id == 1) {
					amChoked = false;
				} else if (id == 2) {
					theyInterested = true;
					eng.NudgeRechoke();
				} else if (id == 3) {
					theyInterested = false;
					if (!amChoking) SetChoked(true);
				} else if (id == 4 && msg.Length >= 5) {
					int idx = Bt.R32(msg, 1);
					if (eng.pieces == null) {
						pendingHaves.Add(idx);
					} else if (their != null && idx >= 0 && idx < their.Length && !their[idx]) {
						their[idx] = true;
						theyHave++;
						if (eng.meta != null && theyHave >= eng.meta.PieceCount && eng.meta.PieceCount > 0) theySeed = true;
						eng.pieces.AddAvailOne(idx);
					}
				} else if (id == 5) {
					int bits = msg.Length - 1;
					byte[] bf = new byte[bits];
					Buffer.BlockCopy(msg, 1, bf, 0, bits);
					if (eng.pieces == null) pendingBitfield = bf;
					else {
						ApplyBitfield(bf);
						if (eng.pieces.PeerHasSomethingWeNeed(their)) SendInterested();
					}
				} else if (id == 6 && msg.Length >= 13) {
					if (amChoking || eng.pieces == null) return;
					int idx = Bt.R32(msg, 1);
					int begin = Bt.R32(msg, 5);
					int len = Bt.R32(msg, 9);
					if (len > 16384 || len <= 0) return;
					byte[] data = eng.pieces.ReadBlock(idx, begin, len);
					if (data != null) SendPieceMsg(idx, begin, data);
				} else if (id == 20 && msg.Length >= 2) {
					HandleExt(msg);
				} else if (id == 7 && msg.Length >= 9) {
					int idx = Bt.R32(msg, 1);
					int begin = Bt.R32(msg, 5);
					int dlen = msg.Length - 9;
					bool match = false;
					lock (claimLock) {
						for (int i = 0; i < claimsPiece.Count; i++) {
							if (claimsPiece[i] == idx && claimsBegin[i] == begin) {
								claimsPiece.RemoveAt(i);
								claimsBegin.RemoveAt(i);
								claimAt.RemoveAt(i);
								match = true;
								break;
							}
						}
					}
					if (eng.pieces == null) return;
					int stored = eng.pieces.Submit(idx, begin, msg, 9, dlen);
					if (stored < 0) {
						strikes++;
						if (strikes >= 2) {
							eng.Log(1, "dropping " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + " after bad pieces");
							try { Kill(); } catch { }
						}
						return;
					}
					if (stored == 0) {
						if (match) eng.pieces.Unclaim(idx, begin);
						else if (eng.pieces.BlockDone(idx, begin)) eng.BroadcastCancel(idx, begin, this);
						return;
					}
					peerGot += dlen;
					snubHits = 0;
					snubbed = false;
					eng.BroadcastCancel(idx, begin, this);
					if (stored == 2) {
						eng.Log(2, "piece " + idx.ToString(CultureInfo.InvariantCulture) + " verified");
						eng.BroadcastHave(idx);
					}
				}
			}

			void HandleExt(byte[] msg) {
				int ext = msg[1];
				if (ext == 0) {
					int consumed;
					Be d = Benc.DecodeAt(msg, 2, out consumed);
					if (d == null || d.Dict == null) return;
					Be m = d.Get("m");
					if (m != null && m.Dict != null) {
						theirUtMeta = (int)m.GetInt("ut_metadata", 0);
						theirUtPex = (int)m.GetInt("ut_pex", 0);
					}
					int lp = (int)d.GetInt("p", 0);
					if (lp > 0 && lp < 65536) theirListenPort = lp;
					int msz = (int)d.GetInt("metadata_size", 0);
					if (msz > 0 && !eng.infoReady) eng.OfferMetaSize(msz);
				} else if (ext == 1) {
					int consumed;
					Be d = Benc.DecodeAt(msg, 2, out consumed);
					if (d == null || d.Dict == null) return;
					int mtype = (int)d.GetInt("msg_type", -1);
					int piece = (int)d.GetInt("piece", -1);
					if (mtype == 0) {
						ServeMetaPiece(piece);
					} else if (mtype == 1) {
						int rest = msg.Length - 2 - consumed;
						if (rest < 0) return;
						byte[] data = new byte[rest];
						if (rest > 0) Buffer.BlockCopy(msg, 2 + consumed, data, 0, rest);
						int ts = (int)d.GetInt("total_size", 0);
						if (ts > 0) eng.OfferMetaSize(ts);
						lastMetaIdx = -1;
						eng.OfferMetaPiece(piece, data);
					} else if (mtype == 2) {
						lastMetaIdx = -1;
					}
				} else if (ext == 2) {
					if (eng.meta != null && eng.meta.Private) return;
					if ((DateTime.UtcNow - lastPexRx).TotalSeconds < 30) return;
					lastPexRx = DateTime.UtcNow;
					int consumed;
					Be d = Benc.DecodeAt(msg, 2, out consumed);
					if (d == null || d.Dict == null) return;
					List<string> tmp = new List<string>();
					Be added = d.Get("added");
					if (added != null && added.Bytes != null) Bt.AddCompactPeers(added.Bytes, tmp);
					Be added6 = d.Get("added6");
					if (added6 != null && added6.Bytes != null) Bt.AddCompact6(added6.Bytes, tmp);
					for (int i = 0; i < tmp.Count; i++) eng.RememberPeer(tmp[i]);
					if (tmp.Count > 0) eng.Log(2, "PEX +" + tmp.Count.ToString(CultureInfo.InvariantCulture) + " from " + host);
				}
			}

			void Pump() {
				if (!eng.running || eng.paused) return;
				DateTime now = DateTime.UtcNow;
				if ((now - lastSend).TotalSeconds > 100) {
					try { SendKeepAlive(); } catch { }
				}
				if (eng.pieces == null) {
					if (theirUtMeta > 0 && !eng.infoReady) {
						if (lastMetaIdx < 0 || (now - lastMetaAt).TotalSeconds > 8) {
							int idx = eng.NextMetaPiece();
							if (idx >= 0) {
								lastMetaIdx = idx;
								lastMetaAt = now;
								try { SendMetaRequest(idx); } catch { }
							}
						}
					}
					return;
				}
				bool endgame = eng.pieces.IsEndgame();
				lock (claimLock) {
					for (int i = claimsPiece.Count - 1; i >= 0; i--) {
						if (eng.pieces.BlockDone(claimsPiece[i], claimsBegin[i])) {
							int len = 16384;
							try { len = eng.pieces.BlockLength(claimsPiece[i], claimsBegin[i]); } catch { }
							try { SendCancel(claimsPiece[i], claimsBegin[i], len); } catch { }
							claimsPiece.RemoveAt(i);
							claimsBegin.RemoveAt(i);
							claimAt.RemoveAt(i);
							continue;
						}
						int claimSec = snubbed ? 4 : (endgame ? 6 : 15);
						if ((now - claimAt[i]).TotalSeconds > claimSec) {
							eng.pieces.Unclaim(claimsPiece[i], claimsBegin[i]);
							claimsPiece.RemoveAt(i);
							claimsBegin.RemoveAt(i);
							claimAt.RemoveAt(i);
							snubHits++;
							if (snubHits >= 2) snubbed = true;
						}
					}
					if (amChoked) return;
					int want = DesiredQueue();
					while (claimsPiece.Count < want) {
						int p, b, l;
						if (!eng.pieces.TryClaim(their, eng.settings.Sequential, false, true, claimsPiece, claimsBegin, out p, out b, out l)) break;
						if (!SendClaim(p, b, l)) break;
					}
					if (claimsPiece.Count == 0) {
						int p, b, l;
						if (eng.pieces.TryClaim(their, eng.settings.Sequential, true, false, claimsPiece, claimsBegin, out p, out b, out l))
							SendClaim(p, b, l);
					}
				}
			}
			int DesiredQueue() {
				DateTime n = DateTime.UtcNow;
				double dt = (n - peerRateAt).TotalSeconds;
				if (dt >= 0.4) {
					double inst = (peerGot - peerGotLast) / Math.Max(dt, 0.001);
					if (peerBps < 32) peerBps = inst;
					else peerBps = peerBps * 0.5 + inst * 0.5;
					peerGotLast = peerGot;
					peerRateAt = n;
				}
				int maxQ = VpnHub.TunnelOn ? 256 : 48;
				if (snubbed) return 2;
				int q;
				if (peerBps < 64) q = Math.Min(16, maxQ);
				else q = (int)(peerBps * 3.0 / 16384.0);
				if (q < 8) q = 8;
				if (q > maxQ) q = maxQ;
				return q;
			}
			bool SendClaim(int p, int b, int l) {
				claimsPiece.Add(p);
				claimsBegin.Add(b);
				claimAt.Add(DateTime.UtcNow);
				try { SendRequest(p, b, l); return true; }
				catch {
					eng.pieces.Unclaim(p, b);
					claimsPiece.RemoveAt(claimsPiece.Count - 1);
					claimsBegin.RemoveAt(claimsBegin.Count - 1);
					claimAt.RemoveAt(claimAt.Count - 1);
					return false;
				}
			}

			void DropClaims() {
				lock (claimLock) {
					if (eng.pieces == null) {
						claimsPiece.Clear();
						claimsBegin.Clear();
						claimAt.Clear();
						return;
					}
					for (int i = 0; i < claimsPiece.Count; i++) eng.pieces.Unclaim(claimsPiece[i], claimsBegin[i]);
					claimsPiece.Clear();
					claimsBegin.Clear();
					claimAt.Clear();
				}
			}

			void Cleanup() {
				try { DropClaims(); } catch { }
				if (addedAvail && eng.pieces != null) {
					try { eng.pieces.SubAvail(their); } catch { }
					addedAvail = false;
				}
				lock (eng.peerLock) eng.peers.Remove(this);
				try { if (io != null) io.Close(); } catch { }
				try { if (tcp != null) tcp.Close(); } catch { }
			}
		}

		public Engine(EngineSettings s) {
			if (s == null) throw new ArgumentNullException("s");
			settings = s;
			if (settings.MaxPeers <= 0) settings.MaxPeers = 80;
			if (settings.ListenPort <= 0) settings.ListenPort = 6881;
			logLevel = settings.LogLevel;
			peerId = Bt.MakePeerId();

			bool isMagnet = !string.IsNullOrEmpty(s.MagnetUri);
			if (isMagnet) {
				MagnetLink mag = MagnetLink.Parse(s.MagnetUri);
				infoHash = mag.InfoHash;
				v2Hash = mag.V2Hash;
				v2Only = mag.V2Only;
				magnetTrackers = mag.Trackers;
				magnetWebseeds = mag.Webseeds;
				meta = Meta.Stub(mag.DisplayName, mag.InfoHash, mag.Trackers, mag.Webseeds);
				infoReady = false;
				status.State = "Idle";
				status.Name = meta.Name;
				return;
			}

			if (string.IsNullOrEmpty(s.TorrentPath) || !File.Exists(s.TorrentPath))
				throw new FileNotFoundException("torrent not found", s.TorrentPath);
			byte[] torrentBytes = File.ReadAllBytes(s.TorrentPath);
			meta = Meta.Load(torrentBytes, s.SavePath);
			infoHash = meta.InfoHash;
			v2Hash = meta.V2Hash;
			v2Only = meta.V2 && meta.PieceHashes == null;
			rawInfo = meta.RawInfo;
			pieces = new PieceMgr(this, meta);
			infoReady = true;
			status.State = "Idle";
			status.Name = meta.Name;
		}

		public bool IsRunning {
			get {
				if (!running) return false;
				string st;
				lock (statusLock) st = status.State;
				if (st == "Error" || st == "Stopped" || st == "Complete") return false;
				return true;
			}
		}

		public static string Fmt(long n) {
			double v = (double)n;
			string u = "B";
			if (v >= 1024) { v /= 1024; u = "KB"; }
			if (v >= 1024) { v /= 1024; u = "MB"; }
			if (v >= 1024) { v /= 1024; u = "GB"; }
			if (v >= 1024) { v /= 1024; u = "TB"; }
			return v.ToString("0.00", CultureInfo.InvariantCulture) + " " + u;
		}

		public TorrentInfo GetInfo() {
			TorrentInfo t = new TorrentInfo();
			t.Name = meta != null ? meta.Name : "";
			t.Comment = meta != null ? (meta.Comment ?? "") : "";
			t.CreatedBy = meta != null ? (meta.CreatedBy ?? "") : "";
			t.InfoHashHex = meta != null ? meta.InfoHashHex : "";
			t.TotalSize = meta != null ? meta.TotalSize : 0;
			t.PieceLength = meta != null ? meta.PieceLength : 0;
			t.PieceCount = meta != null ? meta.PieceCount : 0;
			t.FileCount = (meta != null && meta.Files != null) ? meta.Files.Count : 0;
			t.IsMulti = meta != null && meta.IsMulti;
			t.SavePath = settings != null && settings.SavePath != null ? settings.SavePath : "";
			t.Trackers = (meta != null) ? meta.Trackers.ToArray() : new string[0];
			t.Webseeds = (meta != null) ? meta.Webseeds.ToArray() : new string[0];
			int fc = t.FileCount;
			t.Files = new string[fc];
			t.FileRows = new FileRow[fc];
			for (int i = 0; i < fc; i++) {
				FileRow fr = new FileRow();
				fr.Name = meta.Files[i].RelPath ?? "";
				fr.SizeText = Fmt(meta.Files[i].Length);
				t.FileRows[i] = fr;
				t.Files[i] = fr.Name + "	(" + fr.SizeText + ")";
			}
			if (pieces != null) pieces.FillFileProgress(t.FileRows);
			return t;
		}

		public void UpdateFileProgress(FileRow[] rows) {
			if (pieces != null) pieces.FillFileProgress(rows);
		}

		void NoteSwarm(int seeds) {
			if (seeds > trackerSeeds) trackerSeeds = seeds;
		}

		public EngineSettings Settings { get { return settings; } }
		public bool IsPaused { get { return paused; } }
		internal byte[] InfoHashBytes { get { return infoHash; } }
		internal int ActivePeerThreads { get { return activePeerThreads; } }
		internal bool KeepGoing { get { return running; } }

		internal void RunIncoming(PeerIo io) {
			Interlocked.Increment(ref activePeerThreads);
			try {
				PeerWorker w = new PeerWorker(this, io);
				w.Run();
			} finally {
				Interlocked.Decrement(ref activePeerThreads);
			}
		}

		void ZeroRates() {
			downBps = 0;
			upBps = 0;
			lastDl = Interlocked.Read(ref sessionDown);
			lastUl = Interlocked.Read(ref sessionUp);
			lastSp = DateTime.UtcNow;
			spdN = 0;
			spdI = 0;
			spdSum = 0;
			etaSmooth = -1;
			idleTicks = 0;
		}

		public void Pause() {
			paused = true;
			ZeroRates();
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) snap[i].Kill();
			lock (statusLock) status.State = "Paused";
			Log(1, "paused");
		}

		public void Resume() {
			paused = false;
			lastDl = Interlocked.Read(ref sessionDown);
			lastUl = Interlocked.Read(ref sessionUp);
			lastSp = DateTime.UtcNow;
			if (!running) {
				Start();
				return;
			}
			lock (statusLock) {
				if (status.State == "Paused") {
					if (!infoReady) status.State = "Metadata";
					else if (pieces != null && pieces.IsComplete)
						status.State = settings.SeedAfterComplete ? "Seeding" : "Complete";
					else status.State = "Downloading";
				}
			}
			Log(1, "resumed");
		}

		public void Start() {
			if (running) return;
			if (!Session.Register(this)) {
				lock (statusLock) {
					status.State = "Error";
					status.ErrorMessage = "session torrent limit (" + Session.MaxTorrents.ToString(CultureInfo.InvariantCulture) + ")";
				}
				return;
			}
			sessionReg = true;
			running = true;
			if (infoReady && !hashedThisRun) {
				lock (statusLock) {
					status.State = "Hashing";
					status.PiecesDone = 0;
					status.PiecesTotal = meta != null ? meta.PieceCount : 0;
				}
			}
			coordThread = new Thread(Coordinator);
			coordThread.IsBackground = true;
			coordThread.Name = "PowerTorrent";
			coordThread.Start();
		}

		public void ApplyLiveSettings() {
			if (settings.MaxPeers <= 0) settings.MaxPeers = 80;
			if (settings.ListenPort <= 0) settings.ListenPort = 6881;
			if (!running) return;
			if (settings.EnableDht && (meta == null || !meta.Private)) {
				Thread t = dhtThread;
				if (t == null || !t.IsAlive) {
					dhtThread = new Thread(DhtRun);
					dhtThread.IsBackground = true;
					dhtThread.Start();
					Log(1, "DHT enabled");
				}
			}
			Session.EnsureListen(settings.ListenPort, settings.EnableUtp);
			if (boundPort <= 0) boundPort = Session.BoundPort;
			if (boundPort > 0) Dht.Attach(boundPort);
		}

		public void Stop() {
			paused = false;
			running = false;
			hashedThisRun = false;
			ZeroRates();
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) snap[i].Kill();
			if (startedAnnounced) {
				try { AnnounceAll("stopped"); } catch { }
			}
			if (coordThread != null && Thread.CurrentThread != coordThread) {
				try { coordThread.Join(3000); } catch { }
			}
			try { SaveResume(); } catch { }
			if (infoHash != null) Dht.Unwant(infoHash);
			if (pieces != null) pieces.Close();
			if (sessionReg) {
				Session.Unregister(this);
				sessionReg = false;
			}
			ZeroRates();
			lock (statusLock) {
				if (status.State != "Complete" && status.State != "Error") status.State = "Stopped";
			}
		}

		public void DeleteDownloadedFiles() {
			if (meta == null || meta.Files == null) return;
			for (int i = 0; i < meta.Files.Count; i++) {
				string p = meta.Files[i].Path;
				if (string.IsNullOrEmpty(p)) continue;
				try { if (File.Exists(p)) File.Delete(p); } catch (Exception ex) { Log(1, "delete failed: " + ex.Message); }
			}
			try {
				if (settings != null && !string.IsNullOrEmpty(settings.SavePath) && meta.Name != null) {
					string tp = Path.Combine(settings.SavePath, meta.Name + ".torrent");
					if (File.Exists(tp)) File.Delete(tp);
				}
			} catch { }
			if (meta.IsMulti && settings != null && !string.IsNullOrEmpty(settings.SavePath) && !string.IsNullOrEmpty(meta.Name)) {
				try {
					string saveRoot = Path.GetFullPath(settings.SavePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					string root = Path.GetFullPath(Path.Combine(settings.SavePath, meta.Name));
					string prefix = saveRoot + Path.DirectorySeparatorChar;
					if (Directory.Exists(root) && root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(root, saveRoot, StringComparison.OrdinalIgnoreCase))
						Directory.Delete(root, true);
				} catch (Exception ex) { Log(1, "delete folder failed: " + ex.Message); }
			}
		}

		public EngineStatus GetStatus() {
			EngineStatus s = new EngineStatus();
			int dc = 0, tot = 0;
			long ver = 0, totb = 0;
			if (pieces != null) pieces.Snapshot(out dc, out tot, out ver, out totb);
			lock (statusLock) {
				s.State = status.State;
				s.ErrorMessage = status.ErrorMessage;
			}
			s.Name = meta != null ? meta.Name : "";
			s.InfoHashHex = meta != null ? meta.InfoHashHex : "";
			s.Downloaded = ver;
			s.Uploaded = Interlocked.Read(ref sessionUp);
			s.TotalSize = meta != null ? meta.TotalSize : 0;
			if (s.State == "Metadata" || (!infoReady && pieces == null)) {
				int got, totMd;
				lock (metaLock) {
					got = mdGot;
					totMd = mdHave != null ? mdHave.Length : 0;
				}
				s.State = "Metadata";
				s.PiecesDone = got;
				s.PiecesTotal = totMd;
				s.ProgressPercent = totMd > 0 ? (100.0 * got / totMd) : 0;
				s.Eta = "metadata";
			} else if (s.State == "Hashing") {
				int hpDone, hpTot;
				lock (statusLock) {
					hpDone = status.PiecesDone;
					hpTot = status.PiecesTotal;
				}
				s.PiecesDone = dc;
				s.PiecesTotal = tot > 0 ? tot : hpTot;
				s.Downloaded = ver;
				s.ProgressPercent = s.TotalSize > 0 ? (100.0 * ver / s.TotalSize) : 0;
				if (hpTot > 0)
					s.Eta = "checking " + hpDone.ToString(CultureInfo.InvariantCulture) + "/" + hpTot.ToString(CultureInfo.InvariantCulture);
				else
					s.Eta = "checking";
			} else {
				s.ProgressPercent = s.TotalSize > 0 ? (100.0 * ver / s.TotalSize) : 0;
				s.PiecesDone = dc;
				s.PiecesTotal = tot;
				s.Eta = "--";
			}
			int seedN = 0;
			lock (peerLock) {
				s.PeersConnected = peers.Count;
				for (int i = 0; i < peers.Count; i++) if (peers[i].IsSeed) seedN++;
			}
			s.SeedsConnected = seedN;
			int ts = trackerSeeds;
			s.SeedsKnown = ts > seedN ? ts : seedN;
			lock (poolLock) s.PeersKnown = seen.Count;
			s.ListenPort = boundPort;
			s.Availability = pieces != null ? pieces.Availability() : 0;
			lock (logLock) s.LogLines = logs.ToArray();
			if (paused && s.State != "Stopped" && s.State != "Error" && s.State != "Complete")
				s.State = "Paused";
			bool halt = paused || !running || s.State == "Stopped" || s.State == "Paused" || s.State == "Error" || s.State == "Complete" || s.State == "VPN";
			if (halt) {
				s.DownBytesPerSec = 0;
				s.UpBytesPerSec = 0;
				if (s.State == "Paused" || s.State == "Stopped" || s.State == "Complete" || s.State == "Error" || s.State == "VPN")
					s.Eta = "--";
			} else {
				UpdateSpeed();
				s.DownBytesPerSec = downBps;
				s.UpBytesPerSec = upBps;
				if (s.State != "Metadata" && s.State != "Hashing" && ver < s.TotalSize) {
					s.Eta = FormatEta(s.TotalSize - ver);
				}
			}
			return s;
		}

		string FormatEta(long remain) {
			if (remain <= 0) return "00:00:00";
			double avgWin = spdN > 0 ? spdSum / spdN : 0;
			double avgHist = (activeSecs >= 4) ? (double)Interlocked.Read(ref sessionDown) / activeSecs : 0;
			double rate;
			if (avgWin > 256 && avgHist > 256) rate = 0.65 * avgWin + 0.35 * avgHist;
			else if (avgWin > 256) rate = avgWin;
			else if (avgHist > 256) rate = avgHist;
			else rate = 0;
			if (rate < 256) {
				if (etaSmooth > 2 && avgWin > 64) return FormatEtaSec(etaSmooth);
				return "--";
			}
			double sec = remain / rate;
			if (etaSmooth < 0) etaSmooth = sec;
			else etaSmooth = etaSmooth * 0.75 + sec * 0.25;
			if (etaSmooth < 0) etaSmooth = 0;
			if (etaSmooth > 359999) etaSmooth = 359999;
			return FormatEtaSec(etaSmooth);
		}

		static string FormatEtaSec(double sec) {
			int secI = (int)(sec + 0.5);
			if (secI < 0) secI = 0;
			if (secI > 359999) secI = 359999;
			return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", secI / 3600, (secI / 60) % 60, secI % 60);
		}

		public static string SelfTest() {
			try {
				Be a = Benc.Decode(Encoding.ASCII.GetBytes("i42e"));
				if (a.Kind != 0 || a.I != 42) return "int";
				Be b = Benc.Decode(Encoding.ASCII.GetBytes("4:spam"));
				if (Encoding.ASCII.GetString(b.Bytes) != "spam") return "str";
				Be c = Benc.Decode(Encoding.ASCII.GetBytes("le"));
				if (c.List == null || c.List.Count != 0) return "elist";
				Be d = Benc.Decode(Encoding.ASCII.GetBytes("de"));
				if (d.Dict == null || d.Dict.Count != 0) return "edict";
				Be e = Benc.Decode(Encoding.ASCII.GetBytes("d3:foo3:bare"));
				if (e.Str("foo") != "bar") return "dict";
				byte[] rt = Benc.Encode(e);
				Be e2 = Benc.Decode(rt);
				if (e2.Str("foo") != "bar") return "roundtrip";

				byte[] torrent = Encoding.ASCII.GetBytes("d8:announce15:http://t.com:804:infod6:lengthi4e4:name4:test12:piece lengthi16384e6:pieces20:01234567890123456789ee");
				byte[] info = Benc.SliceDictValue(torrent, "info");
				if (info == null || info[0] != (byte)'d') return "slice";
				Be root = Benc.Decode(torrent);
				if (root.Get("info") == null) return "parse torrent";
				if (root.Get("info").GetInt("length", 0) != 4) return "info length";

				byte[] buf4 = new byte[4];
				Bt.W32(buf4, 0, 0x01020304);
				if (buf4[0] != 1 || buf4[1] != 2 || buf4[2] != 3 || buf4[3] != 4) return "w32";
				if (Bt.R32(buf4, 0) != 0x01020304) return "r32";

				byte[] buf8 = new byte[8];
				Bt.W64(buf8, 0, 0x41727101980L);
				if (buf8[2] != 0x04 || buf8[3] != 0x17 || buf8[7] != 0x80) return "w64-magic";
				if (Bt.R64(buf8, 0) != 0x41727101980L) return "r64";

				string enc = Bt.UrlEnc(new byte[] { 0x00, 0xAB, 0xFF });
				if (enc != "%00%AB%FF") return "urlenc " + enc;

				List<string> peers = new List<string>();
				Bt.AddCompactPeers(new byte[] { 1, 2, 3, 4, 0x1A, 0xE1 }, peers);
				if (peers.Count != 1 || peers[0] != "1.2.3.4:6881") return "compact " + (peers.Count > 0 ? peers[0] : "none");

				byte[] empty = Benc.Encode(Be.FromDict(new Dictionary<string, Be>()));
				if (empty.Length != 2 || empty[0] != (byte)'d' || empty[1] != (byte)'e') return "enc-de";

				int consumed;
				Be da = Benc.DecodeAt(Encoding.ASCII.GetBytes("i42eMORE"), 0, out consumed);
				if (da.I != 42 || consumed != 4) return "decodeat";

				MagnetLink mag = MagnetLink.Parse("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Hello%20World&tr=udp%3A%2F%2Ft.example.com%3A80");
				if (mag.InfoHashHex != "0123456789abcdef0123456789abcdef01234567") return "magnet-hex";
				if (mag.DisplayName != "Hello World") return "magnet-dn";
				if (mag.Trackers.Count != 1 || mag.Trackers[0] != "udp://t.example.com:80") return "magnet-tr";

				byte[] z = MagnetLink.FromBase32("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
				if (z == null || z.Length != 20) return "b32-len";
				for (int i = 0; i < 20; i++) if (z[i] != 0) return "b32-zero";

				byte[] h = MagnetLink.ParseInfoHash("0123456789ABCDEF0123456789ABCDEF01234567");
				if (h == null || h[0] != 0x01 || h[19] != 0x67) return "parse-hash";

				MagnetLink mag2 = MagnetLink.Parse("magnet:?xt=urn:btmh:1220000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
				if (!mag2.V2Only || mag2.V2Hash == null || mag2.V2Hash.Length != 32) return "magnet-v2";
				if (mag2.InfoHash[0] != 0x00 || mag2.InfoHash[1] != 0x01 || mag2.V2Hash[31] != 0x1f) return "magnet-v2-trunc";

				Rc4 rc = new Rc4(Encoding.ASCII.GetBytes("Key"));
				byte[] pt = Encoding.ASCII.GetBytes("Plaintext");
				rc.Crypt(pt, 0, pt.Length);
				string rcHex = BitConverter.ToString(pt).Replace("-", "");
				if (rcHex != "BBF316E8D940AF0AD3") return "rc4 " + rcHex;

				BigInteger two = new BigInteger(2);
				byte[] pub = Crypto.DhPub(two);
				if (pub.Length != 96 || pub[95] != 4) return "dh-pub";
				byte[] sec = Crypto.DhSecret(pub, two);
				if (sec[95] != 16) return "dh-sec";

				byte[] leaf = new byte[16384];
				byte[] mroot = Crypto.MerklePiece(leaf, leaf.Length);
				if (mroot == null || mroot.Length != 32) return "merkle";
				int plim = Session.PeerLimit(null, 80);
				if (plim < 6 || plim > Session.GlobalMaxPeers) return "session-peer-limit";
				if (Session.TorrentCount != 0) return "session-count";
				List<Engine> batch = new List<Engine>();
				try {
					for (int i = 0; i < 50; i++) {
						EngineSettings es = new EngineSettings();
						es.MagnetUri = "magnet:?xt=urn:btih:" + i.ToString("x8", CultureInfo.InvariantCulture).PadLeft(40, '0');
						es.SavePath = Path.GetTempPath();
						es.MaxPeers = 80;
						es.ListenPort = 6881;
						Engine en = new Engine(es);
						if (!Session.Register(en)) return "session-reg-50 " + i.ToString(CultureInfo.InvariantCulture);
						batch.Add(en);
					}
					if (Session.TorrentCount != 50) return "session-count-50";
					int share = Session.PeerLimit(batch[0], 80);
					if (share != 10) return "session-share-50 " + share.ToString(CultureInfo.InvariantCulture);
					EngineSettings extraS = new EngineSettings();
					extraS.MagnetUri = batch[0].GetInfo().InfoHashHex.Length == 40
						? "magnet:?xt=urn:btih:" + batch[0].GetInfo().InfoHashHex
						: "magnet:?xt=urn:btih:ffffffffffffffffffffffffffffffffffffffff";
					extraS.SavePath = Path.GetTempPath();
					Engine dup = new Engine(extraS);
					if (Session.Register(dup)) return "session-dup";
				} finally {
					for (int i = 0; i < batch.Count; i++) Session.Unregister(batch[i]);
				}
				if (Session.TorrentCount != 0) return "session-cleanup";
				string magS = TorrentSearch.MakeMagnet("0123456789abcdef0123456789abcdef01234567", "a b");
				if (magS.IndexOf("btih:0123456789abcdef0123456789abcdef01234567", StringComparison.Ordinal) < 0) return "search-mag";
				if (magS.IndexOf("dn=a%20b", StringComparison.Ordinal) < 0) return "search-dn";
				if (!TorrentSearch.IsZeroHash("0000000000000000000000000000000000000000")) return "search-zero";
				List<SearchRow> parsed = new List<SearchRow>();
				Dictionary<string, bool> seenS = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
				TorrentSearch.ParseObjects("[{\"info_hash\":\"0123456789abcdef0123456789abcdef01234567\",\"name\":\"x\",\"size\":\"10\",\"seeders\":\"3\",\"leechers\":\"1\"}]", "info_hash", "size", "The Pirate Bay", parsed, seenS);
				if (parsed.Count != 1 || parsed[0].Seeds != 3 || parsed[0].SizeBytes != 10) return "search-parse";
				List<SearchRow> parsedCsv = new List<SearchRow>();
				Dictionary<string, bool> seenCsv = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
				TorrentSearch.ParseObjects("{\"torrents\":[{\"infohash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"one\",\"size_bytes\":11,\"seeders\":4,\"leechers\":2},{\"infohash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"two\",\"size_bytes\":22,\"seeders\":5,\"leechers\":1}],\"next\":9}", "infohash", "size_bytes", "torrents-csv", parsedCsv, seenCsv);
				if (parsedCsv.Count != 2 || parsedCsv[0].Name != "one" || parsedCsv[1].Name != "two" || parsedCsv[1].SizeBytes != 22) return "search-parse-csv";
				bool oldReq = VpnHub.Require;
				VpnHub.Require = false;
				if (!VpnHub.OsAllowed) { VpnHub.Require = oldReq; return "os-allowed"; }
				VpnHub.Require = oldReq;
				string vpnFail = VpnHub.SelfCheck();
				if (vpnFail != null) return "vpn-" + vpnFail;
				string dhtFail = Dht.SelfCheck();
				if (dhtFail != null) return "dht-" + dhtFail;
				if (Session.UnchokeSlots(null) != 8) return "unchoke-slots";
				byte[] pexA, pexF, pexA6, pexF6;
				List<string> pexEps = new List<string>();
				pexEps.Add("1.2.3.4:6881");
				List<byte> pexFlags = new List<byte>();
				pexFlags.Add(2);
				PackEnds(pexEps, pexFlags, out pexA, out pexF, out pexA6, out pexF6);
				if (pexA == null || pexA.Length != 6 || pexA[0] != 1 || pexA[4] != 0x1A || pexA[5] != 0xE1 || pexF.Length != 1 || pexF[0] != 2) return "pex";
				if (OvpnSess.PickCipher("AES-256-CBC", "AES-128-GCM:AES-256-GCM") != "AES-256-GCM") return "ovpn-ciphers";
				if (OvpnSess.PickCipher("AES-256-CBC", null) != "AES-256-CBC") return "ovpn-cipher";
				if (OvpnSess.PickCipher(null, "AES-128-GCM") != null) return "ovpn-no-gcm";
				return null;
			} catch (Exception ex) {
				return ex.ToString();
			}
		}

		void AddSessionDown(int n) { Interlocked.Add(ref sessionDown, n); }
		void AddSessionUp(int n) { Interlocked.Add(ref sessionUp, n); }

		void Log(int level, string msg) {
			if (level > logLevel) return;
			string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg;
			lock (logLock) {
				logs.Add(line);
				while (logs.Count > 15) logs.RemoveAt(0);
			}
		}

		void OnHashProgress(int cur, int tot) {
			lock (statusLock) {
				status.State = "Hashing";
				status.PiecesDone = cur;
				status.PiecesTotal = tot;
			}
		}

		void HashExistingFiles() {
			if (!infoReady || pieces == null) return;
			if (hashedThisRun) {
				pieces.Open(true);
				return;
			}
			lock (statusLock) {
				status.State = "Hashing";
				status.PiecesDone = 0;
				status.PiecesTotal = meta != null ? meta.PieceCount : 0;
			}
			long[] lens;
			long[] ticks;
			bool[] dirty;
			bool resumeOk = ResumeMatches(out lens, out ticks, out dirty);
			pieces.Open(AfterHash != 2);
			if (resumeOk) {
				byte[] bits = ReadResumeBits();
				if (bits == null) resumeOk = false;
			}
			if (resumeOk) {
				byte[] bits = ReadResumeBits();
				pieces.ImportBitfield(bits);
				bool anyDirty = false;
				if (dirty != null) {
					for (int i = 0; i < dirty.Length; i++) if (dirty[i]) { anyDirty = true; pieces.DropPiecesTouching(i); }
				}
				if (!anyDirty) {
					hashedThisRun = true;
					int dc0, tot0; long ver0, totb0;
					pieces.Snapshot(out dc0, out tot0, out ver0, out totb0);
					Log(1, "resume " + dc0.ToString(CultureInfo.InvariantCulture) + "/" + tot0.ToString(CultureInfo.InvariantCulture) + " pieces (" + Fmt(ver0) + ")");
					SaveResume();
					return;
				}
				Log(1, "resume changed, rechecking");
			} else {
				Log(1, "checking existing pieces...");
			}
			pieces.HashCheck();
			if (!running) return;
			hashedThisRun = true;
			int dc1, tot1; long ver1, totb1;
			pieces.Snapshot(out dc1, out tot1, out ver1, out totb1);
			Log(1, "have " + dc1.ToString(CultureInfo.InvariantCulture) + "/" + tot1.ToString(CultureInfo.InvariantCulture) + " pieces (" + Fmt(ver1) + ")");
			SaveResume();
		}

		string ResumePath() {
			if (meta == null || string.IsNullOrEmpty(meta.InfoHashHex)) return null;
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTorrent", "resume");
			return Path.Combine(dir, meta.InfoHashHex + ".resume");
		}

		static void FileStamp(string path, out long len, out long ticks) {
			len = -1;
			ticks = 0;
			try {
				if (!File.Exists(path)) return;
				FileInfo fi = new FileInfo(path);
				len = fi.Length;
				ticks = fi.LastWriteTimeUtc.Ticks;
			} catch { }
		}

		bool ResumeMatches(out long[] lens, out long[] ticks, out bool[] dirty) {
			lens = null;
			ticks = null;
			dirty = null;
			if (meta == null || meta.Files == null) return false;
			int nf = meta.Files.Count;
			lens = new long[nf];
			ticks = new long[nf];
			dirty = new bool[nf];
			string path = ResumePath();
			if (path == null || !File.Exists(path)) {
				for (int i = 0; i < nf; i++) dirty[i] = true;
				return false;
			}
			try {
				string[] lines = File.ReadAllLines(path);
				if (lines.Length < 3 || lines[0] != "PT1") return false;
				int pc;
				int fc;
				if (!int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pc)) return false;
				if (!int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fc)) return false;
				if (pc != meta.PieceCount || fc != nf) return false;
				if (lines.Length < 3 + fc + 1) return false;
				for (int i = 0; i < nf; i++) {
					FileStamp(meta.Files[i].Path, out lens[i], out ticks[i]);
					string[] parts = lines[3 + i].Split('\t');
					if (parts.Length < 2) { dirty[i] = true; continue; }
					long rl, rt;
					if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out rl)) { dirty[i] = true; continue; }
					if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rt)) { dirty[i] = true; continue; }
					dirty[i] = rl != lens[i] || rt != ticks[i];
				}
				return true;
			} catch { return false; }
		}

		byte[] ReadResumeBits() {
			try {
				string path = ResumePath();
				if (path == null || !File.Exists(path)) return null;
				string[] lines = File.ReadAllLines(path);
				int fc;
				if (lines.Length < 4 || !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fc)) return null;
				int at = 3 + fc;
				if (at >= lines.Length) return null;
				return Convert.FromBase64String(lines[at].Trim());
			} catch { return null; }
		}

		void SaveResume() {
			if (meta == null || pieces == null || meta.Files == null) return;
			string path = ResumePath();
			if (path == null) return;
			try {
				string dir = Path.GetDirectoryName(path);
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
				StringBuilder sb = new StringBuilder();
				sb.Append("PT1\n");
				sb.Append(meta.PieceCount.ToString(CultureInfo.InvariantCulture));
				sb.Append('\n');
				sb.Append(meta.Files.Count.ToString(CultureInfo.InvariantCulture));
				sb.Append('\n');
				for (int i = 0; i < meta.Files.Count; i++) {
					long len, ticks;
					FileStamp(meta.Files[i].Path, out len, out ticks);
					sb.Append(len.ToString(CultureInfo.InvariantCulture));
					sb.Append('\t');
					sb.Append(ticks.ToString(CultureInfo.InvariantCulture));
					sb.Append('\n');
				}
				sb.Append(Convert.ToBase64String(pieces.MakeBitfield()));
				sb.Append('\n');
				File.WriteAllText(path, sb.ToString());
				lastResumeSave = DateTime.UtcNow;
			} catch { }
		}

		bool ApplyAfterHash() {
			int mode = AfterHash;
			AfterHash = 0;
			if (mode == 2) {
				FinishStoppedAfterHash();
				return false;
			}
			if (mode == 1) {
				paused = true;
				ZeroRates();
				lock (statusLock) status.State = "Paused";
				Log(1, "paused after hash");
			}
			return true;
		}

		void FinishStoppedAfterHash() {
			paused = false;
			running = false;
			ZeroRates();
			if (pieces != null) pieces.Close();
			if (sessionReg) {
				Session.Unregister(this);
				sessionReg = false;
			}
			lock (statusLock) {
				if (status.State == "Error") return;
				if (pieces != null && pieces.IsComplete) status.State = "Complete";
				else status.State = "Stopped";
			}
			Log(1, "stopped after hash");
		}

		void BroadcastHave(int piece) {
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) {
				try { snap[i].SendHavePublic(piece); } catch { }
			}
		}

		void BroadcastCancel(int piece, int begin, PeerWorker except) {
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) {
				if (snap[i] == except) continue;
				try { snap[i].CancelBlock(piece, begin); } catch { }
			}
		}

		bool OfferMetaSize(int size) {
			if (size <= 0 || size > 8 * 1024 * 1024) return false;
			lock (metaLock) {
				if (mdSize < 0) {
					mdSize = size;
					int n = (size + 16383) / 16384;
					mdPieces = new byte[n][];
					mdHave = new bool[n];
					mdGot = 0;
					Log(1, "metadata size " + size.ToString(CultureInfo.InvariantCulture) + " bytes (" + n.ToString(CultureInfo.InvariantCulture) + " piece(s))");
					return true;
				}
				return mdSize == size;
			}
		}

		int NextMetaPiece() {
			lock (metaLock) {
				if (mdHave == null) return -1;
				for (int i = 0; i < mdHave.Length; i++) if (!mdHave[i]) return i;
				return -1;
			}
		}

		byte[] MetaPieceBytes(int index) {
			lock (metaLock) {
				if (rawInfo == null) return null;
				int sz = rawInfo.Length;
				int n = (sz + 16383) / 16384;
				if (index < 0 || index >= n) return null;
				int off = index * 16384;
				int len = Math.Min(16384, sz - off);
				byte[] b = new byte[len];
				Buffer.BlockCopy(rawInfo, off, b, 0, len);
				return b;
			}
		}

		void OfferMetaPiece(int index, byte[] data) {
			byte[] assembled = null;
			lock (metaLock) {
				if (infoReady || mdHave == null || index < 0 || index >= mdHave.Length) return;
				if (mdHave[index]) return;
				int expect = (index == mdHave.Length - 1) ? (mdSize - index * 16384) : 16384;
				if (data == null || data.Length != expect) return;
				mdPieces[index] = data;
				mdHave[index] = true;
				mdGot++;
				if (mdGot < mdHave.Length) {
					Log(2, "metadata piece " + index.ToString(CultureInfo.InvariantCulture) + " (" + mdGot.ToString(CultureInfo.InvariantCulture) + "/" + mdHave.Length.ToString(CultureInfo.InvariantCulture) + ")");
					return;
				}
				assembled = new byte[mdSize];
				int o = 0;
				for (int i = 0; i < mdPieces.Length; i++) {
					Buffer.BlockCopy(mdPieces[i], 0, assembled, o, mdPieces[i].Length);
					o += mdPieces[i].Length;
				}
			}
			if (assembled == null) return;
			byte[] hash;
			using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
				hash = sha.ComputeHash(assembled);
			}
			bool hashOk = true;
			if (v2Only && v2Hash != null && v2Hash.Length == 32) {
				byte[] h256 = Crypto.Sha256All(assembled);
				for (int k = 0; k < 32; k++) if (h256[k] != v2Hash[k]) hashOk = false;
			} else {
				for (int k = 0; k < 20; k++) if (hash[k] != infoHash[k]) hashOk = false;
			}
			if (!hashOk) {
					Log(0, "metadata hash mismatch, retrying");
					lock (metaLock) {
						mdSize = -1;
						mdPieces = null;
						mdHave = null;
						mdGot = 0;
					}
					return;
			}
			fetchedInfo = assembled;
			infoReady = true;
			Log(1, "metadata received (" + assembled.Length.ToString(CultureInfo.InvariantCulture) + " bytes)");
		}

		void ApplyFetchedInfo() {
			if (fetchedInfo == null) throw new Exception("no metadata");
			Meta loaded = Meta.FromInfoBytes(fetchedInfo, settings.SavePath);
			HashSet<string> seenT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < loaded.Trackers.Count; i++) seenT.Add(loaded.Trackers[i]);
			for (int i = 0; i < magnetTrackers.Count; i++) MetaAddTracker(loaded, seenT, magnetTrackers[i]);
			for (int i = 0; i < magnetWebseeds.Count; i++) {
				if (!string.IsNullOrEmpty(magnetWebseeds[i]) && !loaded.Webseeds.Contains(magnetWebseeds[i]))
					loaded.Webseeds.Add(magnetWebseeds[i]);
			}
			meta = loaded;
			rawInfo = fetchedInfo;
			lock (trkLock) { trk.Clear(); trkReady = false; }
			pieces = new PieceMgr(this, meta);
			status.Name = meta.Name;
			Log(1, "torrent: " + meta.Name + " (" + Fmt(meta.TotalSize) + ", " + meta.PieceCount.ToString(CultureInfo.InvariantCulture) + " pieces)");
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) {
				try { snap[i].OnEngineHasInfo(); } catch { }
			}
		}

		static void MetaAddTracker(Meta m, HashSet<string> seenT, string url) {
			if (string.IsNullOrEmpty(url)) return;
			url = url.Trim();
			if (url.Length == 0) return;
			if (!seenT.Add(url)) return;
			m.Trackers.Add(url);
			if (m.Tiers.Count == 0) m.Tiers.Add(new List<string>());
			m.Tiers[m.Tiers.Count - 1].Add(url);
		}

		void RememberPeer(string ep) {
			if (string.IsNullOrEmpty(ep)) return;
			lock (poolLock) {
				if (seen.Add(ep)) pool.Enqueue(ep);
			}
		}

		string NextPeer() {
			lock (poolLock) {
				if (pool.Count == 0) return null;
				return pool.Dequeue();
			}
		}

		public void DropPeers() {
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			for (int i = 0; i < snap.Length; i++) {
				try { snap[i].Kill(); } catch { }
			}
			lock (poolLock) {
				pool.Clear();
				foreach (string s in seen) pool.Enqueue(s);
				lastRecycle = DateTime.UtcNow;
			}
			if (snap.Length > 0) Log(1, "dropped " + snap.Length.ToString(CultureInfo.InvariantCulture) + " peer connection(s) for killswitch");
		}

		void RecyclePeers() {
			lock (poolLock) {
				if (pool.Count > 0) return;
				if (seen.Count == 0) return;
				if ((DateTime.UtcNow - lastRecycle).TotalSeconds < 90) return;
				lastRecycle = DateTime.UtcNow;
				foreach (string s in seen) pool.Enqueue(s);
				Log(2, "re-queued " + seen.Count.ToString(CultureInfo.InvariantCulture) + " peers");
			}
		}

		void UpdateSpeed() {
			DateTime n = DateTime.UtcNow;
			double dt = (n - lastSp).TotalSeconds;
			if (dt < 0.85) return;
			if (dt > 8) dt = 8;
			long dl = Interlocked.Read(ref sessionDown);
			long ul = Interlocked.Read(ref sessionUp);
			double nd = (dl - lastDl) / dt;
			double nu = (ul - lastUl) / dt;
			if (nd < 0) nd = 0;
			if (nu < 0) nu = 0;
			if (downBps < 1) downBps = nd;
			else downBps = downBps * 0.8 + nd * 0.2;
			if (upBps < 1) upBps = nu;
			else upBps = upBps * 0.8 + nu * 0.2;
			if (nd < 32 && nu < 32) {
				idleTicks++;
				if (idleTicks >= 3) { downBps = 0; upBps = 0; }
			} else idleTicks = 0;
			if (spdN == 30) spdSum -= spdWin[spdI];
			else spdN++;
			spdWin[spdI] = downBps;
			spdSum += downBps;
			spdI = (spdI + 1) % 30;
			if (nd > 0 || nu > 0) activeSecs += dt;
			lastDl = dl;
			lastUl = ul;
			lastSp = n;
		}

		void Coordinator() {
			try {
				try {
					ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
				} catch { }
				ServicePointManager.DefaultConnectionLimit = 64;
				ServicePointManager.Expect100Continue = false;

				if (infoReady) {
					HashExistingFiles();
					if (!running) return;
					if (!ApplyAfterHash()) return;
				}

				while (running && VpnHub.Blocked) {
					lock (statusLock) {
						if (paused) status.State = "Paused";
						else {
							status.State = "VPN";
							status.ErrorMessage = string.IsNullOrEmpty(VpnHub.LastError) ? "VPN down" : VpnHub.LastError;
						}
					}
					Thread.Sleep(400);
				}
				if (!running) return;
				lock (statusLock) status.ErrorMessage = "";

				Log(1, "info hash " + meta.InfoHashHex);
				if (VpnHub.TunnelOn) Log(1, "VPN tunnel " + VpnHub.Status);
				StartListen();
				if (!string.IsNullOrEmpty(settings.ForcedPeer)) RememberPeer(settings.ForcedPeer);

				if (!infoReady) {
					if (meta.Trackers.Count > 0) {
						lock (statusLock) status.State = "Metadata";
						Log(1, "announcing to " + meta.Trackers.Count.ToString(CultureInfo.InvariantCulture) + " tracker(s)");
						AnnounceAll("started");
						startedAnnounced = true;
					}
					if (settings.EnableDht && (meta == null || !meta.Private)) {
						dhtThread = new Thread(DhtRun);
						dhtThread.IsBackground = true;
						dhtThread.Start();
					}
					lock (statusLock) status.State = "Metadata";
					if (meta.Trackers.Count == 0 && !settings.EnableDht && string.IsNullOrEmpty(settings.ForcedPeer))
						Log(0, "magnet has no trackers, DHT is off, and no -Peer was given");
					Log(1, "fetching metadata");
					while (running && !infoReady) {
						if (VpnHub.Blocked) { Thread.Sleep(400); continue; }
						if (paused) { Thread.Sleep(250); continue; }
						RecyclePeers();
						FillPeers();
						Thread.Sleep(250);
					}
					if (!running) return;
					ApplyFetchedInfo();
					HashExistingFiles();
					if (!running) return;
					if (!ApplyAfterHash()) return;
				}

				if (settings.EnableDht && (meta == null || !meta.Private) && (dhtThread == null || !dhtThread.IsAlive)) {
					dhtThread = new Thread(DhtRun);
					dhtThread.IsBackground = true;
					dhtThread.Start();
				}

				if (meta.Trackers.Count > 0 && !paused) {
					lock (statusLock) status.State = "Announcing";
					string ev = startedAnnounced ? "" : "started";
					AnnounceAll(ev);
					startedAnnounced = true;
				}

				if (meta.Webseeds.Count > 0 && !paused) {
					int wn = Math.Min(4, Math.Max(2, meta.Webseeds.Count));
					if (Session.TorrentCount > 15) wn = 1;
					else if (Session.TorrentCount > 8) wn = Math.Min(wn, 2);
					for (int wi = 0; wi < wn; wi++) {
						Thread wt = new Thread(WebseedRun);
						wt.IsBackground = true;
						wt.Name = "pt-webseed";
						wt.Start();
					}
					Log(1, "webseeds: " + meta.Webseeds.Count.ToString(CultureInfo.InvariantCulture) + " (" + wn.ToString(CultureInfo.InvariantCulture) + " workers)");
				}

				lastSp = DateTime.UtcNow;
				if (pieces != null) pieces.RefreshBudget();
				while (running) {
					if (VpnHub.Blocked) {
						ZeroRates();
						lock (statusLock) {
							status.State = "VPN";
							status.ErrorMessage = string.IsNullOrEmpty(VpnHub.LastError) ? "VPN down" : VpnHub.LastError;
						}
						Thread.Sleep(400);
						continue;
					}
					if (paused) {
						ZeroRates();
						lock (statusLock) status.State = "Paused";
						Thread.Sleep(250);
						continue;
					}
					if (!startedAnnounced && meta.Trackers.Count > 0) {
						lock (statusLock) status.State = "Announcing";
						AnnounceAll("started");
						startedAnnounced = true;
					}
					UpdateSpeed();
					RecyclePeers();
					FillPeers();
					RechokeIfDue();
					TickPex();
					if ((DateTime.UtcNow - lastResumeSave).TotalSeconds >= 30) SaveResume();
					if (pieces.IsComplete) {
						if (settings.SeedAfterComplete) {
							lock (statusLock) {
								if (status.State != "Seeding") {
									status.State = "Seeding";
									Log(1, "download complete, seeding");
									if (!toldSeeding) {
										toldSeeding = true;
										PeerWorker[] snapSeed;
										lock (peerLock) snapSeed = peers.ToArray();
										for (int si = 0; si < snapSeed.Length; si++) snapSeed[si].MarkNotInterested();
									}
									ThreadPool.QueueUserWorkItem(delegate { try { AnnounceAll("completed"); } catch { } });
								}
							}
						} else {
							lock (statusLock) status.State = "Complete";
							Log(1, "download complete");
							ThreadPool.QueueUserWorkItem(delegate { try { AnnounceAll("completed"); } catch { } });
							running = false;
							break;
						}
					} else {
						lock (statusLock) {
							if (status.State != "Hashing") status.State = "Downloading";
						}
					}
					if ((DateTime.UtcNow - lastAnnScan).TotalSeconds >= 5) {
						lastAnnScan = DateTime.UtcNow;
						ThreadPool.QueueUserWorkItem(delegate { try { AnnounceAll(""); } catch { } });
					}
					Thread.Sleep(VpnHub.TunnelOn ? 25 : 50);
				}
			} catch (Exception ex) {
				lock (statusLock) {
					status.State = "Error";
					status.ErrorMessage = ex.Message;
				}
				Log(0, "engine error: " + ex.Message);
			}
		}

		void NudgeRechoke() {
			Interlocked.Exchange(ref rechokeNudge, 1);
		}

		void RechokeIfDue() {
			bool nudge = Interlocked.Exchange(ref rechokeNudge, 0) == 1;
			DateTime now = DateTime.UtcNow;
			if (!nudge && (now - lastRechoke).TotalSeconds < 15) return;
			if (nudge && (now - lastRechoke).TotalSeconds < 1) {
				Interlocked.Exchange(ref rechokeNudge, 1);
				return;
			}
			lastRechoke = now;
			Rechoke();
		}

		// Fixed slots, one of them optimistic. Seeds prefer the fastest downloaders (qBittorrent's default).
		void Rechoke() {
			if (pieces == null) return;
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			bool seeding = pieces.IsComplete;
			List<PeerWorker> cand = new List<PeerWorker>();
			for (int i = 0; i < snap.Length; i++) {
				if (!snap[i].Interested) {
					if (!snap[i].Choking) snap[i].SetChoked(true);
					continue;
				}
				cand.Add(snap[i]);
			}
			int slots = Session.UnchokeSlots(this);
			int rateSlots = slots > 0 ? slots - 1 : 0;
			cand.Sort(delegate(PeerWorker a, PeerWorker b) {
				double da = seeding ? a.UpRate : a.DownRate;
				double db = seeding ? b.UpRate : b.DownRate;
				if (da == db) return 0;
				return db.CompareTo(da);
			});
			bool still = false;
			for (int i = 0; i < cand.Count; i++) if (cand[i] == optPeer) still = true;
			bool rotate = optPeer == null || !still || (DateTime.UtcNow - optSince).TotalSeconds >= 30;
			if (rotate) {
				List<PeerWorker> pool = new List<PeerWorker>();
				for (int i = rateSlots; i < cand.Count; i++) pool.Add(cand[i]);
				if (pool.Count == 0 && cand.Count > 0) pool.Add(cand[cand.Count - 1]);
				if (pool.Count > 0) {
					optPeer = pool[new Random().Next(pool.Count)];
					optSince = DateTime.UtcNow;
				} else optPeer = null;
			}
			for (int i = 0; i < cand.Count; i++) {
				bool unchoke = i < rateSlots || cand[i] == optPeer;
				cand[i].SetChoked(!unchoke);
			}
		}

		void TickPex() {
			if (meta != null && meta.Private) return;
			if (nextPex == DateTime.MinValue) { nextPex = DateTime.UtcNow.AddSeconds(60); return; }
			if (DateTime.UtcNow < nextPex) return;
			nextPex = DateTime.UtcNow.AddSeconds(60);
			PeerWorker[] snap;
			lock (peerLock) snap = peers.ToArray();
			Dictionary<string, byte> nowSet = new Dictionary<string, byte>();
			for (int i = 0; i < snap.Length; i++) {
				string ep = snap[i].PexEndpoint();
				if (string.IsNullOrEmpty(ep) || nowSet.ContainsKey(ep)) continue;
				nowSet[ep] = snap[i].PexFlags();
			}
			List<string> added = new List<string>();
			List<byte> addedF = new List<byte>();
			List<string> dropped = new List<string>();
			foreach (KeyValuePair<string, byte> kv in nowSet) {
				if (pexHave.ContainsKey(kv.Key)) continue;
				if (added.Count >= 100) continue;
				added.Add(kv.Key);
				addedF.Add(kv.Value);
			}
			foreach (KeyValuePair<string, byte> kv in pexHave) {
				if (!nowSet.ContainsKey(kv.Key)) dropped.Add(kv.Key);
			}
			pexHave = nowSet;
			List<string> fullE = new List<string>();
			List<byte> fullF = new List<byte>();
			foreach (KeyValuePair<string, byte> kv in nowSet) {
				if (fullE.Count >= 100) break;
				fullE.Add(kv.Key);
				fullF.Add(kv.Value);
			}
			pexFull = BuildPex(fullE, fullF, new List<string>());
			pexDiff = BuildPex(added, addedF, dropped);
			pexDiffEmpty = added.Count == 0 && dropped.Count == 0;
			for (int i = 0; i < snap.Length; i++) snap[i].SendPex(pexFull, pexDiff, pexDiffEmpty);
		}

		static byte[] BuildPex(List<string> added, List<byte> addedF, List<string> dropped) {
			byte[] a4, f4, a6, f6, d4, d6, ignore4, ignore6;
			PackEnds(added, addedF, out a4, out f4, out a6, out f6);
			PackEnds(dropped, null, out d4, out ignore4, out d6, out ignore6);
			Dictionary<string, Be> d = new Dictionary<string, Be>();
			d["added"] = Be.Blob(a4);
			d["added.f"] = Be.Blob(f4);
			d["added6"] = Be.Blob(a6);
			d["added6.f"] = Be.Blob(f6);
			d["dropped"] = Be.Blob(d4);
			d["dropped6"] = Be.Blob(d6);
			return Benc.Encode(Be.FromDict(d));
		}

		static void PackEnds(List<string> eps, List<byte> flags, out byte[] v4, out byte[] f4, out byte[] v6, out byte[] f6) {
			List<byte> a = new List<byte>();
			List<byte> fa = new List<byte>();
			List<byte> b = new List<byte>();
			List<byte> fb = new List<byte>();
			if (eps != null) {
				for (int i = 0; i < eps.Count; i++) {
					byte fl = (flags != null && i < flags.Count) ? flags[i] : (byte)0;
					string ep = eps[i];
					if (string.IsNullOrEmpty(ep)) continue;
					int colon = ep.LastIndexOf(':');
					if (colon <= 0) continue;
					string h = ep.Substring(0, colon);
					int p;
					if (!int.TryParse(ep.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) continue;
					if (p <= 0 || p > 65535) continue;
					if (h.Length >= 2 && h[0] == '[') h = h.Substring(1, h.Length - 2);
					IPAddress ip;
					if (!IPAddress.TryParse(h, out ip)) continue;
					byte[] raw = ip.GetAddressBytes();
					List<byte> dst = raw.Length == 16 ? b : a;
					List<byte> fd = raw.Length == 16 ? fb : fa;
					if (raw.Length != 4 && raw.Length != 16) continue;
					for (int k = 0; k < raw.Length; k++) dst.Add(raw[k]);
					dst.Add((byte)(p >> 8));
					dst.Add((byte)p);
					fd.Add(fl);
				}
			}
			v4 = a.ToArray();
			f4 = fa.ToArray();
			v6 = b.ToArray();
			f6 = fb.ToArray();
		}

		void FillPeers() {
			if (paused) return;
			if (VpnHub.Blocked) return;
			int limit = Session.PeerLimit(this, settings.MaxPeers);
			int nT = Session.TorrentCount;
			int burst = nT > 20 ? 4 : (nT > 8 ? 8 : 24);
			if (VpnHub.TunnelOn) burst = Math.Max(burst, 16);
			for (int k = 0; k < burst; k++) {
				if (activePeerThreads >= limit) return;
				lock (peerLock) {
					if (activePeerThreads - peers.Count >= burst) return;
				}
				string ep = NextPeer();
				if (ep == null) return;
				int colon = ep.LastIndexOf(':');
				if (colon <= 0) continue;
				string h = ep.Substring(0, colon);
				if (h.Length >= 2 && h[0] == '[') h = h.Substring(1, h.Length - 2);
				int p;
				if (!int.TryParse(ep.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) continue;
				if (p <= 0 || p > 65535) continue;
				StartOutgoing(h, p);
			}
		}

		void StartOutgoing(string host, int port) {
			if (!Session.TryBeginPeer()) return;
			Interlocked.Increment(ref activePeerThreads);
			Thread t = new Thread(delegate(object state) {
				try {
					object[] hp = (object[])state;
					PeerWorker w = new PeerWorker(this, (string)hp[0], (int)hp[1]);
					w.Run();
				} finally {
					Interlocked.Decrement(ref activePeerThreads);
					Session.EndPeer();
				}
			});
			t.IsBackground = true;
			t.Start(new object[] { host, port });
		}

		void StartListen() {
			int p = Session.EnsureListen(settings.ListenPort, settings.EnableUtp);
			if (p > 0) {
				boundPort = p;
				Log(1, "listening on TCP " + boundPort.ToString(CultureInfo.InvariantCulture)
					+ (Session.Utp != null ? " / UDP" : "")
					+ (VpnHub.TunnelOn ? " (VPN)" : ""));
			} else {
				boundPort = settings.ListenPort;
				if (VpnHub.Blocked) Log(1, "VPN down: not listening");
				else Log(1, "could not bind a listen port; outgoing connections only");
			}
			if (boundPort > 0 && (meta == null || !meta.Private)) Dht.Attach(boundPort);
		}

		static void SendUdp(VpnUdpSock vs, UdpClient udp, byte[] req, IPEndPoint dest) {
			if (vs != null) vs.SendTo(req, dest);
			else udp.Send(req, req.Length);
		}
		static byte[] RecvUdp(VpnUdpSock vs, UdpClient udp, int timeoutMs) {
			if (vs != null) {
				byte[] d; IPEndPoint f;
				if (!vs.Recv(timeoutMs, out d, out f)) return null;
				return d;
			}
			IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
			return udp.Receive(ref ep);
		}

		void EnsureTrackers() {
			if (trkReady || meta == null) return;
			lock (trkLock) {
				if (trkReady) return;
				List<string> urls = new List<string>();
				if (meta.Tiers != null && meta.Tiers.Count > 0) {
					for (int t = 0; t < meta.Tiers.Count; t++) {
						if (meta.Tiers[t] == null) continue;
						for (int u = 0; u < meta.Tiers[t].Count; u++) urls.Add(meta.Tiers[t][u]);
					}
				} else if (meta.Trackers != null) {
					for (int i = 0; i < meta.Trackers.Count; i++) urls.Add(meta.Trackers[i]);
				}
				HashSet<string> seenU = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < urls.Count; i++) {
					if (string.IsNullOrEmpty(urls[i]) || !seenU.Add(urls[i])) continue;
					Trk tr = new Trk();
					tr.Url = urls[i];
					tr.Next = DateTime.UtcNow;
					trk.Add(tr);
				}
				trkReady = true;
			}
		}

		void AnnounceAll(string ev) {
			if (meta == null) return;
			bool periodic = string.IsNullOrEmpty(ev);
			if (periodic && Interlocked.CompareExchange(ref annBusy, 1, 0) != 0) return;
			try {
			EnsureTrackers();
			bool force = ev == "started" || ev == "completed" || ev == "stopped";
			List<Trk> due = new List<Trk>();
			DateTime now = DateTime.UtcNow;
			lock (trkLock) {
				for (int i = 0; i < trk.Count; i++) {
					if (force || trk[i].Next <= now) due.Add(trk[i]);
				}
			}
			if (due.Count == 0) {
				if (force && ev == "started") Log(1, "no trackers in torrent");
				return;
			}
			if (force) Log(1, "announcing to " + due.Count.ToString(CultureInfo.InvariantCulture) + " tracker(s)");
			int remaining = due.Count;
			ManualResetEvent done = new ManualResetEvent(false);
			for (int i = 0; i < due.Count; i++) {
				ThreadPool.QueueUserWorkItem(delegate(object state) {
					Trk tr = (Trk)state;
					List<string> local = new List<string>();
					AnnOut result = new AnnOut();
					try {
						string scheme = "";
						try { scheme = new Uri(tr.Url).Scheme.ToLowerInvariant(); } catch { }
						if (scheme == "udp") QueryUdp(tr.Url, ev, local, result);
						else if (scheme == "http" || scheme == "https") QueryHttp(tr.Url, ev, local, result);
						else Log(2, "skip tracker " + tr.Url);
					} catch (Exception ex) {
						Log(2, "tracker " + tr.Url + " " + ex.Message);
					}
					lock (trkLock) {
						if (result.Ok) {
							tr.Fail = 0;
							tr.Interval = result.Interval >= 60 ? result.Interval : 1800;
							tr.Next = DateTime.UtcNow.AddSeconds(tr.Interval);
						} else {
							tr.Fail++;
							int back = 60;
							int f = tr.Fail;
							if (f > 6) f = 6;
							for (int k = 1; k < f; k++) back *= 2;
							if (back > 1800) back = 1800;
							tr.Next = DateTime.UtcNow.AddSeconds(back);
						}
					}
					for (int k = 0; k < local.Count; k++) RememberPeer(local[k]);
					if (local.Count > 0)
						Log(1, local.Count.ToString(CultureInfo.InvariantCulture) + " peers from " + tr.Url);
					if (Interlocked.Decrement(ref remaining) == 0) done.Set();
				}, due[i]);
			}
			done.WaitOne(ev == "stopped" ? 2000 : (force ? 12000 : 8000));
			} finally {
				if (periodic) Interlocked.Exchange(ref annBusy, 0);
			}
		}

		void QueryUdp(string url, string ev, List<string> peersOut, AnnOut result) {
			Session.AcquireAnnounce();
			try { QueryUdpCore(url, ev, peersOut, result); }
			finally { Session.ReleaseAnnounce(); }
		}

		void QueryUdpCore(string url, string ev, List<string> peersOut, AnnOut result) {
			if (VpnHub.Blocked) { Log(2, "UDP blocked (VPN down)"); return; }
			Uri uri;
			try { uri = new Uri(url); } catch { return; }
			if (uri.Port <= 0) { Log(2, "UDP tracker missing port: " + url); return; }
			IPAddress ip = Bt.ResolveV4(uri.Host);
			if (ip == null) { Log(2, "UDP DNS failed: " + uri.Host); return; }
			VpnUdpSock vs = null;
			UdpClient udp = null;
			IPEndPoint dest = new IPEndPoint(ip, uri.Port);
			try {
				if (VpnHub.TunnelOn) vs = VpnHub.BindUdp(0);
				else if (VpnHub.OsAllowed) {
					udp = new UdpClient();
					udp.Client.ReceiveTimeout = 8000;
					udp.Connect(ip, uri.Port);
				} else return;
				Random rng = new Random(Guid.NewGuid().GetHashCode());
				byte[] req = new byte[16];
				Bt.W64(req, 0, 0x41727101980L);
				Bt.W32(req, 8, 0);
				int tx = rng.Next();
				Bt.W32(req, 12, tx);
				SendUdp(vs, udp, req, dest);
				byte[] resp = RecvUdp(vs, udp, 8000);
				if (resp == null || resp.Length < 16) return;
				if (Bt.R32(resp, 0) != 0) return;
				if (Bt.R32(resp, 4) != tx) return;
				byte[] conn = new byte[8];
				Buffer.BlockCopy(resp, 8, conn, 0, 8);

				byte[] a = new byte[98];
				Buffer.BlockCopy(conn, 0, a, 0, 8);
				Bt.W32(a, 8, 1);
				tx = rng.Next();
				Bt.W32(a, 12, tx);
				Buffer.BlockCopy(infoHash, 0, a, 16, 20);
				Buffer.BlockCopy(peerId, 0, a, 36, 20);
				int dc = 0, totp = 0; long ver = 0, totb = 0;
				if (pieces != null) pieces.Snapshot(out dc, out totp, out ver, out totb);
				else totb = 1;
				Bt.W64(a, 56, Interlocked.Read(ref sessionDown));
				Bt.W64(a, 64, Math.Max(0, totb - ver));
				Bt.W64(a, 72, Interlocked.Read(ref sessionUp));
				int evn = 0;
				if (ev == "completed") evn = 1;
				else if (ev == "started") evn = 2;
				else if (ev == "stopped") evn = 3;
				Bt.W32(a, 80, evn);
				Bt.W32(a, 84, 0);
				Bt.W32(a, 88, rng.Next());
				Bt.W32(a, 92, 200);
				a[96] = (byte)((boundPort >> 8) & 0xFF);
				a[97] = (byte)(boundPort & 0xFF);
				SendUdp(vs, udp, a, dest);
				resp = RecvUdp(vs, udp, 8000);
				if (resp == null || resp.Length < 20) return;
				if (Bt.R32(resp, 4) != tx) return;
				int action = Bt.R32(resp, 0);
				if (action == 3) {
					Log(1, "UDP tracker error " + url);
					return;
				}
				if (action != 1) return;
				int iv = Bt.R32(resp, 8);
				if (result != null) {
					result.Ok = true;
					result.Interval = iv >= 60 ? iv : 1800;
				}
				if (iv >= 60) {
					lock (statusLock) {
						if (iv < announceInterval) announceInterval = iv;
					}
				}
				if (resp.Length >= 20) {
					int seeders = Bt.R32(resp, 16);
					if (seeders > 0) NoteSwarm(seeders);
				}
				if (resp.Length > 20) {
					byte[] compact = new byte[resp.Length - 20];
					Buffer.BlockCopy(resp, 20, compact, 0, compact.Length);
					Bt.AddCompactPeers(compact, peersOut);
				}
			} catch (Exception ex) {
				Log(2, "UDP " + url + " " + ex.Message);
			} finally {
				if (vs != null) VpnHub.DropUdp(vs);
				try { if (udp != null) udp.Close(); } catch { }
			}
		}

		void QueryHttp(string url, string ev, List<string> peersOut, AnnOut result) {
			Session.AcquireAnnounce();
			try { QueryHttpCore(url, ev, peersOut, result); }
			finally { Session.ReleaseAnnounce(); }
		}

		void QueryHttpCore(string url, string ev, List<string> peersOut, AnnOut result) {
			try {
				string sep = url.IndexOf('?') >= 0 ? "&" : "?";
				int dc = 0, totp = 0; long ver = 0, totb = 0;
				if (pieces != null) pieces.Snapshot(out dc, out totp, out ver, out totb);
				else totb = 1;
				StringBuilder sb = new StringBuilder();
				sb.Append(url);
				sb.Append(sep);
				sb.Append("info_hash=");
				sb.Append(Bt.UrlEnc(infoHash));
				sb.Append("&peer_id=");
				sb.Append(Bt.UrlEnc(peerId));
				sb.Append("&port=");
				sb.Append(boundPort.ToString(CultureInfo.InvariantCulture));
				sb.Append("&uploaded=");
				sb.Append(Interlocked.Read(ref sessionUp).ToString(CultureInfo.InvariantCulture));
				sb.Append("&downloaded=");
				sb.Append(Interlocked.Read(ref sessionDown).ToString(CultureInfo.InvariantCulture));
				sb.Append("&left=");
				sb.Append(Math.Max(0, totb - ver).ToString(CultureInfo.InvariantCulture));
				sb.Append("&compact=1&numwant=200&supportcrypto=1");
				if (!string.IsNullOrEmpty(ev)) {
					sb.Append("&event=");
					sb.Append(ev);
				}
				if (VpnHub.Blocked) return;
				byte[] body;
				if (VpnHub.TunnelOn) {
					int st;
					if (!VpnHub.HttpRequest(sb.ToString(), 15000, -1, -1, out st, out body) || body == null) return;
				} else if (VpnHub.OsAllowed) {
					HttpWebRequest req = (HttpWebRequest)WebRequest.Create(sb.ToString());
					req.Method = "GET";
					req.UserAgent = "PowerTorrent/1.5";
					req.Timeout = 15000;
					req.ReadWriteTimeout = 15000;
					req.KeepAlive = false;
					req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
					using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
					using (Stream rs = resp.GetResponseStream()) {
						MemoryStream ms = new MemoryStream();
						byte[] tmp = new byte[4096];
						int n;
						while ((n = rs.Read(tmp, 0, tmp.Length)) > 0) ms.Write(tmp, 0, n);
						body = ms.ToArray();
					}
				} else return;
				Be dict = Benc.Decode(body);
				if (dict == null || dict.Dict == null) return;
				Be fail = dict.Get("failure reason");
				if (fail != null && fail.Bytes != null) {
					Log(1, "tracker " + url + ": " + Encoding.UTF8.GetString(fail.Bytes));
					return;
				}
				long iv = dict.GetInt("interval", 0);
				if (result != null) {
					result.Ok = true;
					result.Interval = iv >= 60 ? (int)iv : 1800;
				}
				if (iv >= 60) {
					lock (statusLock) {
						if ((int)iv < announceInterval) announceInterval = (int)iv;
					}
				}
				int complete = (int)dict.GetInt("complete", -1);
				if (complete >= 0) NoteSwarm(complete);
				Be p = dict.Get("peers");
				if (p != null && p.Bytes != null) Bt.AddCompactPeers(p.Bytes, peersOut);
				else if (p != null && p.List != null) {
					for (int i = 0; i < p.List.Count; i++) {
						Be d2 = p.List[i];
						if (d2.Dict == null) continue;
						string ip = d2.Str("ip");
						long prt = d2.GetInt("port", 0);
						if (!string.IsNullOrEmpty(ip) && prt > 0) {
							string ep = ip.IndexOf(':') >= 0
								? "[" + ip + "]:" + prt.ToString(CultureInfo.InvariantCulture)
								: ip + ":" + prt.ToString(CultureInfo.InvariantCulture);
							peersOut.Add(ep);
						}
					}
				}
			} catch (Exception ex) {
				Log(2, "HTTP " + url + " " + ex.Message);
			}
		}

		static byte[] BuildGetPeers(byte[] nodeId, byte[] ih, byte[] tid) {
			MemoryStream ms = new MemoryStream();
			byte[] p1 = Encoding.ASCII.GetBytes("d1:ad2:id20:");
			ms.Write(p1, 0, p1.Length);
			ms.Write(nodeId, 0, 20);
			byte[] p2 = Encoding.ASCII.GetBytes("9:info_hash20:");
			ms.Write(p2, 0, p2.Length);
			ms.Write(ih, 0, 20);
			byte[] p3 = Encoding.ASCII.GetBytes("e1:q9:get_peers1:t");
			ms.Write(p3, 0, p3.Length);
			byte[] p4 = Encoding.ASCII.GetBytes(tid.Length.ToString(CultureInfo.InvariantCulture) + ":");
			ms.Write(p4, 0, p4.Length);
			ms.Write(tid, 0, tid.Length);
			byte[] p5 = Encoding.ASCII.GetBytes("1:y1:qe");
			ms.Write(p5, 0, p5.Length);
			return ms.ToArray();
		}

		static byte[] BuildAnnouncePeer(byte[] nodeId, byte[] ih, byte[] token, int port, byte[] tid) {
			MemoryStream ms = new MemoryStream();
			byte[] p1 = Encoding.ASCII.GetBytes("d1:ad2:id20:");
			ms.Write(p1, 0, p1.Length);
			ms.Write(nodeId, 0, 20);
			byte[] p2 = Encoding.ASCII.GetBytes("9:info_hash20:");
			ms.Write(p2, 0, p2.Length);
			ms.Write(ih, 0, 20);
			byte[] p3 = Encoding.ASCII.GetBytes("4:porti" + port.ToString(CultureInfo.InvariantCulture) + "e5:token" + token.Length.ToString(CultureInfo.InvariantCulture) + ":");
			ms.Write(p3, 0, p3.Length);
			ms.Write(token, 0, token.Length);
			byte[] p4 = Encoding.ASCII.GetBytes("e1:q13:announce_peer1:t" + tid.Length.ToString(CultureInfo.InvariantCulture) + ":");
			ms.Write(p4, 0, p4.Length);
			ms.Write(tid, 0, tid.Length);
			byte[] p5 = Encoding.ASCII.GetBytes("1:y1:qe");
			ms.Write(p5, 0, p5.Length);
			return ms.ToArray();
		}

		void DhtRun() {
			int stagger = Session.IndexOf(this);
			if (stagger < 0) stagger = 0;
			Thread.Sleep(80 * (stagger % 25));
			Action<string> onPeer = delegate(string ep) { RememberPeer(ep); };
			Action<int> onDone = delegate(int n) {
				if (n > 0) Log(1, "DHT +" + n.ToString(CultureInfo.InvariantCulture) + " peers");
			};
			while (running && settings.EnableDht) {
				if (meta != null && meta.Private) break;
				if (VpnHub.Blocked || (!VpnHub.TunnelOn && !VpnHub.OsAllowed)) { Thread.Sleep(400); continue; }
				if (boundPort > 0 && infoHash != null) Dht.Want(infoHash, boundPort, onPeer, onDone);
				Thread.Sleep(1000);
			}
			if (infoHash != null) Dht.Unwant(infoHash);
		}

		void WebseedRun() {
			while (running && pieces != null && !pieces.IsComplete) {
				if (paused) { Thread.Sleep(250); continue; }
				try {
					BitArray all = new BitArray(Math.Max(meta.PieceCount, 0));
					all.SetAll(true);
					int piece, begin, len;
					bool endgame = pieces.IsEndgame();
					if (!pieces.TryClaim(all, settings.Sequential, endgame, out piece, out begin, out len)) {
						Thread.Sleep(1000);
						continue;
					}
					bool ok = false;
					for (int w = 0; w < meta.Webseeds.Count && !ok && running; w++) {
						ok = FetchWebseedBlock(meta.Webseeds[w], piece, begin, len);
					}
					if (!ok) {
						pieces.Unclaim(piece, begin);
						Thread.Sleep(800);
					}
				} catch {
					Thread.Sleep(1000);
				}
			}
		}

		bool FetchWebseedBlock(string baseUrl, int piece, int begin, int length) {
			long off = (long)piece * (long)meta.PieceLength + begin;
			byte[] data = new byte[length];
			long cursor = off;
			int remain = length;
			int bufOff = 0;
			for (int fi = 0; fi < meta.Files.Count && remain > 0; fi++) {
				FileEnt f = meta.Files[fi];
				long fe = f.Offset + f.Length;
				if (cursor >= fe || cursor < f.Offset) continue;
				long local = cursor - f.Offset;
				int n = (int)Math.Min((long)remain, f.Length - local);
				string url = CombineUrl(baseUrl, f.RelPath, meta.IsMulti);
				if (!HttpRange(url, local, n, data, bufOff)) return false;
				cursor += n;
				remain -= n;
				bufOff += n;
			}
			if (remain != 0) return false;
			int stored = pieces.Submit(piece, begin, data, 0, length);
			if (stored == 0) return false;
			if (stored == 2) BroadcastHave(piece);
			return true;
		}

		static string CombineUrl(string baseUrl, string rel, bool multi) {
			if (!multi) {
				if (baseUrl.EndsWith("/")) return baseUrl + rel.Replace('\\', '/');
				return baseUrl;
			}
			string r = rel.Replace('\\', '/');
			if (!baseUrl.EndsWith("/")) baseUrl += "/";
			return baseUrl + r;
		}

		bool HttpRange(string url, long start, int n, byte[] dest, int destOff) {
			if (VpnHub.Blocked) return false;
			if (VpnHub.TunnelOn) {
				int st; byte[] body;
				if (!VpnHub.HttpRequest(url, 20000, start, start + n - 1, out st, out body) || body == null) return false;
				if (st == 200 && start != 0) return false;
				if (st != 206 && st != 200) return false;
				if (body.Length < n) return false;
				Buffer.BlockCopy(body, 0, dest, destOff, n);
				return true;
			}
			if (!VpnHub.OsAllowed) return false;
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = "GET";
			req.UserAgent = "PowerTorrent/1.5";
			req.Timeout = 20000;
			req.ReadWriteTimeout = 20000;
			req.AddRange(start, start + n - 1);
			req.KeepAlive = false;
			using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) {
				if (resp.StatusCode == HttpStatusCode.OK && start != 0) return false;
				if (resp.StatusCode != HttpStatusCode.PartialContent && resp.StatusCode != HttpStatusCode.OK) return false;
				using (Stream s = resp.GetResponseStream()) {
					int got = 0;
					while (got < n) {
						int r = s.Read(dest, destOff + got, n - got);
						if (r <= 0) return false;
						got += r;
					}
				}
				return true;
			}
		}
	}
}

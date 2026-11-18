namespace Guardex;

static class Scorer
{
	public static Verdict Score(string src, IReadOnlyList<Observation> events, string canary)
	{
		var items = events.OrderBy(e => e.Utc).ToList();
		var signals = new List<Signal>();
		var http = items.Where(e => e.Kind == ObsKind.Http).ToList();

		var families = new HashSet<string>();
		var paths = new HashSet<string>(StringComparer.Ordinal);
		var queries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		var headerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var ports = new HashSet<int>();
		var browser = 0;
		var cookieBare = 0;
		var status4 = 0;
		var seenHard = new HashSet<string>();

		foreach (var e in items)
		{
			if (e.LocalPort > 0) ports.Add(e.LocalPort);
		}

		foreach (var e in http)
		{
			var path = string.IsNullOrEmpty(e.Path) ? "/" : e.Path;
			paths.Add(path);
			if (!queries.TryGetValue(path, out var qs))
			{
				qs = new HashSet<string>(StringComparer.Ordinal);
				queries[path] = qs;
			}
			qs.Add(e.Query ?? "");

			if (CanaryHit(path, canary) && seenHard.Add("canary"))
				signals.Add(new Signal { Name = "canary", Weight = 100, Detail = "hit " + canary, Hard = true });

			var fam = UaFamily(e.UserAgent);
			if (fam is not ("empty" or "other")) families.Add(fam);
			if (IsScanner(fam) && seenHard.Add("scanner"))
				signals.Add(new Signal { Name = "scanner_ua", Weight = 100, Detail = "family " + fam, Hard = true });

			var hk = HeaderKey(e.Headers);
			headerCounts.TryGetValue(hk, out var hc);
			headerCounts[hk] = hc + 1;
			var joined = "," + hk + ",";
			if (joined.Contains(",sec-fetch-site,", StringComparison.Ordinal)) browser++;
			if (joined.Contains(",cookie,", StringComparison.Ordinal) && !joined.Contains(",sec-fetch-", StringComparison.Ordinal))
				cookieBare++;
			if (e.Status >= 400 && e.Status < 500) status4++;
		}

		foreach (var e in items.Where(e => e.Kind != ObsKind.Http))
		{
			var fam = UaFamily(e.UserAgent);
			if (fam is not ("empty" or "other")) families.Add(fam);
		}

		if (families.Contains("go"))
			signals.Add(Soft("go_client", 20, "Go-http-client, the platform default when no target User-Agent is set"));
		if (families.Contains("curl") || families.Contains("python"))
			signals.Add(Soft("shell_http", 8, "curl or python-requests"));
		if (families.Count >= 2)
			signals.Add(Soft("mixed_clients", 25, "families " + string.Join(", ", families.OrderBy(f => f))));

		// A browser refreshing a page leaves the same gaps as a slow model.
		// Those timing signals stay on scripted clients. A spoofed browser
		// user-agent still loses to a hard signal.
		var browserOnly = families.Count == 1 && families.Contains("browser");
		var pace = "";
		var paceDetail = "";
		var cycle = "";
		double thinkMedian = 0;
		var thinkGaps = 0;
		if (http.Count > 0 && !browserOnly)
		{
			(pace, paceDetail) = ToolPace(http);
			if (pace.Length > 0)
				signals.Add(Soft("paced", 15, paceDetail));

			string cycleDetail;
			(cycle, thinkMedian, thinkGaps, cycleDetail) = ThinkCycles(http);
			if (cycle == "slow")
				signals.Add(Soft("slow_cycle", 35, cycleDetail));
			else if (cycle == "fast")
				signals.Add(Soft("fast_cycle", 30, cycleDetail));

			var phases = Phases(http);
			if (phases.Distinct >= 2 && phases.Solid >= 2)
				signals.Add(Soft("episodic", 25, "bursts " + string.Join(" -> ", phases.Names)));
		}

		if (http.Count >= 12 && paths.Count > 0)
		{
			var uniq = paths.Count / (double)http.Count;
			var miss = status4 / (double)http.Count;
			if (uniq >= 0.75 && miss >= 0.40)
				signals.Add(Soft("breadth", 20, $"{paths.Count} paths, {status4} misses"));
		}

		var maxQ = queries.Count == 0 ? 0 : queries.Max(kv => kv.Value.Count);
		if (maxQ >= 8)
			signals.Add(Soft("depth", 15, $"{maxQ} distinct queries on one path"));

		if (http.Count >= 10)
		{
			var top = 0;
			foreach (var kv in headerCounts)
			{
				if (!InformativeHeaders(kv.Key)) continue;
				if (kv.Value > top) top = kv.Value;
			}
			if (top > 0 && top / (double)http.Count >= 0.80 && browser == 0)
				signals.Add(Soft("frozen_headers", 15, "one header set on most requests, no browser fetch metadata"));
		}
		if (http.Count >= 8 && cookieBare >= 6 && paths.Count >= 6)
			signals.Add(Soft("cookie_replay", 15, "one cookie style across many paths, no browser fetch metadata"));
		if (http.Count >= 5 && browser / (double)http.Count >= 0.70)
			signals.Add(Soft("browser_hints", -25, "sec-fetch-site on most requests"));

		var swept = PortSweep(items);
		if (swept >= 6)
			signals.Add(Soft("port_sweep", 50, swept + " local ports in under two minutes"));

		if (http.Count == 0 && ports.Count == 1)
		{
			var burst = PeakCount(items, 60);
			if (burst >= 12)
				signals.Add(Soft("repeat_connect", 30, burst + " connections to :" + ports.First() + " in one minute, no request detail yet"));
		}

		var score = signals.Where(s => !s.Hard).Sum(s => s.Weight);
		if (score < 0) score = 0;
		if (score > 100) score = 100;
		var hard = signals.Any(s => s.Hard);
		var band = score >= 70 ? "block" : score >= 45 ? "challenge" : score >= 25 ? "watch" : "allow";
		if (hard)
		{
			band = "block";
			if (score < 70) score = 70;
		}

		var verdict = new Verdict
		{
			Src = src,
			Score = score,
			Band = band,
			Gray = !hard && score >= 25 && score <= 69,
			Hard = hard,
			Count = items.Count,
			SpanSec = items.Count < 2 ? 0 : (items[^1].Utc - items[0].Utc).TotalSeconds,
			Pace = pace,
			Cycle = cycle,
			ThinkMedianSec = thinkMedian,
			ThinkGaps = thinkGaps,
			Signals = signals,
			Ports = ports.OrderBy(p => p).ToList(),
			Families = families.Count == 0 ? "none" : string.Join(",", families.OrderBy(f => f)),
			UpdatedUtc = DateTime.UtcNow
		};
		var (action, why) = Recommend(verdict);
		verdict.Action = action;
		verdict.Why = why;
		return verdict;
	}

	static (string Action, string Why) Recommend(Verdict v)
	{
		if (v.Band == "allow")
			return ("none", "The session sits in the allow band.");
		if (v.Band == "watch")
			return ("watch", "Keep this source. The score is not high enough to act.");
		if (v.Band == "block")
			return ("block", "Drop new connections from this source.");
		if (v.Signals.Any(s => s.Name is "slow_cycle" or "episodic"))
			return ("tarpit", "A slow reply spends the quiet time while their model is already the slow part. A 429 only brakes a client that bothers to read it.");
		if (v.Signals.Any(s => s.Name == "port_sweep"))
			return ("block", "A browser check does not stop a port sweep. Drop new connections from this source.");
		return ("challenge", "A browser check stops a bare HTTP client. A worker driving a real browser can pass it. A pass means watch that source more closely.");
	}

	static (string Pace, string Detail) ToolPace(List<Observation> items)
	{
		if (items.Count < 10) return ("", "");
		var gaps = ToolGaps(items);
		if (gaps.Count < 6) return ("", "");
		var tail = gaps.Count > 8 ? gaps.Skip(4).ToList() : gaps;
		var median = Median(tail);
		var perSec = PeakPerSecond(items);
		var name = median >= 0.40 && median <= 0.95 && perSec <= 4 ? "quiet"
			: median >= 0.14 && median <= 0.35 && perSec <= 8 ? "balanced"
			: median >= 0.03 && median <= 0.12 && perSec <= 25 ? "aggressive"
			: "";
		if (name.Length == 0) return ("", "");
		return (name, $"tool gap {Math.Round(median * 1000)} ms, peak {perSec}/s, matches the {name} governor");
	}

	static (string Cycle, double MedianSec, int Gaps, string Detail) ThinkCycles(List<Observation> items)
	{
		if (items.Count < 3) return ("", 0, 0, "");
		var thinks = new List<double>();
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			if (g >= Timing.FastestThinkSec && g <= Timing.SlowestThinkSec)
				thinks.Add(g);
		}
		if (thinks.Count < 2) return ("", 0, thinks.Count, "");
		var med = Median(thinks);
		var kind = med >= Timing.TypicalThinkSec ? "slow" : "fast";
		var who = kind == "slow"
			? "slow local model, the common home-PC case"
			: "fast local model";
		var detail = $"{thinks.Count} pauses, median {FormatGap(med)}, {who}";
		return (kind, med, thinks.Count, detail);
	}

	static (List<string> Names, int Distinct, int Solid) Phases(List<Observation> items)
	{
		var bursts = SplitBursts(items);
		var names = new List<string>();
		var solid = 0;
		foreach (var b in bursts)
		{
			names.Add(Phase(b));
			if (b.Count >= 3) solid++;
		}
		return (names, names.Distinct().Count(), solid);
	}

	static List<List<Observation>> SplitBursts(List<Observation> items)
	{
		var bursts = new List<List<Observation>>();
		if (items.Count == 0) return bursts;
		var cur = new List<Observation> { items[0] };
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			var portChanged = items[i].LocalPort != items[i - 1].LocalPort && items[i].LocalPort > 0 && items[i - 1].LocalPort > 0;
			if (g > Timing.SlowestThinkSec || g >= Timing.FastestThinkSec || portChanged)
			{
				bursts.Add(cur);
				cur = [];
			}
			cur.Add(items[i]);
		}
		if (cur.Count > 0) bursts.Add(cur);
		return bursts;
	}

	static string Phase(List<Observation> burst)
	{
		var http = burst.Where(e => e.Kind == ObsKind.Http).ToList();
		var use = http.Count > 0 ? http : burst;
		if (use.Count == 0) return "empty";
		var paths = use.Select(e => string.IsNullOrEmpty(e.Path) ? "/" : e.Path).Distinct().Count();
		var maxQ = use.GroupBy(e => string.IsNullOrEmpty(e.Path) ? "/" : e.Path)
			.Select(g => g.Select(e => e.Query ?? "").Distinct().Count())
			.DefaultIfEmpty(0).Max();
		var miss = use.Count(e => e.Status >= 400);
		if (paths <= 2 && maxQ >= 5 && use.Count >= 6) return "depth";
		if (paths >= 6 && miss / (double)use.Count >= 0.45) return "breadth";
		if (use.Count <= 6) return "probe";
		return "mixed";
	}

	static List<double> ToolGaps(List<Observation> items)
	{
		var gaps = new List<double>();
		for (var i = 1; i < items.Count; i++)
		{
			var g = (items[i].Utc - items[i - 1].Utc).TotalSeconds;
			if (g >= 0 && g < Timing.FastestThinkSec) gaps.Add(g);
		}
		return gaps;
	}

	static int PeakPerSecond(List<Observation> items)
	{
		var max = 1;
		for (var i = 0; i < items.Count; i++)
		{
			var c = 0;
			for (var j = i; j < items.Count; j++)
			{
				if ((items[j].Utc - items[i].Utc).TotalSeconds > 1) break;
				c++;
			}
			if (c > max) max = c;
		}
		return max;
	}

	static double Median(List<double> values)
	{
		if (values.Count == 0) return 0;
		var s = values.OrderBy(v => v).ToList();
		var mid = s.Count / 2;
		if (s.Count % 2 == 1) return s[mid];
		return (s[mid - 1] + s[mid]) / 2;
	}

	static string FormatGap(double sec)
	{
		if (sec >= 90) return Math.Round(sec / 60.0, 1) + " min";
		return Math.Round(sec) + "s";
	}

	static bool CanaryHit(string path, string canary)
	{
		if (string.IsNullOrWhiteSpace(canary)) return false;
		var a = path.TrimEnd('/');
		var b = canary.TrimEnd('/');
		if (a.Length == 0) a = "/";
		if (b.Length == 0) b = "/";
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	public static string UaFamily(string? ua)
	{
		var u = (ua ?? "").ToLowerInvariant();
		if (u.Contains("nuclei")) return "nuclei";
		if (u.Contains("sqlmap")) return "sqlmap";
		if (u.Contains("ffuf") || u.Contains("fuzz faster u fool")) return "ffuf";
		if (u.Contains("dalfox")) return "dalfox";
		if (u.Contains("katana")) return "katana";
		if (u.Contains("httpx")) return "httpx";
		if (u.Contains("nikto") || u.Contains("nmap") || u.Contains("masscan") || u.Contains("gobuster") || u.Contains("dirbuster") || u.Contains("wpscan") || u.Contains("zgrab"))
			return "scanner";
		if (u.Contains("go-http-client")) return "go";
		if (u.Contains("python-requests")) return "python";
		if (u.Contains("curl/")) return "curl";
		if (u.Contains("mozilla/")) return "browser";
		if (u.Length == 0) return "empty";
		return "other";
	}

	static bool IsScanner(string fam) =>
		fam is "nuclei" or "sqlmap" or "ffuf" or "dalfox" or "katana" or "httpx" or "scanner";

	static string HeaderKey(string[] headers) =>
		string.Join(",", headers.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().OrderBy(h => h));

	// The live HTTP sensor stores the user-agent name and nothing else.
	// That single name is not a frozen client and cannot prove a browser.
	static bool InformativeHeaders(string key)
	{
		if (key.Length == 0) return false;
		var comma = key.IndexOf(',');
		if (comma > 0 && comma < key.Length - 1) return true;
		return !key.Equals("user-agent", StringComparison.Ordinal);
	}

	// Six or more listening ports inside two minutes, and no single port
	// holding the conversation. A PC using SMB all afternoon is not a sweep.
	static int PortSweep(List<Observation> items)
	{
		var best = 0;
		for (var i = 0; i < items.Count; i++)
		{
			var counts = new Dictionary<int, int>();
			for (var j = i; j < items.Count; j++)
			{
				if ((items[j].Utc - items[i].Utc).TotalSeconds > 120) break;
				var port = items[j].LocalPort;
				if (port <= 0) continue;
				counts.TryGetValue(port, out var n);
				counts[port] = n + 1;
			}
			if (counts.Count < 6) continue;
			var busiest = 0;
			foreach (var n in counts.Values)
				if (n > busiest) busiest = n;
			if (busiest <= 3 && counts.Count > best) best = counts.Count;
		}
		return best;
	}

	static int PeakCount(List<Observation> items, double windowSec)
	{
		var max = 0;
		for (var i = 0; i < items.Count; i++)
		{
			var count = 0;
			for (var j = i; j < items.Count; j++)
			{
				if ((items[j].Utc - items[i].Utc).TotalSeconds > windowSec) break;
				count++;
			}
			if (count > max) max = count;
		}
		return max;
	}

	static Signal Soft(string name, int weight, string detail) =>
		new() { Name = name, Weight = weight, Detail = detail, Hard = false };

	public static string SelfCheck()
	{
		var failures = new List<string>();
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		var quiet = new List<Observation>();
		for (var i = 0; i < 12; i++)
			quiet.Add(Http(t0.AddSeconds(i * 0.6), "/"));
		var qv = Score("q", quiet, "");
		if (qv.Pace != "quiet") failures.Add("quiet pace got " + qv.Pace);
		if (qv.Cycle.Length != 0) failures.Add("quiet stream counted a model cycle");

		var slow = new List<Observation>
		{
			Http(t0, "/"),
			Http(t0.AddSeconds(90), "/a"),
			Http(t0.AddSeconds(200), "/b"),
			Http(t0.AddSeconds(400), "/c")
		};
		var sv = Score("s", slow, "");
		if (sv.Cycle != "slow") failures.Add("90s pauses got cycle " + sv.Cycle);
		if (!sv.Signals.Any(s => s.Name == "slow_cycle")) failures.Add("missing slow_cycle");

		var fast = new List<Observation>();
		for (var b = 0; b < 3; b++)
		{
			var start = t0.AddSeconds(b * 8);
			for (var i = 0; i < 8; i++)
				fast.Add(Http(start.AddSeconds(i * 0.05), "/p" + b));
		}
		var fv = Score("f", fast, "");
		if (fv.Cycle != "fast") failures.Add("3s-class pauses got cycle " + fv.Cycle + " median " + fv.ThinkMedianSec);
		if (fv.Pace != "aggressive") failures.Add("aggressive burst got pace " + fv.Pace);

		var idle = new List<Observation>
		{
			Http(t0, "/"),
			Http(t0.AddMinutes(20), "/"),
			Http(t0.AddMinutes(40), "/")
		};
		var iv = Score("i", idle, "");
		if (iv.Cycle.Length != 0) failures.Add("20 min idle counted as a model cycle");

		var scan = new List<Observation> { Http(t0, "/", ua: "sqlmap/1.8") };
		if (Score("x", scan, "").Band != "block") failures.Add("scanner ua was not a block");
		if (!qv.Signals.Any(s => s.Name == "frozen_headers")) failures.Add("two header names did not freeze");

		var phone = new List<Observation>();
		for (var load = 0; load < 5; load++)
		{
			var start = t0.AddMinutes(load * 4);
			for (var c = 0; c < 6; c++)
				phone.Add(Tcp(start.AddMilliseconds(c * 500), 8080));
		}
		var phoneScore = Score("phone", phone, "");
		if (phoneScore.Band != "allow" || phoneScore.Action == "block")
			failures.Add("phone page loads scored " + phoneScore.Score + " " + phoneScore.Band);
		if (phoneScore.Signals.Any(s => s.Name is "slow_cycle" or "paced" or "port_sweep" or "repeat_connect"))
			failures.Add("phone page loads looked scripted");

		var lan = new List<Observation>();
		int[] lanPorts = [135, 139, 445, 5040, 5357, 7680];
		for (var i = 0; i < lanPorts.Length; i++)
		{
			lan.Add(Tcp(t0.AddMinutes(i * 5), lanPorts[i]));
			lan.Add(Tcp(t0.AddMinutes(i * 5).AddSeconds(1), lanPorts[i]));
		}
		var lanScore = Score("lan", lan, "");
		if (lanScore.Action == "block" || lanScore.Signals.Any(s => s.Name == "port_sweep"))
			failures.Add("lan services over 30 min scored " + lanScore.Score);

		var chatty = new List<Observation>();
		foreach (var port in lanPorts)
			chatty.Add(Tcp(t0, port));
		for (var i = 0; i < 8; i++)
			chatty.Add(Tcp(t0.AddMilliseconds(200 * i), 445));
		if (Score("smb", chatty, "").Signals.Any(s => s.Name == "port_sweep"))
			failures.Add("smb fan-out counted as a sweep");

		var poll = new List<Observation>();
		for (var i = 0; i < 40; i++)
			poll.Add(Tcp(t0.AddSeconds(i * 30), 5357));
		var pollScore = Score("poll", poll, "");
		if (pollScore.Band is "challenge" or "block" || pollScore.Action is "challenge" or "block")
			failures.Add("30s poll scored " + pollScore.Score + " " + pollScore.Band);

		var sweep = new List<Observation>();
		for (var p = 0; p < 6; p++)
			sweep.Add(Tcp(t0.AddSeconds(p), 2000 + p));
		var sweepScore = Score("sweep", sweep, "");
		if (sweepScore.Action != "block" || !sweepScore.Signals.Any(s => s.Name == "port_sweep"))
			failures.Add("six ports in six seconds was " + sweepScore.Action);

		var browserLoads = new List<Observation>();
		for (var load = 0; load < 5; load++)
		{
			var start = t0.AddMinutes(load * 4);
			for (var c = 0; c < 4; c++)
				browserLoads.Add(Http(start.AddMilliseconds(c * 40), "/ui", ua: "Mozilla/5.0 (iPhone)", headers: ["user-agent"]));
		}
		var browserScore = Score("browser", browserLoads, "");
		if (browserScore.Band == "block" || browserScore.Action == "block")
			failures.Add("browser ui scored " + browserScore.Score);
		if (browserScore.Signals.Any(s => s.Name is "frozen_headers" or "slow_cycle" or "paced" or "browser_hints" or "cookie_replay"))
			failures.Add("browser ui used a dead header signal");

		var hinted = new List<Observation>();
		for (var i = 0; i < 6; i++)
			hinted.Add(Http(t0.AddSeconds(i), "/", ua: "Mozilla/5.0", headers: ["accept", "sec-fetch-site", "user-agent"]));
		if (!Score("hints", hinted, "").Signals.Any(s => s.Name == "browser_hints"))
			failures.Add("sec-fetch-site did not credit the browser");

		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}

	static Observation Http(DateTime t, string path, string ua = "Go-http-client/1.1", string[]? headers = null) => new()
	{
		Utc = t,
		Src = "203.0.113.8",
		LocalPort = 80,
		Kind = ObsKind.Http,
		Path = path,
		Method = "GET",
		Status = 200,
		UserAgent = ua,
		Headers = headers ?? ["accept", "user-agent"]
	};

	static Observation Tcp(DateTime t, int port) => new()
	{
		Utc = t,
		Src = "198.51.100.4",
		LocalPort = port,
		Kind = ObsKind.Tcp,
		Method = "TCP"
	};
}

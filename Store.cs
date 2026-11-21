namespace Guardex;

// Sessions live here. A host sensor fills them today. A gateway sensor can call Add later.
public sealed class SessionStore
{
	readonly object _gate = new();
	readonly Dictionary<string, List<Observation>> _events = new(StringComparer.Ordinal);
	readonly Dictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);
	readonly List<FeedItem> _feed = [];
	readonly List<int> _listenPorts = [];
	readonly List<Finding> _findings = [];
	readonly List<BlockRow> _blocks = [];
	readonly List<RemoteRow> _remote = [];
	string _enforce = "reporting";
	string _posture = "not checked yet";
	string _remoteAlert = "";
	string _launchAlert = "";
	string _flowAlert = "";
	string _clipAlert = "";
	string _ask = "";
	string _askAbout = "";
	string _askId = "";
	string _askSubject = "";
	string _remoteStatus = "";
	bool _stopUnlisted = true;
	int _generation;

	public string TcpStatus { get; private set; } = "starting";
	public string HttpStatus { get; private set; } = "starting";
	public string ModelStatus { get; private set; } = "idle";

	public int Generation
	{
		get { lock (_gate) return _generation; }
	}

	public void SetModel(string status)
	{
		lock (_gate)
		{
			if (ModelStatus == status) return;
			ModelStatus = status;
			_generation++;
		}
	}

	public void SetStatus(bool tcp, string status)
	{
		lock (_gate)
		{
			if (tcp)
			{
				if (TcpStatus == status) return;
				TcpStatus = status;
			}
			else
			{
				if (HttpStatus == status) return;
				HttpStatus = status;
			}
			_generation++;
		}
	}

	public void SetListenPorts(IReadOnlyCollection<int> ports)
	{
		lock (_gate)
		{
			if (ports.Count == _listenPorts.Count && ports.All(p => _listenPorts.Contains(p))) return;
			_listenPorts.Clear();
			_listenPorts.AddRange(ports.OrderBy(p => p));
			_generation++;
		}
	}

	internal Verdict? Add(Observation obs)
	{
		lock (_gate)
		{
			if (!_events.TryGetValue(obs.Src, out var list))
			{
				list = [];
				_events[obs.Src] = list;
			}
			list.Add(obs);
			Trim(list);
			var verdict = Scorer.Score(obs.Src, list, Canary);
			_verdicts[obs.Src] = verdict;
			var where = obs.Kind == ObsKind.Http
				? $"{obs.Method} {obs.Path} {obs.Status}"
				: $"tcp :{obs.LocalPort} {obs.LocalProcess}".Trim();
			_feed.Add(new FeedItem { Utc = obs.Utc, Text = $"{obs.Src}  {where}" });
			if (_feed.Count > 80) _feed.RemoveRange(0, _feed.Count - 80);
			_generation++;
			return verdict;
		}
	}

	public void ApplyJudge(string src, int scoreAtAsk, string label, string reason, string band)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var v)) return;
			if (v.Score != scoreAtAsk) return;
			v.Judge = label;
			v.Reason = reason;
			if (!string.IsNullOrEmpty(band)) v.Band = band;
			var (action, why) = RecommendFrom(v);
			v.Action = action;
			v.Why = why;
			_generation++;
		}
	}

	static (string Action, string Why) RecommendFrom(Verdict v)
	{
		if (v.Band == "allow") return ("none", "The session sits in the allow band.");
		if (v.Band == "watch") return ("watch", "Keep this source. The score is not high enough to act.");
		if (v.Band == "block") return ("block", "Drop new connections from this source.");
		if (v.Signals.Any(s => s.Name is "slow_cycle" or "episodic" or "fast_cycle"))
			return ("tarpit", "A slow reply spends the pause while their model thinks. Guardex does not apply it.");
		if (v.Signals.Any(s => s.Name == "port_sweep"))
			return ("block", "A browser check does not stop a port sweep. Drop new connections from this source.");
		return ("challenge", "A browser check stops a bare HTTP client. A worker driving a real browser can pass it.");
	}

	public string Canary { get; set; } = "";

	public IReadOnlyList<(string Src, int Score, bool Gray)> GrayCandidates()
	{
		lock (_gate)
		{
			return _verdicts.Values
				.Where(v => v.Gray)
				.Select(v => (v.Src, v.Score, v.Gray))
				.ToList();
		}
	}

	public string Digest(string src)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var v)) return "";
			var sig = string.Join(", ", v.Signals.Select(s => $"{s.Name}({s.Weight})"));
			if (sig.Length == 0) sig = "none";
			return $"""
				[[guardex-digest]]
				src={v.Src} n={v.Count} span_sec={Math.Round(v.SpanSec)} ports={string.Join(",", v.Ports)}
				pace={v.Pace} cycle={v.Cycle} think_median_sec={Math.Round(v.ThinkMedianSec)} think_gaps={v.ThinkGaps}
				window_min=60 fastest_think_sec=2.5 typical_think_sec=8
				rule_score={v.Score} rule_band={v.Band} gray={v.Gray} ua={v.Families}
				signals={sig}
				[[/guardex-digest]]
				""";
		}
	}

	static void Trim(List<Observation> list)
	{
		var cutoff = DateTime.UtcNow.AddSeconds(-Timing.WindowSec);
		list.RemoveAll(e => e.Utc < cutoff);
		if (list.Count > Timing.MaxEventsPerSource)
			list.RemoveRange(0, list.Count - Timing.MaxEventsPerSource);
	}

	public Dash Copy()
	{
		lock (_gate)
		{
			var sources = _verdicts.Values
				.OrderByDescending(v => v.Score)
				.ThenBy(v => v.Src)
				.Take(100)
				.Select(ToRow)
				.ToList();
			var feed = _feed.TakeLast(40)
				.Select(f => f.Utc.ToLocalTime().ToString("HH:mm:ss") + "  " + f.Text)
				.ToList();
			return new Dash
			{
				Summary = Timing.Summary,
				Tcp = TcpStatus,
				Http = HttpStatus,
				Model = ModelStatus,
				ListenCount = _listenPorts.Count,
				ListenText = string.Join(", ", _listenPorts.Take(24)),
				Sources = sources,
				Feed = feed,
				Enforce = _enforce,
				Posture = _posture,
				RemoteAlert = _remoteAlert,
				GuardAlert = Join(Join(_launchAlert, _flowAlert), _clipAlert),
				Ask = _ask,
				AskAbout = _askAbout,
				AskId = _askId,
				AskSubject = _askSubject,
				Banner = Join(Join(Join(_launchAlert, _flowAlert), _clipAlert), _remoteAlert),
				RemoteStatus = _remoteStatus,
				StopUnlisted = _stopUnlisted,
				Blocks = _blocks.ToList(),
				Findings = _findings.ToList(),
				Remote = _remote.ToList()
			};
		}
	}

	public void Note(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		text = text.Trim();
		var at = DateTime.UtcNow;
		lock (_gate)
		{
			_feed.Add(new FeedItem { Utc = at, Text = text });
			if (_feed.Count > 80) _feed.RemoveRange(0, _feed.Count - 80);
			_generation++;
		}
		Journal.Append(at, text);
	}

	internal Verdict? CopyVerdict(string src)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var v)) return null;
			return new Verdict
			{
				Src = v.Src,
				Score = v.Score,
				Band = v.Band,
				Gray = v.Gray,
				Hard = v.Hard,
				Count = v.Count,
				SpanSec = v.SpanSec,
				Pace = v.Pace,
				Cycle = v.Cycle,
				ThinkMedianSec = v.ThinkMedianSec,
				ThinkGaps = v.ThinkGaps,
				Signals = v.Signals,
				Action = v.Action,
				Why = v.Why,
				Judge = v.Judge,
				Reason = v.Reason,
				Ports = v.Ports,
				Families = v.Families,
				UpdatedUtc = v.UpdatedUtc
			};
		}
	}

	public List<Finding> CopyFindings()
	{
		lock (_gate) return _findings.ToList();
	}

	public void SetEnforce(string status)
	{
		lock (_gate)
		{
			if (_enforce == status) return;
			_enforce = status;
			_generation++;
		}
	}

	public void SetFindings(List<Finding> items, string posture)
	{
		lock (_gate)
		{
			if (_posture == posture && SameFindings(_findings, items)) return;
			_findings.Clear();
			_findings.AddRange(items);
			_posture = posture;
			_generation++;
		}
	}

	public void ApplyHostJudge(string id, string judge, string reason, string severity)
	{
		lock (_gate)
		{
			var item = _findings.FirstOrDefault(f => f.Id == id);
			if (item == null) return;
			if (item.Severity == "block") severity = "block";
			if (item.Judge == judge && item.Reason == reason && item.Severity == severity) return;
			var rose = item.Severity != "block" && severity == "block";
			item.Judge = judge;
			item.Reason = reason;
			item.Severity = severity;
			var planted = rose ? item.Name + " looks planted" : "";
			if (rose)
			{
				_feed.Add(new FeedItem { Utc = DateTime.UtcNow, Text = planted });
				if (_feed.Count > 80) _feed.RemoveRange(0, _feed.Count - 80);
			}
			_generation++;
			if (planted.Length > 0) Journal.Append(DateTime.UtcNow, planted);
		}
	}

	public void SetBlocks(List<BlockRow> items)
	{
		lock (_gate)
		{
			if (SameBlocks(_blocks, items)) return;
			_blocks.Clear();
			_blocks.AddRange(items);
			_generation++;
		}
	}

	public void SetRemote(List<RemoteRow> rows, string alert, bool stopUnlisted)
	{
		lock (_gate)
		{
			if (_remoteAlert == alert && _stopUnlisted == stopUnlisted && SameRemote(_remote, rows)) return;
			_remote.Clear();
			_remote.AddRange(rows);
			_remoteAlert = alert;
			_stopUnlisted = stopUnlisted;
			_generation++;
		}
	}

	public void SetLaunchAlert(string text) => Hold(ref _launchAlert, text);

	public void SetFlowAlert(string text) => Hold(ref _flowAlert, text);

	public void SetClipAlert(string text) => Hold(ref _clipAlert, text);

	public void SetAsk(string text, string about, string id) => SetAsk(text, about, id, null);

	public void SetAsk(string text, string about, string id, string? subject)
	{
		lock (_gate)
		{
			var next = subject ?? _askSubject;
			if (_ask == text && _askAbout == about && _askId == id && _askSubject == next) return;
			_ask = text;
			_askAbout = about;
			_askId = id;
			_askSubject = next;
			_generation++;
		}
	}

	internal (int Score, string Band, bool Hard)? Client(string src)
	{
		lock (_gate)
		{
			if (!_verdicts.TryGetValue(src, out var verdict)) return null;
			return (verdict.Score, verdict.Band, verdict.Hard);
		}
	}

	void Hold(ref string slot, string text)
	{
		lock (_gate)
		{
			if (slot == text) return;
			slot = text;
			_generation++;
		}
	}

	static string Join(string left, string right)
	{
		if (left.Length == 0) return right;
		if (right.Length == 0) return left;
		return left + " " + right;
	}

	public void SetRemoteStatus(string text)
	{
		lock (_gate)
		{
			if (_remoteStatus == text) return;
			_remoteStatus = text;
			_generation++;
		}
	}

	static bool SameFindings(List<Finding> left, List<Finding> right)
	{
		if (left.Count != right.Count) return false;
		for (var i = 0; i < left.Count; i++)
		{
			if (left[i].Id != right[i].Id || left[i].Severity != right[i].Severity || left[i].Why != right[i].Why || left[i].Judge != right[i].Judge || left[i].Reason != right[i].Reason)
				return false;
		}
		return true;
	}

	static bool SameBlocks(List<BlockRow> left, List<BlockRow> right)
	{
		if (left.Count != right.Count) return false;
		for (var i = 0; i < left.Count; i++)
		{
			if (left[i].Src != right[i].Src || left[i].State != right[i].State || left[i].Until != right[i].Until || left[i].Scenario != right[i].Scenario || left[i].Note != right[i].Note)
				return false;
		}
		return true;
	}

	static bool SameRemote(List<RemoteRow> left, List<RemoteRow> right)
	{
		if (left.Count != right.Count) return false;
		for (var i = 0; i < left.Count; i++)
		{
			if (left[i].Id != right[i].Id || left[i].Allowed != right[i].Allowed || left[i].State != right[i].State || left[i].Line != right[i].Line)
				return false;
		}
		return true;
	}

	static SourceRow ToRow(Verdict v)
	{
		var lines = new List<string>
		{
			$"{v.Band}  {v.Score}",
			$"{v.Count} events over {Math.Round(v.SpanSec)}s. Ports {(v.Ports.Count == 0 ? "none" : string.Join(", ", v.Ports))}.",
			v.ThinkGaps > 0
				? $"Pauses {v.ThinkGaps}, median {Math.Round(v.ThinkMedianSec)}s. Pace {(v.Pace.Length == 0 ? "none" : v.Pace)}."
				: "No model-sized pause in the window.",
			$"Action {v.Action}. {v.Why}"
		};
		if (v.Judge.Length > 0) lines.Add("Model " + v.Judge + ". " + v.Reason);
		foreach (var s in v.Signals)
			lines.Add($"{s.Weight,4}  {s.Name}  {s.Detail}");
		return new SourceRow
		{
			Src = v.Src,
			Score = v.Score,
			Band = v.Band,
			Cycle = v.Cycle.Length > 0 ? v.Cycle : v.Pace,
			Count = v.Count,
			Detail = string.Join("\n", lines)
		};
	}
}

public sealed class Dash
{
	public string Summary { get; init; } = "";
	public string Tcp { get; init; } = "";
	public string Http { get; init; } = "";
	public string Model { get; init; } = "";
	public int ListenCount { get; init; }
	public string ListenText { get; init; } = "";
	public List<SourceRow> Sources { get; init; } = [];
	public List<string> Feed { get; init; } = [];
	public string Enforce { get; init; } = "reporting";
	public string Posture { get; init; } = "";
	public string RemoteAlert { get; init; } = "";
	public string GuardAlert { get; init; } = "";
	public string Banner { get; init; } = "";
	public string Ask { get; init; } = "";
	public string AskAbout { get; init; } = "";
	public string AskId { get; init; } = "";
	public string AskSubject { get; init; } = "";
	public string RemoteStatus { get; init; } = "";
	public bool StopUnlisted { get; init; } = true;
	public List<BlockRow> Blocks { get; init; } = [];
	public List<Finding> Findings { get; init; } = [];
	public List<RemoteRow> Remote { get; init; } = [];
	public int BlockCount => Blocks.Count;
	public int FindingCount => Findings.Count;
	public int RemoteHits => Remote.Count(r => r.State is "Stopped" or "Needs admin" or "Running" or "Watching");
}

public sealed class SourceRow
{
	public string Src { get; init; } = "";
	public int Score { get; init; }
	public string Band { get; init; } = "";
	public string Cycle { get; init; } = "";
	public int Count { get; init; }
	public string Detail { get; init; } = "";
}

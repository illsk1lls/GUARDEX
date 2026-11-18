using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Guardex;

public partial class MainWindow : Window
{
	readonly SessionStore _store;
	readonly DispatcherTimer _timer;
	readonly SolidColorBrush _green = Freeze(143, 181, 115);
	readonly SolidColorBrush _muted = Freeze(142, 160, 136);
	readonly SolidColorBrush _amber = Freeze(214, 177, 90);
	readonly SolidColorBrush _navOn = Freeze(49, 66, 50);
	readonly SolidColorBrush _navOff = Freeze(27, 33, 25);
	bool _allowClose;
	bool _blocking;
	string _page = "traffic";
	string _selected = "";
	string _block = "";
	string _finding = "";
	string _srcSig = "";
	string _blockSig = "";
	string _findSig = "";
	string _remoteSig = "";
	int _gen = -1;
	readonly ObservableCollection<LinkRow> _siteLinks = [];
	readonly List<SiteCall> _siteCalls = [];
	readonly HashSet<string> _siteSeen = new(StringComparer.Ordinal);
	string _sitePage = "";
	bool _siteReady;

	public event Action<string>? ConnectRequested;
	public event Action<string>? EndpointEdited;
	public event Action<bool>? EnforceRequested;
	public event Action<string>? ReleaseRequested;
	public event Action<string, bool>? AllowChanged;
	public event Action? AllowNoneRequested;
	public event Action<bool>? StopUnlistedRequested;
	public event Action<bool>? AskAnswered;

	public MainWindow(SessionStore store)
	{
		_store = store;
		InitializeComponent();
		SourceInitialized += (_, _) =>
		{
			var source = (HwndSource?)PresentationSource.FromVisual(this);
			source?.AddHook(WindowProc);
		};
		SiteLinks.ItemsSource = _siteLinks;
		Show("traffic");
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_timer.Tick += (_, _) => RefreshNow();
		_timer.Start();
	}

	public void AllowClose() => _allowClose = true;

	public void ShowEndpoint(string url, bool ready, string model, string status)
	{
		if (!Endpoint.IsKeyboardFocusWithin)
			Endpoint.Text = url;
		var name = string.IsNullOrWhiteSpace(model) ? "none" : model.Trim();
		ModelLine.Text = "Model: " + name;
		var line = string.IsNullOrWhiteSpace(status) ? (ready ? "ready" : "not ready") : status.Trim();
		ModelState.Text = "status: " + line;
		ModelState.Foreground = line == "ready" ? _green : line.StartsWith("loading", StringComparison.OrdinalIgnoreCase) ? _amber : _muted;
	}

	public void SetBusy(bool busy)
	{
		ConnectButton.IsEnabled = !busy;
		ConnectButton.Content = busy ? "Connecting" : "Connect";
	}

	public void RefreshNow()
	{
		var gen = _store.Generation;
		if (gen == _gen && _gen >= 0) return;
		_gen = gen;
		var dash = _store.Copy();
		PaintChip(TcpChip, "TCP", dash.Tcp);
		PaintChip(HttpChip, "HTTP", dash.Http);
		ListenChip.Text = dash.ListenCount == 0 ? "Listening  none" : "Listening  " + dash.ListenCount;
		ListenChip.Foreground = dash.ListenCount == 0 ? _muted : _green;
		PortsLine.Text = dash.ListenCount == 0 ? "No listening ports." : "Listening  " + dash.ListenText;
		_blocking = dash.Enforce == "blocking";
		EnforceState.Text = dash.Enforce switch
		{
			"blocking" => "Blocking",
			"needs admin" => "Needs admin",
			_ => "Reporting"
		};
		EnforceState.Foreground = _blocking ? _green : dash.Enforce == "needs admin" ? _amber : _muted;
		AskBar.Visibility = dash.Ask.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
		AskText.Text = dash.Ask;
		RemoteBanner.Visibility = dash.Banner.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
		RemoteBannerText.Text = dash.Banner;
		NavBlocks.Content = dash.BlockCount == 0 ? "Blocks" : "Blocks  " + dash.BlockCount;
		NavHost.Content = dash.FindingCount == 0 ? "Host" : "Host  " + dash.FindingCount;
		NavRemote.Content = dash.RemoteHits == 0 ? "Remote" : "Remote  " + dash.RemoteHits;
		PaintNav();
		PostureStatus.Text = dash.Posture;
		RemoteHead.Text = !dash.StopUnlisted
			? "Stopping is off. Unchecked tools are reported and left running."
			: dash.Remote.Count(r => r.Allowed) == 0
				? "None allowed. A tool on this list is stopped until you check it."
				: dash.Remote.Count(r => r.Allowed) + " allowed. Anything unchecked is stopped.";
		RemoteStatus.Text = dash.RemoteStatus;
		if (!StopUnlisted.IsKeyboardFocusWithin && StopUnlisted.IsChecked != dash.StopUnlisted)
			StopUnlisted.IsChecked = dash.StopUnlisted;

		var srcSig = string.Join("\n", dash.Sources.Select(r => r.Src + "|" + r.Score + "|" + r.Band + "|" + r.Cycle + "|" + r.Detail));
		Bind(Sources, dash.Sources, ref _srcSig, srcSig, _selected);
		if (Sources.SelectedItem is SourceRow selected)
			Detail.Text = selected.Detail;
		else if (dash.Sources.Count == 0)
			Detail.Text = "No inbound clients yet.";
		Feed.Text = dash.Feed.Count == 0 ? "Waiting for traffic." : string.Join(Environment.NewLine, dash.Feed);

		var blockSig = string.Join("\n", dash.Blocks.Select(r => r.Src + "|" + r.State + "|" + r.Until + "|" + r.Scenario + "|" + r.Note));
		Bind(Blocks, dash.Blocks, ref _blockSig, blockSig, _block);
		if (Blocks.SelectedItem is BlockRow block)
			ShowBlock(block);
		else if (dash.Blocks.Count == 0)
			BlockDetail.Text = "No source is blocked.";

		var findSig = string.Join("\n", dash.Findings.Select(r => r.Id + "|" + r.Severity + "|" + r.Why + "|" + r.Judge));
		Bind(Findings, dash.Findings, ref _findSig, findSig, _finding);
		if (Findings.SelectedItem is Finding finding)
			ShowFinding(finding);
		else if (dash.Findings.Count == 0)
			HostDetail.Text = "Nothing in the startup folders, run keys, services, tasks, or the other start locations looks planted.";

		var remoteSig = string.Join("\n", dash.Remote.Select(r => r.Id + "|" + r.Allowed + "|" + r.State + "|" + r.Line));
		if (_remoteSig != remoteSig)
		{
			_remoteSig = remoteSig;
			RemoteList.ItemsSource = dash.Remote;
		}
	}

	static void Bind(System.Windows.Controls.ListBox box, System.Collections.IEnumerable items, ref string sig, string next, string keep)
	{
		if (sig == next) return;
		sig = next;
		box.ItemsSource = items;
		if (keep.Length == 0) return;
		foreach (var item in box.Items)
		{
			var id = item switch
			{
				SourceRow row => row.Src,
				BlockRow block => block.Src,
				Finding finding => finding.Id,
				_ => ""
			};
			if (id == keep)
			{
				box.SelectedItem = item;
				return;
			}
		}
	}

	void PaintChip(TextBlock chip, string name, string status)
	{
		chip.Text = name + "  " + status;
		var lower = status.ToLowerInvariant();
		chip.Foreground = lower.StartsWith("on") || lower.Contains("ready") ? _green
			: lower.Contains("error") || lower.Contains("needs") || lower.Contains("unreachable") || lower.Contains("404") || lower.Contains("timeout") ? _amber
			: _muted;
	}

	void PaintNav()
	{
		foreach (var button in new[] { NavTraffic, NavBlocks, NavHost, NavRemote, NavSite })
		{
			var on = button == NavTraffic && _page == "traffic"
				|| button == NavBlocks && _page == "blocks"
				|| button == NavHost && _page == "host"
				|| button == NavRemote && _page == "remote"
				|| button == NavSite && _page == "site";
			button.Background = on ? _navOn : _navOff;
		}
	}

	void Show(string page)
	{
		_page = page;
		TrafficPage.Visibility = page == "traffic" ? Visibility.Visible : Visibility.Collapsed;
		BlocksPage.Visibility = page == "blocks" ? Visibility.Visible : Visibility.Collapsed;
		HostPage.Visibility = page == "host" ? Visibility.Visible : Visibility.Collapsed;
		RemotePage.Visibility = page == "remote" ? Visibility.Visible : Visibility.Collapsed;
		SitePage.Visibility = page == "site" ? Visibility.Visible : Visibility.Collapsed;
		PaintNav();
	}

	void ShowBlock(BlockRow row)
	{
		BlockDetail.Text = row.State + "  until " + row.Until + "\n" + row.Origin + "  " + row.Scenario + "\n" + row.Why + "\n" + row.Note;
	}

	void OnSelect(object sender, SelectionChangedEventArgs e)
	{
		if (Sources.SelectedItem is not SourceRow row) return;
		_selected = row.Src;
		Detail.Text = row.Detail;
	}

	void OnBlockSelect(object sender, SelectionChangedEventArgs e)
	{
		if (Blocks.SelectedItem is not BlockRow row) return;
		_block = row.Src;
		ShowBlock(row);
	}

	void OnFindingSelect(object sender, SelectionChangedEventArgs e)
	{
		if (Findings.SelectedItem is not Finding row) return;
		_finding = row.Id;
		ShowFinding(row);
	}

	void ShowFinding(Finding row)
	{
		var judge = row.Judge.Length == 0 ? "" : "\nModel " + row.Judge + ". " + row.Reason;
		HostDetail.Text = row.Severity + "  " + row.Kind + "\n" + row.Name + "\n" + row.Location + "\n" + row.Detail + "\n" + row.Why + judge;
	}

	void OnConnect(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke(Endpoint.Text);

	void OnEndpointLostFocus(object sender, RoutedEventArgs e) => EndpointEdited?.Invoke(Endpoint.Text);

	void OnEnforce(object sender, RoutedEventArgs e) => EnforceRequested?.Invoke(!_blocking);

	void OnRelease(object sender, RoutedEventArgs e)
	{
		if (Blocks.SelectedItem is BlockRow row)
			ReleaseRequested?.Invoke(row.Src);
	}

	void OnAllowClick(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.CheckBox box || box.DataContext is not RemoteRow row) return;
		AllowChanged?.Invoke(row.Id, box.IsChecked == true);
	}

	void OnAllowNone(object sender, RoutedEventArgs e) => AllowNoneRequested?.Invoke();

	void OnStopToggle(object sender, RoutedEventArgs e) => StopUnlistedRequested?.Invoke(StopUnlisted.IsChecked == true);

	void OnNav(object sender, RoutedEventArgs e)
	{
		if (sender == NavBlocks) Show("blocks");
		else if (sender == NavHost) Show("host");
		else if (sender == NavRemote) Show("remote");
		else if (sender == NavSite) Show("site");
		else Show("traffic");
	}

	async void OnSiteOpen(object sender, RoutedEventArgs e)
	{
		if (!LinkRules.TryPage(SiteUrl.Text, out var url, out var error))
		{
			SiteStatus.Text = error;
			return;
		}
		SiteButton.IsEnabled = false;
		try
		{
			await EnsureSite();
			_sitePage = url;
			_siteCalls.Clear();
			_siteSeen.Clear();
			_siteLinks.Clear();
			SiteStatus.Text = "Opening " + url;
			SiteView.CoreWebView2.Navigate(url);
		}
		catch (WebView2RuntimeNotFoundException)
		{
			SiteStatus.Text = "The Edge WebView2 runtime is not installed, so this pane cannot open the page.";
		}
		catch (Exception ex)
		{
			SiteStatus.Text = ex.Message;
		}
		finally
		{
			SiteButton.IsEnabled = true;
		}
	}

	async Task EnsureSite()
	{
		if (_siteReady) return;
		var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "site-view");
		Directory.CreateDirectory(folder);
		var env = await CoreWebView2Environment.CreateAsync(null, folder);
		await SiteView.EnsureCoreWebView2Async(env);
		var core = SiteView.CoreWebView2;
		core.Settings.IsPasswordAutosaveEnabled = false;
		core.Settings.IsGeneralAutofillEnabled = false;
		core.DownloadStarting += (_, args) =>
		{
			args.Cancel = true;
			args.Handled = true;
		};
		core.NewWindowRequested += (_, args) =>
		{
			args.Handled = true;
			if (!string.IsNullOrWhiteSpace(args.Uri)) core.Navigate(args.Uri);
		};
		core.NavigationCompleted += (_, args) =>
		{
			if (core.Source != null && !string.IsNullOrWhiteSpace(core.Source))
				_sitePage = core.Source;
			PaintSite(args.IsSuccess ? "" : "The page did not finish loading.");
		};
		await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
		var receiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
		receiver.DevToolsProtocolEventReceived += (_, args) => OnSiteCall(args.ParameterObjectAsJson);
		_siteReady = true;
	}

	void OnSiteCall(string json)
	{
		if (!LinkRules.Read(json, _sitePage, out var url, out var type, out var initiator)) return;
		Dispatcher.BeginInvoke(new Action(() =>
		{
			if (_siteCalls.Count < 400)
				_siteCalls.Add(new SiteCall(url, type, initiator));
			PaintSite("");
		}));
	}

	void PaintSite(string note)
	{
		_siteLinks.Clear();
		_siteSeen.Clear();
		var watch = 0;
		var block = 0;
		foreach (var call in _siteCalls)
		{
			var row = LinkRules.Row(_sitePage, call.Url, call.Type, call.Initiator);
			if (row == null) continue;
			var key = row.Band + "|" + row.Kind + "|" + row.Host + "|" + row.Path;
			if (!_siteSeen.Add(key)) continue;
			if (row.Band == "block")
			{
				block++;
				_siteLinks.Insert(0, row);
			}
			else
			{
				watch++;
				_siteLinks.Add(row);
			}
		}
		var summary = block + " second-hop or address calls. " + watch + " third-party calls.";
		SiteStatus.Text = note.Length == 0 ? summary : note + " " + summary;
	}

	readonly record struct SiteCall(string Url, string Type, string Initiator);

	void OnAskYes(object sender, RoutedEventArgs e) => AskAnswered?.Invoke(true);

	void OnAskNo(object sender, RoutedEventArgs e) => AskAnswered?.Invoke(false);

	void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	void OnMaximize(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	void OnWindowClose(object sender, RoutedEventArgs e) => Close();

	void OnStateChanged(object? sender, EventArgs e) =>
		MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

	IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		const int WmGetMinMaxInfo = 0x0024;
		if (msg == WmGetMinMaxInfo)
		{
			var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
			var monitor = MonitorFromWindow(hwnd, 2);
			var screen = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
			if (GetMonitorInfo(monitor, ref screen))
			{
				var work = screen.rcWork;
				var area = screen.rcMonitor;
				info.ptMaxPosition.X = work.Left - area.Left;
				info.ptMaxPosition.Y = work.Top - area.Top;
				info.ptMaxSize.X = work.Right - work.Left;
				info.ptMaxSize.Y = work.Bottom - work.Top;
				Marshal.StructureToPtr(info, lParam, true);
				handled = true;
			}
		}
		return IntPtr.Zero;
	}

	[DllImport("user32.dll")]
	static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[StructLayout(LayoutKind.Sequential)]
	struct PointWin
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct MinMaxInfo
	{
		public PointWin ptReserved;
		public PointWin ptMaxSize;
		public PointWin ptMaxPosition;
		public PointWin ptMinTrackSize;
		public PointWin ptMaxTrackSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct RectWin
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	struct MonitorInfo
	{
		public int cbSize;
		public RectWin rcMonitor;
		public RectWin rcWork;
		public int dwFlags;
	}

	void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_allowClose) return;
		e.Cancel = true;
		Hide();
	}

	static SolidColorBrush Freeze(byte r, byte g, byte b)
	{
		var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}
}

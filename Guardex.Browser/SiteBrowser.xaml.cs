using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TorrentRow = Guardex.Browser.TorrentRow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Guardex.Browser;
using Guardex.Tunnel;

namespace Guardex;

public sealed class SiteTab : INotifyPropertyChanged
{
	readonly WebView2 _view;
	string _title = "New tab";
	bool _selected;

	public SiteTab(WebView2 view) => _view = view;

	public WebView2 View => _view;
	public List<SiteCall> Calls { get; } = [];
	public string PageUrl { get; set; } = "";
	public string AdScriptId { get; set; } = "";
	public bool Loading { get; set; }

	public string Title
	{
		get => _title;
		set
		{
			var next = string.IsNullOrWhiteSpace(value) ? "New tab" : value;
			if (_title == next) return;
			_title = next;
			Changed(nameof(Title));
		}
	}

	public bool Selected
	{
		get => _selected;
		set
		{
			if (_selected == value) return;
			_selected = value;
			Changed(nameof(Selected));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public readonly record struct SiteCall(string Url, string Type, string Initiator);

public sealed class DownloadRow : INotifyPropertyChanged
{
	string _name;
	string _detail = "Starting";
	double _fraction;
	bool _running = true;
	bool _folder;

	public DownloadRow(string name, string path, CoreWebView2DownloadOperation? operation)
	{
		_name = name;
		Path = path;
		Operation = operation;
	}

	public string Path { get; }
	public CoreWebView2DownloadOperation? Operation { get; }
	public TorrentRow? Torrent { get; set; }
	public bool OpenAfter { get; set; }
	public bool FileSettled { get; set; }
	public string Name { get => _name; set { if (_name == value) return; _name = value; Changed(nameof(Name)); } }
	public string Detail { get => _detail; set { if (_detail == value) return; _detail = value; Changed(nameof(Detail)); } }
	public double Fraction { get => _fraction; set { if (Math.Abs(_fraction - value) < 0.001) return; _fraction = value; Changed(nameof(Fraction)); } }
	public Visibility CancelVisibility => _running ? Visibility.Visible : Visibility.Collapsed;
	public Visibility FolderVisibility => _folder ? Visibility.Visible : Visibility.Collapsed;

	public void SetChrome(bool running, bool folder)
	{
		if (_running == running && _folder == folder) return;
		_running = running;
		_folder = folder;
		Changed(nameof(CancelVisibility));
		Changed(nameof(FolderVisibility));
	}

	public void SetFileEnd(string detail, bool folder)
	{
		Detail = detail;
		if (folder) Fraction = 1;
		SetChrome(false, folder);
	}

	public void PullTorrent()
	{
		var torrent = Torrent;
		if (torrent == null) return;
		EngineStatus status;
		try { status = torrent.Engine.GetStatus(); }
		catch { return; }
		Name = string.IsNullOrEmpty(status.Name) ? "Torrent" : status.Name;
		var pct = Math.Clamp(status.ProgressPercent, 0, 100);
		Fraction = pct / 100.0;
		var err = string.IsNullOrEmpty(status.ErrorMessage) ? "" : "  " + status.ErrorMessage;
		Detail = pct.ToString("0", CultureInfo.InvariantCulture) + "%  " + status.State
			+ "  " + Engine.Fmt((long)status.DownBytesPerSec) + "/s" + err;
		var finished = status.State is "Complete" or "Seeding";
		SetChrome(!finished, finished);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

sealed class PendingAsk
{
	public string Kind = "";
	public string Magnet = "";
	public string Name = "";
	public CoreWebView2DownloadStartingEventArgs? Args;
	public CoreWebView2Deferral? Deferral;
}

public partial class SiteBrowser : UserControl
{
	readonly ObservableCollection<SiteTab> _tabs = [];
	readonly ObservableCollection<LinkRow> _links = [];
	readonly ObservableCollection<DownloadRow> _downloads = [];
	readonly Queue<PendingAsk> _askQueue = new();
	readonly List<SiteVisit> _history = [];
	readonly List<SiteVisit> _bookmarks = [];
	readonly List<string> _closed = [];
	readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
	static readonly SolidColorBrush NavOn = MakeBrush(0x27, 0x32, 0x22);
	CoreWebView2Environment? _env;
	PendingAsk? _currentAsk;
	int _proxyPort;
	Task? _wake;
	bool _historyOpen;
	bool _callsOpen;
	bool _downloadsOpen;
	bool _menuOpen;
	bool _settingsOpen;
	bool _findOpen;
	string _settingsSection = "general";
	string _note = "";
	string _hover = "";
	string _findTerm = "";
	SiteTab? _findTab;
	Task _adQueue = Task.CompletedTask;

	public SiteBrowser()
	{
		InitializeComponent();
		TabList.ItemsSource = _tabs;
		CallList.ItemsSource = _links;
		DownloadList.ItemsSource = _downloads;
		_downloads.CollectionChanged += (_, _) => PaintDownloadEmpty();
		DownloadFolderText.Text = DownloadsDirectory();
		TorrentFolderText.Text = TorrentDesk.SaveRoot;
		AskHint.Text = "Type " + TorrentDesk.Phrase + " to use this PC's network.";
		ShowSection("general");
		AdBlock.Shared.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
		{
			PaintAds();
			QueueAds();
		}));
		AdBlock.Shared.Start();
		PaintAds();
		PaintChrome();
	}

	public event Action? MinimizeRequested;
	public event Action? MaximizeRequested;
	public event Action? CloseRequested;
	public event Action? TunnelToggleRequested;
	public event Action? TunnelImportRequested;

	public void SetRestore(bool maximized) => MaxButton.Content = maximized ? "\uE923" : "\uE922";

	public void ApplyTunnel(string status, string action, bool enabled)
	{
		TunnelStatus.Text = status;
		TunnelButton.Content = action;
		TunnelButton.IsEnabled = enabled;
		ImportButton.IsEnabled = enabled;
	}

	public void TickShelf()
	{
		TorrentDesk.Refresh();
		foreach (var torrent in TorrentDesk.Items.ToList())
		{
			var row = _downloads.FirstOrDefault(item => ReferenceEquals(item.Torrent, torrent));
			if (row == null)
			{
				row = new DownloadRow("Torrent", TorrentDesk.SaveRoot, null) { Torrent = torrent };
				_downloads.Insert(0, row);
			}
			row.PullTorrent();
		}
		for (var i = _downloads.Count - 1; i >= 0; i--)
		{
			var torrent = _downloads[i].Torrent;
			if (torrent != null && !TorrentDesk.Items.Contains(torrent)) _downloads.RemoveAt(i);
		}
	}

	public void UseProxy(int port)
	{
		if (port <= 0) throw new InvalidOperationException("The local proxy is not running.");
		_proxyPort = port;
	}

	public void Wake() => _wake ??= Start();

	async Task Start()
	{
		try
		{
			Directory.CreateDirectory(Root);
			_history.AddRange(SiteDesk.ReadVisits(ReadFile(HistoryFile), SiteDesk.HistoryMax));
			_bookmarks.AddRange(SiteDesk.ReadVisits(ReadFile(BookmarkFile), SiteDesk.BookmarkMax));
			FillLists();
			var urls = SiteDesk.ReadSession(ReadFile(SessionFile));
			if (urls.Count == 0)
			{
				await AddTab(null, true, true);
				return;
			}
			foreach (var url in urls)
				await AddTab(url, false, false);
			if (_tabs.Count > 0) Select(_tabs[0]);
		}
		catch (Exception ex)
		{
			SetNote(ex.Message);
		}
	}

	async Task<SiteTab?> AddTab(string? url, bool select, bool focusAddress)
	{
		if (_tabs.Count >= 20)
		{
			SetNote("Too many tabs.");
			return null;
		}
		var view = new WebView2 { Visibility = Visibility.Collapsed };
		view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 22, 17);
		ViewHost.Children.Add(view);
		var tab = new SiteTab(view);
		_tabs.Add(tab);
		if (select) Select(tab);
		try
		{
			await EnsureEnv();
			await view.EnsureCoreWebView2Async(_env);
			Wire(tab);
			if (SiteDesk.IsPage(url)) view.CoreWebView2.Navigate(url!);
			if (select) PaintChrome();
			if (select && _findOpen) _ = RunFind(false);
			if (focusAddress) _ = Dispatcher.BeginInvoke(() => { Address.Focus(); Address.SelectAll(); });
			else if (select) view.Focus();
		}
		catch (Exception ex)
		{
			SetNote(ex is WebView2RuntimeNotFoundException
				? "The Edge WebView2 runtime is not installed, so this pane cannot open pages."
				: ex.Message);
		}
		SaveSession();
		return tab;
	}

	async Task EnsureEnv()
	{
		if (_env != null) return;
		var folder = Path.Combine(Root, "site-view");
		Directory.CreateDirectory(folder);
		if (_proxyPort <= 0) throw new InvalidOperationException("The local proxy is not running.");
		var options = new CoreWebView2EnvironmentOptions(Guardex.Browser.SocksGate.BrowserArguments(_proxyPort))
		{
			AllowSingleSignOnUsingOSPrimaryAccount = false,
			ExclusiveUserDataFolderAccess = true
		};
		_env = await CoreWebView2Environment.CreateAsync(null, folder, options);
	}

	void Wire(SiteTab tab)
	{
		var view = tab.View;
		var core = view.CoreWebView2;
		core.Settings.IsPasswordAutosaveEnabled = false;
		core.Settings.IsGeneralAutofillEnabled = false;
		core.Settings.IsStatusBarEnabled = false;
		core.Settings.AreDefaultContextMenusEnabled = true;
		core.Settings.AreDefaultScriptDialogsEnabled = true;
		core.Settings.IsZoomControlEnabled = true;
		core.Settings.IsReputationCheckingRequired = false;
		core.PermissionRequested += (_, e) =>
		{
			if (e.PermissionKind is CoreWebView2PermissionKind.Camera
				or CoreWebView2PermissionKind.Microphone
				or CoreWebView2PermissionKind.Geolocation
				or CoreWebView2PermissionKind.Notifications)
				e.State = CoreWebView2PermissionState.Deny;
		};
		core.LaunchingExternalUriScheme += (_, schemeArgs) =>
		{
			var schemeUri = schemeArgs.Uri ?? "";
			if (schemeUri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
			{
				schemeArgs.Cancel = true;
				AskMagnet(schemeUri);
			}
		};
		core.NavigationStarting += (_, args) =>
		{
			var navUri = args.Uri ?? "";
			if (navUri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
			{
				args.Cancel = true;
				AskMagnet(navUri);
				return;
			}
			if (tab == Active) CloseSettings();
			tab.Loading = true;
			tab.PageUrl = args.Uri ?? "";
			tab.Calls.Clear();
			if (tab == Active) PaintChrome();
		};
		core.NavigationCompleted += (_, args) =>
		{
			tab.Loading = false;
			if (args.IsSuccess)
			{
				NoteVisit(tab);
				SaveSession();
				if (tab == Active) SetNote("");
			}
			else if (tab == Active && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
				SetNote("The page did not finish loading.");
			if (tab == Active) PaintChrome();
		};
		core.SourceChanged += (_, _) =>
		{
			if (!string.IsNullOrWhiteSpace(core.Source)) tab.PageUrl = core.Source;
			if (tab == Active) PaintChrome();
			SaveSession();
		};
		core.DocumentTitleChanged += (_, _) =>
		{
			tab.Title = TabTitle(core);
			NoteVisit(tab);
			if (tab == Active) PaintChrome();
		};
		core.HistoryChanged += (_, _) => { if (tab == Active) PaintChrome(); };
		core.StatusBarTextChanged += (_, _) => { if (tab == Active) SetHover(core.StatusBarText ?? ""); };
		core.ContainsFullScreenElementChanged += (_, _) => { if (tab == Active) PaintChrome(); };
		core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => CloseTab(tab));
		core.NewWindowRequested += (_, args) =>
		{
			var deferral = args.GetDeferral();
			_ = OpenPopup(args, deferral);
		};
		core.DownloadStarting += (_, args) => OnDownload(args);
		core.Find.MatchCountChanged += (_, _) => { if (tab == Active) Dispatcher.BeginInvoke(PaintFind); };
		core.Find.ActiveMatchIndexChanged += (_, _) => { if (tab == Active) Dispatcher.BeginInvoke(PaintFind); };
		view.ZoomFactorChanged += (_, _) => { if (tab == Active) PaintZoom(); };
		core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
		core.WebResourceRequested += (_, args) => OnResource(tab, args);
		QueueAds();
		_ = WatchCalls(tab, core);
	}

	void OnResource(SiteTab tab, CoreWebView2WebResourceRequestedEventArgs args)
	{
		if (!AdBlock.Shared.Enabled || _env == null) return;
		var url = args.Request.Uri ?? "";
		if (url.Length == 0) return;
		var main = IsMainDocument(args);
		var page = tab.PageUrl;
		if (page.Length == 0) page = tab.View.CoreWebView2?.Source ?? "";
		if (!AdBlock.Shared.Block(page, url, ResourceKind(args.ResourceContext, main), main)) return;
		args.Response = _env.CreateWebResourceResponse(new MemoryStream([]), 403, "Blocked", "Content-Length: 0");
	}

	static bool IsMainDocument(CoreWebView2WebResourceRequestedEventArgs args)
	{
		if (args.ResourceContext != CoreWebView2WebResourceContext.Document) return false;
		var dest = "";
		try { dest = args.Request.Headers.GetHeader("sec-fetch-dest"); } catch { }
		return !dest.Equals("iframe", StringComparison.OrdinalIgnoreCase) && !dest.Equals("frame", StringComparison.OrdinalIgnoreCase);
	}

	static string ResourceKind(CoreWebView2WebResourceContext context, bool main) => context switch
	{
		CoreWebView2WebResourceContext.Document => main ? "document" : "subdocument",
		CoreWebView2WebResourceContext.Script => "script",
		CoreWebView2WebResourceContext.Image => "image",
		CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
		CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.EventSource => "xmlhttprequest",
		CoreWebView2WebResourceContext.Font => "font",
		CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.TextTrack => "media",
		CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => "ping",
		CoreWebView2WebResourceContext.Websocket => "websocket",
		_ => "other"
	};

	void QueueAds() => _adQueue = RefreshAds(_adQueue);

	async Task RefreshAds(Task prior)
	{
		try { await prior; } catch { }
		var body = AdBlock.Shared.CosmeticScript();
		foreach (var tab in _tabs.ToList())
		{
			var core = tab.View.CoreWebView2;
			if (core == null) continue;
			if (tab.AdScriptId.Length > 0)
			{
				try { core.RemoveScriptToExecuteOnDocumentCreated(tab.AdScriptId); } catch { }
				tab.AdScriptId = "";
			}
			if (body.Length == 0) continue;
			try { tab.AdScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(body); }
			catch { }
		}
	}

	void PaintAds()
	{
		AdButton.Content = AdBlock.Shared.Enabled ? "Turn off" : "Turn on";
		AdStatus.Text = AdBlock.Shared.Status;
	}

	void OnAdToggle(object sender, RoutedEventArgs e) => AdBlock.Shared.SetEnabled(!AdBlock.Shared.Enabled);

	async Task WatchCalls(SiteTab tab, CoreWebView2 core)
	{
		try
		{
			await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
			var receiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
			receiver.DevToolsProtocolEventReceived += (_, args) => OnCall(tab, args.ParameterObjectAsJson);
		}
		catch
		{
		}
	}

	async Task OpenPopup(CoreWebView2NewWindowRequestedEventArgs args, CoreWebView2Deferral deferral)
	{
		try
		{
			var tab = await AddTab(null, true, false);
			if (tab?.View.CoreWebView2 != null) args.NewWindow = tab.View.CoreWebView2;
			else args.Handled = true;
		}
		catch
		{
			args.Handled = true;
		}
		finally
		{
			deferral.Complete();
		}
	}

	void OnCall(SiteTab tab, string json)
	{
		var page = tab.PageUrl;
		if (!LinkRules.Read(json, page, out var url, out var type, out var initiator)) return;
		Dispatcher.BeginInvoke(() =>
		{
			if (tab.Calls.Count >= 400) return;
			tab.Calls.Add(new SiteCall(url, type, initiator));
			if (tab == Active) PaintCalls();
		});
	}

	void OnDownload(CoreWebView2DownloadStartingEventArgs args)
	{
		var name = Path.GetFileName(args.ResultFilePath ?? "");
		if (name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
		{
			var deferral = args.GetDeferral();
			EnqueueAsk(new PendingAsk { Kind = "torrent", Name = name, Args = args, Deferral = deferral });
			return;
		}
		try { BeginFileDownload(args, false); }
		catch (Exception ex)
		{
			args.Cancel = true;
			SetNote(ex.Message);
		}
	}

	void BeginFileDownload(CoreWebView2DownloadStartingEventArgs args, bool openAfter)
	{
		var dir = openAfter ? OpenedTorrentDirectory() : DownloadsDirectory();
		var reserved = new HashSet<string>(_reserved, StringComparer.OrdinalIgnoreCase);
		var path = SiteDesk.DownloadPath(dir, args.ResultFilePath, item => File.Exists(item) || reserved.Contains(item));
		_reserved.Add(path);
		args.ResultFilePath = path;
		args.Handled = true;
		var row = new DownloadRow(Path.GetFileName(path), path, args.DownloadOperation) { OpenAfter = openAfter };
		args.DownloadOperation.StateChanged += (_, _) => OnFetch(row);
		args.DownloadOperation.BytesReceivedChanged += (_, _) => OnFetch(row);
		_downloads.Insert(0, row);
		while (_downloads.Count > 30) _downloads.RemoveAt(_downloads.Count - 1);
		ShowDownloads();
		OnFetch(row);
	}

	void OnFetch(DownloadRow row)
	{
		void Apply()
		{
			var op = row.Operation;
			if (op == null || row.Torrent != null) return;
			if (op.State == CoreWebView2DownloadState.Completed)
			{
				var first = !row.FileSettled;
				row.FileSettled = true;
				_reserved.Remove(row.Path);
				if (first && row.OpenAfter)
				{
					var err = TorrentDesk.AddFile(row.Path);
					if (err.Length == 0 && TorrentDesk.Items.Count > 0)
					{
						row.Torrent = TorrentDesk.Items[TorrentDesk.Items.Count - 1];
						row.OpenAfter = false;
						row.PullTorrent();
					}
					else row.SetFileEnd(err.Length == 0 ? "Saved" : err, false);
				}
				else row.SetFileEnd("Saved to Downloads", SiteDesk.IsInside(DownloadsDirectory(), row.Path));
			}
			else if (op.State == CoreWebView2DownloadState.Interrupted)
			{
				row.FileSettled = true;
				_reserved.Remove(row.Path);
				row.Detail = "Stopped";
				row.SetChrome(false, false);
			}
			else
			{
				var got = op.BytesReceived < 0 ? 0 : (ulong)op.BytesReceived;
				if (op.TotalBytesToReceive is ulong total && total > 0)
				{
					row.Fraction = Math.Clamp(got / (double)total, 0, 1);
					row.Detail = Size(got) + " of " + Size(total);
				}
				else row.Detail = Size(got);
			}
		}
		if (Dispatcher.CheckAccess()) Apply();
		else Dispatcher.BeginInvoke(Apply);
	}

	static string Progress(CoreWebView2DownloadOperation op)
	{
		var got = op.BytesReceived < 0 ? 0 : (ulong)op.BytesReceived;
		if (op.TotalBytesToReceive is ulong total && total > 0)
			return Size(got) + " of " + Size(total);
		return Size(got);
	}

	static string Size(ulong n)
	{
		if (n < 1024) return n + " B";
		if (n < 1024 * 1024) return (n / 1024.0).ToString("0.#") + " KB";
		return (n / 1024.0 / 1024.0).ToString("0.#") + " MB";
	}

	void Select(SiteTab tab)
	{
		foreach (var item in _tabs)
			item.Selected = item == tab;
		PaintChrome();
		if (_findOpen) _ = RunFind(false);
	}

	void CloseTab(SiteTab tab)
	{
		var url = tab.View.CoreWebView2?.Source;
		if (SiteDesk.IsPage(url))
		{
			_closed.Insert(0, url!);
			if (_closed.Count > 10) _closed.RemoveAt(_closed.Count - 1);
		}
		var index = _tabs.IndexOf(tab);
		_tabs.Remove(tab);
		ViewHost.Children.Remove(tab.View);
		try { tab.View.Dispose(); } catch { }
		if (_tabs.Count == 0) _ = AddTab(null, true, true);
		else Select(_tabs[Math.Min(Math.Max(index, 0), _tabs.Count - 1)]);
		SaveSession();
	}

	void Go()
	{
		var typed = Address.Text.Trim();
		if (typed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
		{
			AskMagnet(typed);
			return;
		}
		CloseSettings();
		if (!LinkRules.TryPage(Address.Text, out var url, out var error))
		{
			SetNote(error);
			return;
		}
		var core = Active?.View.CoreWebView2;
		if (core == null)
		{
			SetNote("The page is not ready.");
			return;
		}
		SetNote("");
		_historyOpen = false;
		core.Navigate(url);
	}

	void NoteVisit(SiteTab tab)
	{
		var core = tab.View.CoreWebView2;
		if (core == null) return;
		tab.Title = TabTitle(core);
		if (!SiteDesk.Remember(_history, core.DocumentTitle, core.Source)) return;
		SaveHistory();
		FillLists();
	}

	static string TabTitle(CoreWebView2 core)
	{
		var title = SiteDesk.OneLine(core.DocumentTitle);
		if (title.Length > 0) return title;
		if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host;
		return "New tab";
	}

	void PaintChrome()
	{
		var tab = Active;
		var core = tab?.View.CoreWebView2;
		var full = core?.ContainsFullScreenElement == true;
		var chrome = full ? Visibility.Collapsed : Visibility.Visible;
		TabRow.Visibility = chrome;
		ToolRow.Visibility = chrome;
		FindBar.Visibility = full || !_findOpen ? Visibility.Collapsed : Visibility.Visible;
		if (full)
		{
			HistoryPane.Visibility = Visibility.Collapsed;
			SettingsPage.Visibility = Visibility.Collapsed;
			DownloadPane.Visibility = Visibility.Collapsed;
			MenuPane.Visibility = Visibility.Collapsed;
			DownloadsColumn.Width = new GridLength(0);
			ApplyViews(true);
			return;
		}
		SettingsPage.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
		MenuPane.Visibility = _menuOpen ? Visibility.Visible : Visibility.Collapsed;
		DownloadPane.Visibility = _downloadsOpen ? Visibility.Visible : Visibility.Collapsed;
		DownloadsColumn.Width = new GridLength(_downloadsOpen ? 380 : 0);
		ApplyViews(false);
		BackButton.IsEnabled = core?.CanGoBack == true;
		ForwardButton.IsEnabled = core?.CanGoForward == true;
		var loading = tab?.Loading == true;
		ReloadButton.Content = loading ? "\uE711" : "\uE72C";
		ReloadButton.ToolTip = loading ? "Stop" : "Reload";
		var url = core?.Source ?? "";
		if (!Address.IsKeyboardFocusWithin)
			Address.Text = SiteDesk.IsPage(url) ? url : "";
		AddressHint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
		StarButton.Content = SiteDesk.IsMarked(_bookmarks, url) ? "\uE735" : "\uE734";
		PaintZoom();
		var blank = !SiteDesk.IsPage(url);
		HistoryPane.Visibility = _settingsOpen ? Visibility.Collapsed : blank || _historyOpen ? Visibility.Visible : Visibility.Collapsed;
		PaintCalls();
	}

	void ApplyViews(bool showPage)
	{
		var hide = _settingsOpen && !showPage;
		ViewHost.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
		foreach (var item in _tabs)
			item.View.Visibility = !hide && item.Selected ? Visibility.Visible : Visibility.Collapsed;
	}

	void PaintZoom()
	{
		var view = Active?.View;
		var factor = view?.CoreWebView2 == null ? 1 : view.ZoomFactor;
		var text = Math.Round(factor * 100) + "%";
		MenuZoom.Text = text;
		SettingsZoom.Text = text;
	}

	void ZoomBy(double delta)
	{
		var view = Active?.View;
		if (view?.CoreWebView2 == null) return;
		view.ZoomFactor = Math.Clamp(delta == 0 ? 1 : view.ZoomFactor + delta, 0.25, 3);
		PaintZoom();
	}

	void PaintCalls()
	{
		_links.Clear();
		var tab = Active;
		if (tab == null)
		{
			CallsSummary.Text = "No page open.";
			return;
		}
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var watch = 0;
		var block = 0;
		foreach (var call in tab.Calls)
		{
			var row = LinkRules.Row(tab.PageUrl, call.Url, call.Type, call.Initiator);
			if (row == null) continue;
			var key = row.Band + "|" + row.Kind + "|" + row.Host + "|" + row.Path;
			if (!seen.Add(key)) continue;
			if (row.Band == "block")
			{
				block++;
				_links.Insert(0, row);
			}
			else
			{
				watch++;
				_links.Add(row);
			}
		}
		var summary = block + " second-hop or address calls. " + watch + " third-party calls.";
		CallsSummary.Text = summary;
	}

	void PaintFind()
	{
		var find = Active?.View.CoreWebView2?.Find;
		if (find == null || !_findOpen) return;
		if (FindBox.Text.Length == 0) FindCount.Text = "";
		else if (find.MatchCount <= 0) FindCount.Text = "No matches";
		else if (find.ActiveMatchIndex > 0) FindCount.Text = find.ActiveMatchIndex + " of " + find.MatchCount;
		else FindCount.Text = find.MatchCount + " matches";
	}

	async Task RunFind(bool advance)
	{
		var core = Active?.View.CoreWebView2;
		if (core == null || _env == null) return;
		var term = FindBox.Text;
		if (term.Length == 0)
		{
			try { core.Find.Stop(); } catch { }
			FindCount.Text = "";
			_findTerm = "";
			_findTab = null;
			return;
		}
		try
		{
			if (advance && _findTerm == term && _findTab == Active)
			{
				core.Find.FindNext();
				return;
			}
			_findTerm = term;
			_findTab = Active;
			var options = _env.CreateFindOptions();
			options.FindTerm = term;
			options.ShouldHighlightAllMatches = true;
			options.SuppressDefaultFindDialog = true;
			await core.Find.StartAsync(options);
			PaintFind();
		}
		catch (Exception ex)
		{
			SetNote(ex.Message);
		}
	}

	async Task RunFindPrevious()
	{
		var core = Active?.View.CoreWebView2;
		if (core == null) return;
		if (_findTerm == FindBox.Text && _findTab == Active && FindBox.Text.Length > 0)
		{
			core.Find.FindPrevious();
			return;
		}
		await RunFind(false);
	}

	void CloseFind()
	{
		_findOpen = false;
		_findTerm = "";
		_findTab = null;
		FindBox.Text = "";
		FindCount.Text = "";
		try { Active?.View.CoreWebView2?.Find.Stop(); } catch { }
		PaintChrome();
	}

	void FillLists()
	{
		BookmarkList.ItemsSource = _bookmarks.ToList();
		HistoryList.ItemsSource = _history.Take(80).ToList();
	}

	void SetNote(string text)
	{
		_note = text ?? "";
		PaintHover();
	}

	void SetHover(string text)
	{
		_hover = text ?? "";
		if (_note.Length == 0) PaintHover();
	}

	void PaintHover()
	{
		var text = _note.Length > 0 ? _note : _hover;
		StatusLeft.Text = text;
		HoverChip.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
	}

	void SaveHistory() => WriteFile(HistoryFile, SiteDesk.WriteVisits(_history));
	void SaveBookmarks() => WriteFile(BookmarkFile, SiteDesk.WriteVisits(_bookmarks));

	void SaveSession()
	{
		var urls = _tabs.Select(tab => tab.View.CoreWebView2?.Source ?? "").Where(SiteDesk.IsPage);
		WriteFile(SessionFile, SiteDesk.WriteSession(urls));
	}

	static string ReadFile(string path)
	{
		try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
		catch { return ""; }
	}

	static void WriteFile(string path, string text)
	{
		try { File.WriteAllText(path, text); }
		catch { }
	}

	static string DownloadsDirectory()
	{
		var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		Directory.CreateDirectory(dir);
		return dir;
	}

	string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex");
	string HistoryFile => Path.Combine(Root, "site-history.txt");
	string BookmarkFile => Path.Combine(Root, "site-bookmarks.txt");
	string SessionFile => Path.Combine(Root, "site-session.txt");
	SiteTab? Active => _tabs.FirstOrDefault(tab => tab.Selected);

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
		if (e.IsRepeat && ctrl && key is Key.T or Key.W or Key.Tab or Key.L or Key.F or Key.H or Key.J or Key.P or Key.D or Key.F12)
		{
			e.Handled = true;
			base.OnPreviewKeyDown(e);
			return;
		}
		var handled = false;
		if (key == Key.Escape && _menuOpen)
		{
			CloseMenu();
			handled = true;
		}
		else if (key == Key.Escape && _downloadsOpen)
		{
			CloseDownloads();
			handled = true;
		}
		else if (key == Key.Escape && _settingsOpen)
		{
			CloseSettings();
			handled = true;
		}
		else if (key == Key.Escape && _currentAsk != null)
		{
			FinishAsk(true);
			handled = true;
		}
		else if (key == Key.Escape && _findOpen)
		{
			CloseFind();
			handled = true;
		}
		else if (alt && key == Key.Left) { Back(); handled = true; }
		else if (alt && key == Key.Right) { Forward(); handled = true; }
		else if (key == Key.F5 || ctrl && key == Key.R) { Reload(); handled = true; }
		else if (key == Key.F12) { DevTools(); handled = true; }
		else if (ctrl && key == Key.L) { Address.Focus(); Address.SelectAll(); handled = true; }
		else if (ctrl && shift && key == Key.T) { Reopen(); handled = true; }
		else if (ctrl && key == Key.T) { _ = AddTab(null, true, true); handled = true; }
		else if (ctrl && key == Key.W) { if (Active != null) CloseTab(Active); handled = true; }
		else if (ctrl && key == Key.Tab) { Cycle(shift ? -1 : 1); handled = true; }
		else if (ctrl && key == Key.F) { ShowFind(); handled = true; }
		else if (ctrl && key == Key.H) { ToggleHistory(); handled = true; }
		else if (ctrl && key == Key.J) { ToggleDownloads(); handled = true; }
		else if (ctrl && key == Key.OemComma) { OpenSettings("general"); handled = true; }
		else if (ctrl && key == Key.P) { Print(); handled = true; }
		else if (ctrl && key == Key.D) { ToggleStar(); handled = true; }
		else if (ctrl && key == Key.OemPlus || ctrl && key == Key.Add) { ZoomBy(0.1); handled = true; }
		else if (ctrl && key == Key.OemMinus || ctrl && key == Key.Subtract) { ZoomBy(-0.1); handled = true; }
		else if (ctrl && key is Key.D0 or Key.NumPad0) { ZoomBy(0); handled = true; }
		else if (ctrl && !shift && !alt && key >= Key.D1 && key <= Key.D8)
		{
			var index = key - Key.D1;
			if (index < _tabs.Count) Select(_tabs[index]);
			handled = true;
		}
		if (handled) e.Handled = true;
		base.OnPreviewKeyDown(e);
	}

	void Back()
	{
		var core = Active?.View.CoreWebView2;
		if (core?.CanGoBack == true) core.GoBack();
	}

	void Forward()
	{
		var core = Active?.View.CoreWebView2;
		if (core?.CanGoForward == true) core.GoForward();
	}

	void Reload()
	{
		var core = Active?.View.CoreWebView2;
		if (core == null) return;
		if (Active!.Loading) core.Stop();
		else core.Reload();
	}

	void Cycle(int step)
	{
		if (_tabs.Count == 0) return;
		var index = Active == null ? 0 : _tabs.IndexOf(Active);
		var next = (index + step + _tabs.Count) % _tabs.Count;
		Select(_tabs[next]);
	}

	void Reopen()
	{
		if (_closed.Count == 0) return;
		var url = _closed[0];
		_closed.RemoveAt(0);
		_ = AddTab(url, true, false);
	}

	void ShowFind()
	{
		_findOpen = true;
		PaintChrome();
		FindBox.Focus();
		FindBox.SelectAll();
		_ = RunFind(false);
	}

	void ToggleHistory()
	{
		_historyOpen = !_historyOpen;
		if (_historyOpen) FillLists();
		PaintChrome();
	}

	void ToggleDownloads()
	{
		if (_downloadsOpen) CloseDownloads();
		else ShowDownloads();
	}

	void ShowDownloads()
	{
		_downloadsOpen = true;
		_menuOpen = false;
		MenuPane.Visibility = Visibility.Collapsed;
		DownloadPane.Visibility = Visibility.Visible;
		PaintDownloadEmpty();
	}

	void CloseDownloads()
	{
		_downloadsOpen = false;
		DownloadPane.Visibility = Visibility.Collapsed;
	}

	void CloseMenu()
	{
		_menuOpen = false;
		MenuPane.Visibility = Visibility.Collapsed;
	}

	void ToggleStar()
	{
		var core = Active?.View.CoreWebView2;
		var url = core?.Source;
		if (!SiteDesk.IsPage(url)) return;
		SiteDesk.ToggleMark(_bookmarks, core!.DocumentTitle, url);
		SaveBookmarks();
		FillLists();
		PaintChrome();
	}

	void Print()
	{
		try { Active?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
		catch (Exception ex) { SetNote(ex.Message); }
	}

	void DevTools()
	{
		try { Active?.View.CoreWebView2?.OpenDevToolsWindow(); }
		catch (Exception ex) { SetNote(ex.Message); }
	}

	void OnNewTab(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		CloseSettings();
		_ = AddTab(null, true, true);
	}
	void OnBack(object sender, RoutedEventArgs e) => Back();
	void OnForward(object sender, RoutedEventArgs e) => Forward();
	void OnReload(object sender, RoutedEventArgs e) => Reload();
	void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(0.1);
	void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(-0.1);
	void OnStar(object sender, RoutedEventArgs e) => ToggleStar();
	void OnPrint(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		Print();
	}
	void OnFindToggle(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		if (_findOpen) CloseFind();
		else ShowFind();
	}
	void OnFindClose(object sender, RoutedEventArgs e) => CloseFind();
	void OnFindNext(object sender, RoutedEventArgs e) => _ = RunFind(true);
	void OnFindPrevious(object sender, RoutedEventArgs e) => _ = RunFindPrevious();
	void OnCallsToggle(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		_callsOpen = !_callsOpen;
		CallsColumn.Width = new GridLength(_callsOpen ? 360 : 0);
		CallsPane.Visibility = _callsOpen ? Visibility.Visible : Visibility.Collapsed;
		if (_callsOpen) CloseSettings();
	}
	void OnHistoryToggle(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		CloseSettings();
		ToggleHistory();
	}
	void OnDownloadsToggle(object sender, RoutedEventArgs e) => ToggleDownloads();
	void OnMenu(object sender, RoutedEventArgs e)
	{
		if (_menuOpen) CloseMenu();
		else
		{
			_menuOpen = true;
			CloseDownloads();
			MenuPane.Visibility = Visibility.Visible;
		}
	}
	void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings("general");
	void OnSettingsNav(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement el && el.Tag is string name) ShowSection(name);
	}
	void OnMinimize(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke();
	void OnMaximize(object sender, RoutedEventArgs e) => MaximizeRequested?.Invoke();
	void OnWindowClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
	void OnTunnelToggle(object sender, RoutedEventArgs e) => TunnelToggleRequested?.Invoke();
	void OnTunnelImport(object sender, RoutedEventArgs e) => TunnelImportRequested?.Invoke();
	void OnZoomReset(object sender, RoutedEventArgs e) => ZoomBy(0);
	void OnAskOpen(object sender, RoutedEventArgs e) => AcceptAsk(true);
	void OnAskSave(object sender, RoutedEventArgs e) => AcceptAsk(false);
	void OnAskCancel(object sender, RoutedEventArgs e) => FinishAsk(true);

	void OnAddressKey(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		Go();
	}

	void OnAddressChanged(object sender, TextChangedEventArgs e) =>
		AddressHint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;

	void OnAddressFocus(object sender, RoutedEventArgs e) =>
		AddressHint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;

	void OnFindKey(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _ = RunFindPrevious();
		else _ = RunFind(true);
	}

	void OnFindChanged(object sender, TextChangedEventArgs e)
	{
		_findTerm = "";
		_ = RunFind(false);
	}

	void OnTabDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement el && el.DataContext is SiteTab tab)
		{
			CloseSettings();
			Select(tab);
		}
	}

	void OnTabMouse(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Middle) return;
		if (sender is FrameworkElement el && el.DataContext is SiteTab tab)
		{
			CloseTab(tab);
			e.Handled = true;
		}
	}

	void OnCloseTab(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement el && el.Tag is SiteTab tab) CloseTab(tab);
		e.Handled = true;
	}

	void OnVisitOpen(object sender, MouseButtonEventArgs e)
	{
		if (sender is not ListBox box || box.SelectedItem is not SiteVisit visit || !SiteDesk.IsPage(visit.Url)) return;
		var core = Active?.View.CoreWebView2;
		if (core == null) return;
		_historyOpen = false;
		CloseSettings();
		SetNote("");
		core.Navigate(visit.Url);
		PaintChrome();
	}

	void OnClearHistory(object sender, RoutedEventArgs e)
	{
		_history.Clear();
		SaveHistory();
		FillLists();
	}

	void OnClearBookmarks(object sender, RoutedEventArgs e)
	{
		_bookmarks.Clear();
		SaveBookmarks();
		FillLists();
		PaintChrome();
	}

	void OnCancelDownload(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement el || el.Tag is not DownloadRow row) return;
		if (row.Torrent != null)
		{
			TorrentDesk.Remove(row.Torrent);
			_downloads.Remove(row);
			return;
		}
		try { row.Operation?.Cancel(); } catch { }
	}

	void OnShowDownload(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement el || el.Tag is not DownloadRow row) return;
		var dir = DownloadsDirectory();
		var path = row.Torrent != null ? TorrentDesk.SaveRoot : row.Path;
		if (!SiteDesk.IsInside(dir, path)) return;
		var select = File.Exists(path);
		Process.Start(new ProcessStartInfo
		{
			FileName = "explorer.exe",
			Arguments = select ? "/select,\"" + path + "\"" : "\"" + path + "\"",
			UseShellExecute = true
		});
	}

	void OnOpenDownloadsFolder(object sender, RoutedEventArgs e)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = "explorer.exe",
			Arguments = "\"" + DownloadsDirectory() + "\"",
			UseShellExecute = true
		});
	}

	protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseUp(e);
		if (e.OriginalSource is not DependencyObject source) return;
		if (_downloadsOpen && !IsWithin(DownloadPane, source) && !IsWithin(DownloadsButton, source))
			CloseDownloads();
		if (_menuOpen && !IsWithin(MenuPane, source) && !IsWithin(MenuButton, source))
			CloseMenu();
	}

	void AskMagnet(string magnet)
	{
		EnqueueAsk(new PendingAsk { Kind = "magnet", Magnet = magnet.Trim() });
	}

	void EnqueueAsk(PendingAsk ask)
	{
		if (_currentAsk == null) PresentAsk(ask);
		else _askQueue.Enqueue(ask);
	}

	void PresentAsk(PendingAsk ask)
	{
		_currentAsk = ask;
		AskBar.Visibility = Visibility.Visible;
		AskSave.Visibility = ask.Kind == "torrent" ? Visibility.Visible : Visibility.Collapsed;
		var phrase = !TorrentDesk.KillswitchOn && !TorrentDesk.OsConfirmed;
		AskPhrase.Visibility = phrase ? Visibility.Visible : Visibility.Collapsed;
		AskHint.Visibility = phrase ? Visibility.Visible : Visibility.Collapsed;
		AskPhrase.Text = "";
		AskText.Text = ask.Kind == "torrent"
			? "Open " + ask.Name + ", or save the .torrent file?"
			: "Open this magnet link?";
	}

	void AcceptAsk(bool open)
	{
		var ask = _currentAsk;
		if (ask == null) return;
		if (!open)
		{
			if (ask.Args == null) { FinishAsk(false); return; }
			try
			{
				BeginFileDownload(ask.Args, false);
				FinishAsk(false);
			}
			catch (Exception ex)
			{
				SetNote(ex.Message);
				FinishAsk(true);
			}
			return;
		}
		var err = AllowTorrent();
		if (err.Length > 0)
		{
			AskText.Text = err;
			AskPhrase.Visibility = Visibility.Visible;
			AskHint.Visibility = Visibility.Visible;
			return;
		}
		if (ask.Kind == "magnet")
		{
			err = TorrentDesk.AddMagnet(ask.Magnet);
			FinishAsk(false);
			if (err.Length > 0) SetNote(err);
			else
			{
				TickShelf();
				ShowDownloads();
			}
			return;
		}
		if (ask.Args == null) { FinishAsk(false); return; }
		try
		{
			BeginFileDownload(ask.Args, true);
			FinishAsk(false);
		}
		catch (Exception ex)
		{
			SetNote(ex.Message);
			FinishAsk(true);
		}
	}

	string AllowTorrent()
	{
		if (TorrentDesk.Allowed) return "";
		var typed = TorrentDesk.KillswitchOn || TorrentDesk.OsConfirmed ? "" : AskPhrase.Text;
		return TorrentDesk.Enable(typed);
	}

	void FinishAsk(bool cancel)
	{
		var ask = _currentAsk;
		_currentAsk = null;
		if (cancel && ask?.Args != null) ask.Args.Cancel = true;
		try { ask?.Deferral?.Complete(); } catch { }
		AskBar.Visibility = Visibility.Collapsed;
		if (_askQueue.Count > 0) PresentAsk(_askQueue.Dequeue());
	}

	void OpenSettings(string section)
	{
		CloseMenu();
		_settingsOpen = true;
		_historyOpen = false;
		ShowSection(section);
		PaintChrome();
	}

	void CloseSettings()
	{
		if (!_settingsOpen) return;
		_settingsOpen = false;
		SettingsPage.Visibility = Visibility.Collapsed;
		PaintChrome();
	}

	void ShowSection(string name)
	{
		_settingsSection = name;
		PageGeneral.Visibility = name == "general" ? Visibility.Visible : Visibility.Collapsed;
		PageDownloads.Visibility = name == "downloads" ? Visibility.Visible : Visibility.Collapsed;
		PageVpn.Visibility = name == "vpn" ? Visibility.Visible : Visibility.Collapsed;
		PagePrivacy.Visibility = name == "privacy" ? Visibility.Visible : Visibility.Collapsed;
		MarkNav(NavGeneral, name == "general");
		MarkNav(NavDownloads, name == "downloads");
		MarkNav(NavVpn, name == "vpn");
		MarkNav(NavPrivacy, name == "privacy");
	}

	static void MarkNav(Button button, bool on)
	{
		if (on) button.Background = NavOn;
		else button.ClearValue(Button.BackgroundProperty);
	}

	void PaintDownloadEmpty() =>
		DownloadEmpty.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

	static string OpenedTorrentDirectory()
	{
		var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardex", "opened-torrents");
		Directory.CreateDirectory(dir);
		return dir;
	}

	static bool IsWithin(DependencyObject parent, DependencyObject source)
	{
		var node = source;
		while (node != null)
		{
			if (node == parent) return true;
			node = VisualTreeHelper.GetParent(node);
		}
		return false;
	}

	static SolidColorBrush MakeBrush(byte r, byte g, byte b)
	{
		var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}
}

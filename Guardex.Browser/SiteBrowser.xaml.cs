using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Binding = System.Windows.Data.Binding;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
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
	public string StyledHost { get; set; } = "";
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
	public bool Running => _running;
	public Visibility CancelVisibility => _running ? Visibility.Visible : Visibility.Collapsed;
	public Visibility BarVisibility => _running ? Visibility.Visible : Visibility.Collapsed;
	public Visibility FolderVisibility => string.IsNullOrWhiteSpace(Path) ? Visibility.Collapsed : Visibility.Visible;

	public void SetChrome(bool running, bool folder)
	{
		if (_running == running && _folder == folder) return;
		_running = running;
		_folder = folder;
		Changed(nameof(Running));
		Changed(nameof(CancelVisibility));
		Changed(nameof(BarVisibility));
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
	static readonly SolidColorBrush TileFace = MakeBrush(0x27, 0x32, 0x22);
	static readonly SolidColorBrush Ink = MakeBrush(0xE4, 0xEC, 0xDF);
	static readonly SolidColorBrush Muted = MakeBrush(0x8E, 0xA0, 0x88);
	static readonly SolidColorBrush PanelBrush = MakeBrush(0x1B, 0x21, 0x19);
	static readonly SolidColorBrush LineBrush = MakeBrush(0x2C, 0x38, 0x28);
	CoreWebView2Environment? _env;
	PendingAsk? _currentAsk;
	int _proxyPort;
	Task? _wake;
	bool _historyOpen;
	bool _callsOpen;
	bool _downloadsOpen;
	bool _ringSpin;
	long _popupUntil;
	DownloadRow? _openRow;
	bool _menuOpen;
	bool _settingsOpen;
	bool _findOpen;
	string _settingsSection = "general";
	string _note = "";
	string _hover = "";
	string _findTerm = "";
	SiteTab? _findTab;
	Task _adQueue = Task.CompletedTask;
	bool _pageFull;
	bool _bookBar = true;
	bool _adModeLock;
	bool _startLock;
	bool _passLock;
	bool _passHere;
	string _passSite = "";
	string _passStamp = "";
	string? _keptAddress;
	string? _typedFile;
	VaultDesk.SavedLogin[] _logins = [];
	readonly Dictionary<string, string> _shownPasses = new(StringComparer.Ordinal);
	readonly List<System.Windows.Controls.TextBox> _passBoxes = new();
	bool _addressLock;
	bool _addressSelectAll;
	int _suggestPick = -1;
	Point _addressDown;
	HomeShortcut? _homeEdit;
	readonly HashSet<string> _liveSites = new(StringComparer.Ordinal);
	readonly SemaphoreSlim _vaultGate = new(1, 1);
	bool _parked;
	bool _parking;
	bool _vaultDismissed;
	string _vaultPage = "";
	string _vaultKind = "";
	string _vaultUser = "";
	string _vaultPass = "";

	public SiteBrowser()
	{
		InitializeComponent();
		TabList.ItemsSource = _tabs;
		CallList.ItemsSource = _links;
		DownloadList.ItemsSource = _downloads;
		_downloads.CollectionChanged += (_, _) =>
		{
			PaintDownloadEmpty();
			PaintDownloadButton();
		};
		DownloadFolderText.Text = DownloadsDirectory();
		AskHint.Text = "Type " + TorrentDesk.Phrase + ". This choice is remembered.";
		LoadBookBar();
		HomeDesk.Load();
		StartDesk.Load();
		PaintHome();
		PaintStart();
		ShowSection("general");
		Favicon.Watch(() => Dispatcher.BeginInvoke(new Action(FillLists)));
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
	public event Action<bool>? PageFullChanged;
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
		PaintDownloadButton();
	}

	public void UseProxy(int port)
	{
		if (port <= 0) throw new InvalidOperationException("The local proxy is not running.");
		_proxyPort = port;
	}

	public void Wake() => _wake ??= Start();

	public void ParkSignIns()
	{
		if (_parked) return;
		ClearShown();
		_parked = true;
		_parking = true;
		ClearVaultPrompt();
		try
		{
			foreach (var tab in _tabs.ToList())
			{
				var core = tab.View.CoreWebView2;
				if (core == null || !VaultDesk.TrySite(tab.PageUrl, out var site) || !VaultDesk.HasSignIn(site)) continue;
				try { core.Navigate("about:blank"); } catch { }
			}
			var coreAny = AnyCore();
			if (coreAny != null)
				foreach (var site in VaultDesk.SignSites()) DropSite(coreAny, site);
			_liveSites.Clear();
		}
		finally
		{
			_parking = false;
		}
	}

	public void ResumeSignIns()
	{
		if (!_parked) return;
		_parked = false;
		foreach (var tab in _tabs.ToList())
		{
			var core = tab.View.CoreWebView2;
			if (core == null || !VaultDesk.TrySite(tab.PageUrl, out var site) || !VaultDesk.HasSignIn(site)) continue;
			Inject(core, site);
			_liveSites.Add(site);
			try { core.Navigate(tab.PageUrl); } catch { }
		}
	}

	async Task Start()
	{
		try
		{
			Directory.CreateDirectory(Root);
			_history.AddRange(SiteDesk.ReadVisits(ReadFile(HistoryFile), SiteDesk.HistoryMax));
			_bookmarks.AddRange(SiteDesk.ReadMarks(ReadFile(BookmarkFile), SiteDesk.BookmarkMax));
			FillLists();
			PaintHome();
			await SweepProfile();
			var home = StartDesk.HomeTarget();
			await AddTab(home.Length == 0 ? null : home, true, home.Length == 0);
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
			ExclusiveUserDataFolderAccess = true,
			IsCustomCrashReportingEnabled = true
		};
		_env = await CoreWebView2Environment.CreateAsync(null, folder, options);
	}

	void Wire(SiteTab tab)
	{
		var view = tab.View;
		var core = view.CoreWebView2;
		core.Settings.IsPasswordAutosaveEnabled = false;
		core.Settings.IsGeneralAutofillEnabled = false;
		core.Settings.IsWebMessageEnabled = true;
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
			if (LinkRules.IsLocalFile(navUri))
			{
				var typed = _typedFile != null && LinkRules.SameFile(navUri, _typedFile);
				if (typed) _typedFile = null;
				else if (args.NavigationKind is not (CoreWebView2NavigationKind.Reload or CoreWebView2NavigationKind.BackOrForward))
				{
					args.Cancel = true;
					return;
				}
			}
			if (_parking || (_parked && (navUri.Length == 0 || navUri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))))
				return;
			if (tab == Active) CloseSettings();
			tab.Loading = true;
			tab.PageUrl = args.Uri ?? "";
			tab.Calls.Clear();
			if (tab == Active)
			{
				NoteVaultPage(tab.PageUrl);
				PaintChrome();
			}
			if (_parked || !VaultDesk.TrySite(tab.PageUrl, out var site)) return;
			if (VaultDesk.HasSignIn(site) && !_liveSites.Contains(site))
			{
				args.Cancel = true;
				_ = Arm(tab, site, tab.PageUrl);
				return;
			}
			_ = ReleaseSites(tab);
		};
		core.NavigationCompleted += (_, args) =>
		{
			tab.Loading = false;
			if (args.IsSuccess)
			{
				NoteVisit(tab);
				SaveSession();
				PaintStyle(tab);
				if (tab == Active) SetNote("");
			}
			else if (tab == Active && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
				SetNote("The page did not finish loading.");
			if (_parked)
			{
				var parkedCore = tab.View.CoreWebView2;
				if (parkedCore != null)
					foreach (var signed in VaultDesk.SignSites()) DropSite(parkedCore, signed);
			}
			else if (args.IsSuccess && VaultDesk.TrySite(tab.PageUrl, out var site) && _liveSites.Contains(site))
				_ = KeepCookies(tab, site);
			if (tab == Active) PaintChrome();
		};
		core.WebMessageReceived += (_, message) =>
		{
			string json;
			try { json = message.WebMessageAsJson; }
			catch { return; }
			if (!VaultDesk.ReadMessage(json, out var kind, out var user, out var pass)) return;
			var source = message.Source;
			Dispatcher.BeginInvoke(() => OnVaultMessage(tab, source, kind, user, pass));
		};
		_ = core.AddScriptToExecuteOnDocumentCreatedAsync(VaultDesk.Script);
		core.SourceChanged += (_, _) =>
		{
			if (tab == Active && SiteDesk.IsPage(core.Source)) _keptAddress = null;
			if (!string.IsNullOrWhiteSpace(core.Source)) tab.PageUrl = core.Source;
			if (tab == Active)
			{
				NoteVaultPage(tab.PageUrl);
				PaintChrome();
			}
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
		core.FaviconChanged += (_, _) => _ = StoreIcon(core);
		view.GotFocus += (_, _) =>
		{
			if (tab != Active || PopupHeld()) return;
			CloseMenu();
			CloseDownloads();
			CloseSuggest();
		};
		core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => CloseTab(tab));
		core.FrameNavigationStarting += (_, frameArgs) =>
		{
			if (LinkRules.IsLocalFile(frameArgs.Uri)) frameArgs.Cancel = true;
		};
		core.NewWindowRequested += (_, args) =>
		{
			if (LinkRules.IsLocalFile(args.Uri))
			{
				args.Handled = true;
				return;
			}
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
			tab.StyledHost = "";
			if (body.Length == 0)
			{
				try { await core.ExecuteScriptAsync("window.__guardexPlayerStop=1;var el=document.getElementById('guardex-ads');if(el)el.remove();"); } catch { }
				continue;
			}
			try { tab.AdScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(body); }
			catch { }
			try { await core.ExecuteScriptAsync(body); } catch { }
			PaintStyle(tab);
		}
	}

	void PaintStyle(SiteTab tab)
	{
		var core = tab.View.CoreWebView2;
		if (core == null || !AdBlock.Shared.Enabled) return;
		var host = Favicon.Host(core.Source);
		if (host.Length == 0 || host == tab.StyledHost) return;
		var js = AdBlock.Shared.StyleScript(core.Source);
		tab.StyledHost = host;
		if (js.Length == 0) return;
		_ = core.ExecuteScriptAsync(js);
	}

	void PaintAds()
	{
		AdButton.Content = AdBlock.Shared.Enabled ? "Turn off" : "Turn on";
		AdStatus.Text = AdBlock.Shared.Status;
		_adModeLock = true;
		AdEasy.IsChecked = AdBlock.Shared.Mode == 0;
		AdMedium.IsChecked = AdBlock.Shared.Mode == 1;
		AdHard.IsChecked = AdBlock.Shared.Mode == 2;
		_adModeLock = false;
	}

	void OnAdToggle(object sender, RoutedEventArgs e) => AdBlock.Shared.SetEnabled(!AdBlock.Shared.Enabled);

	void OnAdMode(object sender, RoutedEventArgs e)
	{
		if (_adModeLock || sender is not System.Windows.Controls.Primitives.ToggleButton box || box.IsChecked != true) return;
		var name = (sender as FrameworkElement)?.Name;
		AdBlock.Shared.SetMode(name == "AdHard" ? 2 : name == "AdMedium" ? 1 : 0);
	}

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
			PaintDownloadButton();
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
		NoteVaultPage(tab.PageUrl);
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
		var core = tab.View.CoreWebView2;
		_tabs.Remove(tab);
		if (core != null) ReleaseSitesNow(core);
		ViewHost.Children.Remove(tab.View);
		try { tab.View.Dispose(); } catch { }
		if (_tabs.Count == 0) OpenTab(true);
		else Select(_tabs[Math.Min(Math.Max(index, 0), _tabs.Count - 1)]);
		SaveSession();
	}

	void Go()
	{
		_keptAddress = null;
		var typed = Address.Text.Trim();
		if (typed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
		{
			AskMagnet(typed);
			return;
		}
		CloseSettings();
		if (LinkRules.LooksLikeFile(typed))
		{
			if (!LinkRules.TryTypedFile(typed, out var fileUrl, out var fileError))
			{
				SetNote(fileError);
				return;
			}
			var fileCore = Active?.View.CoreWebView2;
			if (fileCore == null)
			{
				SetNote("The page is not ready.");
				return;
			}
			SetNote("");
			_historyOpen = false;
			CloseSuggest();
			_typedFile = fileUrl;
			fileCore.Navigate(fileUrl);
			return;
		}
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
		CloseSuggest();
		core.Navigate(url);
	}

	void GoUrl(string url)
	{
		if (!SiteDesk.IsPage(url))
		{
			var text = SiteDesk.Kept(url);
			if (text.Length == 0) return;
			_historyOpen = false;
			_keptAddress = text;
			CloseSettings();
			CloseSuggest();
			SetNote("That bookmark is saved. This browser opens http and https pages.");
			PaintChrome();
			return;
		}
		_keptAddress = null;
		CloseSettings();
		CloseSuggest();
		_historyOpen = false;
		var core = Active?.View.CoreWebView2;
		if (core == null)
		{
			SetNote("The page is not ready.");
			return;
		}
		SetNote("");
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
		if (full != _pageFull)
		{
			_pageFull = full;
			PageFullChanged?.Invoke(full);
		}
		var chrome = full ? Visibility.Collapsed : Visibility.Visible;
		TabRow.Visibility = chrome;
		ToolRow.Visibility = chrome;
		FindBar.Visibility = full || !_findOpen ? Visibility.Collapsed : Visibility.Visible;
		if (full)
		{
			HistoryPane.Visibility = Visibility.Collapsed;
			HomePane.Visibility = Visibility.Collapsed;
			BookBar.Visibility = Visibility.Collapsed;
			SettingsPage.Visibility = Visibility.Collapsed;
			VaultBar.Visibility = Visibility.Collapsed;
			MenuPopup.IsOpen = false;
			DownloadsPopup.IsOpen = false;
			SuggestPopup.IsOpen = false;
			HomeProviderPopup.IsOpen = false;
			ApplyViews(true);
			return;
		}
		VaultBar.Visibility = _vaultKind.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		SettingsPage.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
		MenuPopup.IsOpen = _menuOpen;
		DownloadsPopup.IsOpen = _downloadsOpen;
		BookBar.Visibility = !full && _bookBar && _bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		BookBarItem.Content = _bookBar ? "Hide bookmarks bar" : "Show bookmarks bar";
		ReopenItem.Visibility = _closed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		var url = core?.Source ?? "";
		var blank = !SiteDesk.IsPage(url) && !LinkRules.IsLocalFile(url);
		HomePane.Visibility = !_settingsOpen && blank && !_historyOpen ? Visibility.Visible : Visibility.Collapsed;
		HistoryPane.Visibility = !_settingsOpen && _historyOpen ? Visibility.Visible : Visibility.Collapsed;
		ApplyViews(false);
		BackButton.IsEnabled = core?.CanGoBack == true;
		ForwardButton.IsEnabled = core?.CanGoForward == true;
		var loading = tab?.Loading == true;
		ReloadButton.Content = loading ? "\uE711" : "\uE72C";
		ReloadButton.ToolTip = loading ? "Stop" : "Reload";
		if (!Address.IsKeyboardFocusWithin)
		{
			_addressLock = true;
			Address.Text = _keptAddress ?? (SiteDesk.IsPage(url) ? url : LinkRules.LocalText(url));
			_addressLock = false;
			if (_keptAddress == null) CloseSuggest();
		}
		AddressHint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
		StarButton.Content = SiteDesk.IsMarked(_bookmarks, url) ? "\uE735" : "\uE734";
		PaintZoom();
		PaintCalls();
		if (_settingsOpen && _settingsSection == "passwords") PaintPasswords();
	}

	void ApplyViews(bool showPage)
	{
		var hide = !showPage && (_settingsOpen || HistoryPane.Visibility == Visibility.Visible || HomePane.Visibility == Visibility.Visible);
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
		foreach (var item in _bookmarks)
		{
			item.Icon = Favicon.Image(item.Url);
			Favicon.Queue(item.Url);
		}
		foreach (var item in _history)
		{
			item.Icon = Favicon.Image(item.Url);
			Favicon.Queue(item.Url);
		}
		BookmarkList.ItemsSource = _bookmarks.ToList();
		HistoryList.ItemsSource = _history.Take(80).ToList();
		BookBarList.ItemsSource = _bookmarks.ToList();
		foreach (var item in HomeDesk.Items)
		{
			item.RefreshIcon();
			Favicon.Queue(item.Url);
		}
	}

	async Task SweepProfile()
	{
		if (_parked || VaultDesk.SignSites().Length == 0) return;
		await EnsureEnv();
		var view = new WebView2 { Visibility = Visibility.Collapsed };
		ViewHost.Children.Add(view);
		try
		{
			await view.EnsureCoreWebView2Async(_env);
			var core = view.CoreWebView2;
			foreach (var site in VaultDesk.SignSites()) DropSite(core, site);
			_liveSites.Clear();
		}
		finally
		{
			ViewHost.Children.Remove(view);
			try { view.Dispose(); } catch { }
		}
	}

	async Task Arm(SiteTab tab, string site, string url)
	{
		try
		{
			await _vaultGate.WaitAsync();
			try
			{
				if (_parked || !_tabs.Contains(tab)) return;
				var core = tab.View.CoreWebView2;
				if (core == null) return;
				ReleaseSitesNow(core);
				if (!VaultDesk.HasSignIn(site) || _liveSites.Contains(site)) return;
				if (!VaultDesk.TrySite(tab.PageUrl, out var page) || page != site) return;
				Inject(core, site);
				_liveSites.Add(site);
				if (_parked)
				{
					DropSite(core, site);
					_liveSites.Remove(site);
					return;
				}
				core.Navigate(url);
			}
			finally
			{
				_vaultGate.Release();
			}
		}
		catch { }
	}

	async Task ReleaseSites(SiteTab tab)
	{
		try
		{
			await _vaultGate.WaitAsync();
			try
			{
				if (_parked) return;
				var core = tab.View.CoreWebView2 ?? AnyCore();
				if (core != null) ReleaseSitesNow(core);
			}
			finally
			{
				_vaultGate.Release();
			}
		}
		catch { }
	}

	void ReleaseSitesNow(CoreWebView2 core)
	{
		foreach (var site in _liveSites.ToArray())
		{
			if (SiteOpen(site)) continue;
			DropSite(core, site);
			_liveSites.Remove(site);
		}
	}

	bool SiteOpen(string site)
	{
		foreach (var tab in _tabs)
			if (VaultDesk.TrySite(tab.PageUrl, out var got) && got == site) return true;
		return false;
	}

	CoreWebView2? AnyCore()
	{
		foreach (var tab in _tabs)
			if (tab.View.CoreWebView2 != null) return tab.View.CoreWebView2;
		return null;
	}

	static void DropSite(CoreWebView2 core, string site)
	{
		foreach (var name in VaultDesk.CookieNames(site))
		{
			if (name.Length == 0) continue;
			try { core.CookieManager.DeleteCookies(name, site + "/"); } catch { }
		}
	}

	void Inject(CoreWebView2 core, string site)
	{
		var manager = core.CookieManager;
		foreach (var cookie in VaultDesk.Cookies(site))
		{
			try
			{
				if (cookie.Name.Length == 0 || cookie.Domain.Length == 0) continue;
				var path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path;
				var made = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, path);
				made.IsHttpOnly = cookie.HttpOnly;
				made.IsSecure = cookie.Secure;
				made.SameSite = cookie.SameSite switch
				{
					"none" => CoreWebView2CookieSameSiteKind.None,
					"strict" => CoreWebView2CookieSameSiteKind.Strict,
					_ => CoreWebView2CookieSameSiteKind.Lax
				};
				made.Expires = cookie.Session || cookie.Expires <= 0
					? DateTime.MinValue
					: DateTimeOffset.FromUnixTimeSeconds(cookie.Expires).UtcDateTime;
				manager.AddOrUpdateCookie(made);
			}
			catch { }
		}
	}

	async Task<bool> KeepCookies(SiteTab tab, string site)
	{
		if (_parked || !_tabs.Contains(tab)) return false;
		var core = tab.View.CoreWebView2;
		if (core == null) return false;
		IReadOnlyList<CoreWebView2Cookie> list;
		try { list = await core.CookieManager.GetCookiesAsync(site + "/"); }
		catch { return false; }
		if (_parked || !_tabs.Contains(tab)) return false;
		if (!VaultDesk.TrySite(tab.PageUrl, out var page) || page != site) return false;
		var seen = new List<VaultDesk.SeenCookie>(list.Count);
		foreach (var cookie in list)
		{
			var session = cookie.IsSession || cookie.Expires == DateTime.MinValue;
			long expires = 0;
			if (!session)
			{
				try
				{
					var when = cookie.Expires.Kind switch
					{
						DateTimeKind.Local => cookie.Expires.ToUniversalTime(),
						DateTimeKind.Utc => cookie.Expires,
						_ => DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)
					};
					expires = new DateTimeOffset(when).ToUnixTimeSeconds();
				}
				catch
				{
					session = true;
				}
			}
			seen.Add(new VaultDesk.SeenCookie(
				cookie.Name ?? "",
				cookie.Value ?? "",
				cookie.Domain ?? "",
				cookie.Path ?? "/",
				cookie.IsSecure,
				cookie.IsHttpOnly,
				session,
				expires,
				cookie.SameSite switch
				{
					CoreWebView2CookieSameSiteKind.None => "none",
					CoreWebView2CookieSameSiteKind.Strict => "strict",
					_ => "lax"
				}));
		}
		var previous = VaultDesk.CookieNames(site);
		if (!VaultDesk.SaveCookies(site, seen)) return false;
		var kept = new HashSet<string>(VaultDesk.CookieNames(site), StringComparer.Ordinal);
		foreach (var name in previous)
		{
			if (name.Length == 0 || kept.Contains(name)) continue;
			try { core.CookieManager.DeleteCookies(name, site + "/"); } catch { }
		}
		return true;
	}

	void NoteVaultPage(string? url)
	{
		var site = VaultDesk.TrySite(url, out var got) ? got : "";
		if (site == _vaultPage) return;
		_vaultPage = site;
		_vaultDismissed = false;
		ClearVaultPrompt();
	}

	void ClearVaultPrompt()
	{
		_vaultKind = "";
		_vaultUser = "";
		_vaultPass = "";
		VaultText.Text = "";
		VaultHint.Text = "";
		VaultBar.Visibility = Visibility.Collapsed;
	}

	void DismissVault()
	{
		ClearVaultPrompt();
		_vaultDismissed = true;
	}

	void NeverVault()
	{
		var site = _vaultPage;
		ClearVaultPrompt();
		_vaultDismissed = true;
		if (site.Length > 0) VaultDesk.Never(site);
	}

	void ShowVault(string kind, string user, string pass)
	{
		if (_vaultPage.Length == 0) return;
		_vaultKind = kind;
		_vaultUser = kind == "fill" ? "" : user;
		_vaultPass = kind == "fill" ? "" : pass;
		var label = VaultDesk.Label(_vaultPage);
		if (kind == "fill")
		{
			VaultText.Text = "Use the saved password for " + label + "?";
			VaultHint.Text = "";
			VaultYes.Content = "Fill";
			VaultNever.Visibility = Visibility.Collapsed;
		}
		else
		{
			VaultText.Text = (kind == "update" ? "Update the password for " : "Save the password for ") + label + " on this PC?";
			VaultHint.Text = "The sign-in cookie is encrypted with it and loaded only on this site.";
			VaultYes.Content = kind == "update" ? "Update" : "Save";
			VaultNever.Visibility = Visibility.Visible;
		}
		VaultBar.Visibility = _pageFull ? Visibility.Collapsed : Visibility.Visible;
	}

	void OnVaultMessage(SiteTab tab, string? source, string kind, string user, string pass)
	{
		try
		{
			if (_parked || tab != Active || !_tabs.Contains(tab)) return;
			if (!VaultDesk.TrySite(source, out var from)) return;
			if (!VaultDesk.TrySite(tab.PageUrl, out var page) || from != page || page != _vaultPage) return;
			if (_vaultDismissed) return;
			if (kind == "focus")
			{
				if (_vaultKind is "save" or "update") return;
				if (!VaultDesk.HasPassword(page)) return;
				ShowVault("fill", "", "");
				return;
			}
			if (kind != "save") return;
			var offer = VaultDesk.Offer(page, user, pass);
			if (offer.Length == 0) return;
			ShowVault(offer, user, pass);
		}
		finally
		{
			user = "";
			pass = "";
		}
	}

	async Task AcceptVault()
	{
		var kind = _vaultKind;
		var site = _vaultPage;
		var user = _vaultUser;
		var pass = _vaultPass;
		ClearVaultPrompt();
		try
		{
			if (kind is "save" or "update")
			{
				var note = VaultDesk.SavePassword(site, user, pass);
				if (note.Length > 0)
				{
					SetNote(note);
					return;
				}
				_vaultDismissed = true;
				SetNote("Saved for this PC.");
				var tab = Active;
				if (tab != null && VaultDesk.TrySite(tab.PageUrl, out var page) && page == site)
					_ = KeepCookies(tab, site);
				RefreshLogins();
				return;
			}
			if (kind != "fill") return;
			_vaultDismissed = true;
			if (!VaultDesk.TryPassword(site, out user, out pass))
			{
				SetNote("The saved password could not be opened.");
				return;
			}
			var script = VaultDesk.FillScript(user, pass);
			var core = Active?.View.CoreWebView2;
			if (_parked || core == null || !VaultDesk.TrySite(core.Source, out var here) || here != site)
			{
				SetNote("The password could not be filled.");
				return;
			}
			await core.ExecuteScriptAsync(script);
		}
		catch
		{
			if (kind == "fill") SetNote("The password could not be filled.");
		}
		finally
		{
			user = "";
			pass = "";
		}
	}

	void RefreshLogins()
	{
		_passStamp = "";
		try { _logins = VaultDesk.Logins(); }
		catch { _logins = []; }
		if (_settingsOpen && _settingsSection == "passwords") PaintPasswords();
	}

	void ClearShown()
	{
		foreach (var site in _shownPasses.Keys.ToArray()) _shownPasses[site] = "";
		_shownPasses.Clear();
		_passStamp = "";
		if (_settingsOpen && _settingsSection == "passwords") PaintPasswords();
		else
		{
			foreach (var box in _passBoxes) box.Text = "";
			_passBoxes.Clear();
		}
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
	void SaveBookmarks() => WriteFile(BookmarkFile, SiteDesk.WriteMarks(_bookmarks));

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
		else if (key == Key.Escape && _vaultKind.Length > 0)
		{
			DismissVault();
			handled = true;
		}
		else if (key == Key.Escape && HomeEditor.Visibility == Visibility.Visible)
		{
			CloseHomeEditor();
			handled = true;
		}
		else if (key == Key.Escape && HomeProviderPopup.IsOpen)
		{
			HomeProviderPopup.IsOpen = false;
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
		else if (ctrl && key == Key.T) { OpenTab(false); handled = true; }
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
		else if (ctrl && !shift && !alt && key == Key.D9)
		{
			if (_tabs.Count > 0) Select(_tabs[^1]);
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

	ContextMenu TabMenu(SiteTab tab)
	{
		var items = new List<(string Header, RoutedEventHandler Click)>
		{
			("New tab", (_, _) => OpenTab(false)),
			("Duplicate", (_, _) => _ = Duplicate(tab)),
			("Copy address", (_, _) => CopyAddress(tab)),
			("Close", (_, _) => CloseTab(tab)),
			("Close other tabs", (_, _) => CloseOthers(tab))
		};
		if (_closed.Count > 0)
			items.Add(("Reopen closed tab", (_, _) => Reopen()));
		return DarkMenu(items.ToArray());
	}

	async Task Duplicate(SiteTab tab)
	{
		var url = tab.View.CoreWebView2?.Source ?? "";
		if (LinkRules.IsLocalFile(url))
		{
			var copy = await AddTab(null, true, false);
			var core = copy?.View.CoreWebView2;
			if (core == null) return;
			_typedFile = url;
			core.Navigate(url);
			return;
		}
		if (SiteDesk.IsPage(url))
		{
			await AddTab(url, true, false);
			return;
		}
		OpenTab(false);
	}

	void CopyAddress(SiteTab tab)
	{
		var url = tab.View.CoreWebView2?.Source ?? "";
		var text = SiteDesk.IsPage(url) ? url : LinkRules.IsLocalFile(url) ? LinkRules.LocalText(url) : "";
		if (text.Length == 0)
		{
			SetNote("This tab has no address to copy.");
			return;
		}
		try
		{
			System.Windows.Clipboard.SetText(text);
			SetNote("Address copied.");
		}
		catch (Exception ex)
		{
			SetNote(ex.Message);
		}
	}

	void CloseOthers(SiteTab keep)
	{
		foreach (var tab in _tabs.Where(item => item != keep).ToArray())
			CloseTab(tab);
		if (_tabs.Contains(keep)) Select(keep);
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

	void HoldChrome() => _popupUntil = Environment.TickCount64 + 400;

	bool PopupHeld() => Environment.TickCount64 < _popupUntil;

	void ShowDownloads()
	{
		HoldChrome();
		_downloadsOpen = true;
		_menuOpen = false;
		MenuPopup.IsOpen = false;
		var width = DownloadsButton.ActualWidth;
		if (width > 0) DownloadsPopup.HorizontalOffset = width - 380;
		DownloadsPopup.IsOpen = true;
		PaintDownloadEmpty();
		PaintDownloadButton();
	}

	void CloseDownloads()
	{
		_downloadsOpen = false;
		DownloadsPopup.IsOpen = false;
		_openRow = null;
		DownloadAsk.Visibility = Visibility.Collapsed;
	}

	void CloseMenu()
	{
		_menuOpen = false;
		MenuPopup.IsOpen = false;
	}

	void CloseSuggest()
	{
		_suggestPick = -1;
		SuggestPopup.IsOpen = false;
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

	void OnNewTab(object sender, RoutedEventArgs e) => OpenTab(false);

	void OnReopen(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		Reopen();
	}

	void OpenTab(bool keepChrome)
	{
		if (!keepChrome)
		{
			CloseMenu();
			CloseSettings();
		}
		_keptAddress = null;
		var target = StartDesk.TabTarget();
		_ = AddTab(target.Length == 0 ? null : target, true, target.Length == 0);
	}

	void OnHome(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		var target = StartDesk.HomeTarget();
		_keptAddress = null;
		if (target.Length == 0)
		{
			CloseSettings();
			CloseSuggest();
			_historyOpen = false;
			var core = Active?.View.CoreWebView2;
			if (core == null)
			{
				SetNote("The page is not ready.");
				return;
			}
			SetNote("");
			core.Navigate("about:blank");
			return;
		}
		GoUrl(target);
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
	void OnDownloadsToggle(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		ToggleDownloads();
	}
	void OnMenu(object sender, RoutedEventArgs e)
	{
		if (_menuOpen) CloseMenu();
		else
		{
			HoldChrome();
			_menuOpen = true;
			CloseDownloads();
			MenuPopup.IsOpen = true;
		}
	}
	void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings("general");
	void OnVaultYes(object sender, RoutedEventArgs e) => _ = AcceptVault();
	void OnVaultNever(object sender, RoutedEventArgs e) => NeverVault();
	void OnVaultNotNow(object sender, RoutedEventArgs e) => DismissVault();
	async void OnProtectSignIn(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		var tab = Active;
		var core = tab?.View.CoreWebView2;
		if (_parked || core == null || !VaultDesk.TrySite(core.Source, out var site))
		{
			SetNote("This page has no sign-in cookie to protect.");
			return;
		}
		var saved = await KeepCookies(tab!, site);
		SetNote(saved
			? "Sign-in for " + VaultDesk.Label(site) + " is protected on this PC."
			: "This page has no sign-in cookie to protect.");
	}
	void OnForgetSignIn(object sender, RoutedEventArgs e)
	{
		CloseMenu();
		var core = Active?.View.CoreWebView2;
		if (core == null || !VaultDesk.TrySite(core.Source, out var site)) return;
		var had = VaultDesk.HasPassword(site) || VaultDesk.HasSignIn(site);
		var names = VaultDesk.CookieNames(site);
		VaultDesk.ForgetSite(site);
		foreach (var name in names)
		{
			if (name.Length == 0) continue;
			try { core.CookieManager.DeleteCookies(name, site + "/"); } catch { }
		}
		_liveSites.Remove(site);
		if (_vaultPage == site) DismissVault();
		if (had) SetNote("Sign-in for " + VaultDesk.Label(site) + " was forgotten on this PC.");
		_shownPasses.Remove(site);
		RefreshLogins();
	}
	void OnForgetSignIns(object sender, RoutedEventArgs e)
	{
		var core = AnyCore();
		var sites = VaultDesk.SignSites();
		var names = new Dictionary<string, string[]>(StringComparer.Ordinal);
		foreach (var site in sites) names[site] = VaultDesk.CookieNames(site);
		foreach (var site in _logins) names.TryAdd(site.Site, VaultDesk.CookieNames(site.Site));
		VaultDesk.ForgetAll();
		if (core != null)
		{
			foreach (var pair in names)
			{
				foreach (var name in pair.Value)
				{
					if (name.Length == 0) continue;
					try { core.CookieManager.DeleteCookies(name, pair.Key + "/"); } catch { }
				}
			}
		}
		_liveSites.Clear();
		ClearVaultPrompt();
		foreach (var site in _shownPasses.Keys.ToArray()) _shownPasses[site] = "";
		_shownPasses.Clear();
		RefreshLogins();
		SetNote("Saved sign-ins were forgotten on this PC.");
	}
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
		if (SuggestPopup.IsOpen && e.Key == Key.Down)
		{
			_suggestPick = Math.Min(SuggestList.Items.Count - 1, _suggestPick + 1);
			SuggestList.SelectedIndex = _suggestPick;
			e.Handled = true;
			return;
		}
		if (SuggestPopup.IsOpen && e.Key == Key.Up)
		{
			_suggestPick = Math.Max(-1, _suggestPick - 1);
			SuggestList.SelectedIndex = _suggestPick;
			e.Handled = true;
			return;
		}
		if (e.Key == Key.Escape && SuggestPopup.IsOpen)
		{
			CloseSuggest();
			e.Handled = true;
			return;
		}
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		if (SuggestPopup.IsOpen && SuggestList.SelectedItem is SiteVisit visit)
		{
			CloseSuggest();
			GoUrl(visit.Url);
			return;
		}
		CloseSuggest();
		Go();
	}

	void OnAddressChanged(object sender, TextChangedEventArgs e)
	{
		if (!_addressLock) _keptAddress = null;
		AddressHint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
		if (_addressLock || !Address.IsKeyboardFocusWithin) return;
		ShowSuggest();
	}

	void OnAddressMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount >= 3)
		{
			Address.Focus();
			Address.SelectAll();
			_addressSelectAll = false;
			e.Handled = true;
			return;
		}
		if (e.ClickCount == 2)
		{
			Address.Focus();
			var index = Address.GetCharacterIndexFromPoint(e.GetPosition(Address), true);
			if (index < 0) index = Math.Max(0, Address.CaretIndex);
			var token = SiteDesk.UrlToken(Address.Text, index);
			Address.Select(token.Start, token.Length);
			_addressSelectAll = false;
			e.Handled = true;
			return;
		}
		_addressSelectAll = !Address.IsKeyboardFocusWithin;
		_addressDown = e.GetPosition(Address);
	}

	void OnAddressMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_addressSelectAll) return;
		_addressSelectAll = false;
		var moved = e.GetPosition(Address) - _addressDown;
		if (Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4) return;
		var count = Address.Text.Length;
		if (count == 0) return;
		Address.Select(count, -count);
	}

	void ShowSuggest()
	{
		var page = Active?.View.CoreWebView2?.Source ?? "";
		if (string.Equals(Address.Text.Trim(), page, StringComparison.OrdinalIgnoreCase))
		{
			CloseSuggest();
			return;
		}
		var items = SiteDesk.Suggest(_bookmarks, _history, Address.Text);
		if (items.Count == 0)
		{
			CloseSuggest();
			return;
		}
		_suggestPick = -1;
		SuggestList.SelectedIndex = -1;
		SuggestList.ItemsSource = items;
		if (SuggestPopup.Child is FrameworkElement child)
			child.Width = Math.Max(360, AddressHost.ActualWidth);
		SuggestPopup.IsOpen = true;
	}

	void OnSuggestClick(object sender, MouseButtonEventArgs e)
	{
		if (SuggestList.SelectedItem is not SiteVisit visit) return;
		CloseSuggest();
		GoUrl(visit.Url);
	}

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
		if (sender is not FrameworkElement el || el.DataContext is not SiteTab tab) return;
		if (e.ChangedButton == MouseButton.Middle)
		{
			CloseTab(tab);
			e.Handled = true;
			return;
		}
		if (e.ChangedButton != MouseButton.Right) return;
		e.Handled = true;
		var menu = TabMenu(tab);
		menu.PlacementTarget = el;
		menu.IsOpen = true;
	}

	void OnCloseTab(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement el && el.Tag is SiteTab tab) CloseTab(tab);
		e.Handled = true;
	}

	void OnVisitOpen(object sender, MouseButtonEventArgs e)
	{
		if (sender is not ListBox box || box.SelectedItem is not SiteVisit visit) return;
		GoUrl(visit.Url);
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

	void OnImportBrowser(object sender, RoutedEventArgs e)
	{
		if (sender is not Button button || button.Tag is not string browser) return;
		BookmarkImportChoices.Children.Clear();
		SettingsImportChoices.Children.Clear();
		IReadOnlyList<FavoriteProfile> profiles;
		try { profiles = Favorites.Profiles(browser); }
		catch
		{
			SetImport(Favorites.Missing(browser));
			return;
		}
		if (profiles.Count == 0)
		{
			SetImport(Favorites.Missing(browser));
			return;
		}
		if (profiles.Count == 1)
		{
			ImportOne(profiles[0]);
			return;
		}
		SetImport("Choose one " + Favorites.Label(browser) + " profile.");
		foreach (var profile in profiles)
		{
			BookmarkImportChoices.Children.Add(ProfileButton(profile));
			SettingsImportChoices.Children.Add(ProfileButton(profile));
		}
	}

	Button ProfileButton(FavoriteProfile profile)
	{
		var pick = new Button
		{
			Content = profile.Name,
			Tag = profile,
			Margin = new Thickness(0, 0, 8, 8),
			Padding = new Thickness(12, 6, 12, 6)
		};
		pick.Click += (_, _) => ImportOne(profile);
		return pick;
	}

	void ImportOne(FavoriteProfile profile)
	{
		BookmarkImportChoices.Children.Clear();
		SettingsImportChoices.Children.Clear();
		try
		{
			var incoming = Favorites.Read(profile);
			var merged = Favorites.Merge(_bookmarks, incoming);
			SaveBookmarks();
			FillLists();
			PaintChrome();
			var text = "Imported " + merged.Added + " from " + Favorites.Label(profile.Browser) + " (" + profile.Name + ").";
			if (merged.Already > 0) text += " " + merged.Already + " were already bookmarked.";
			if (merged.Full) text += " The bookmark list is full.";
			SetImport(text);
		}
		catch
		{
			SetImport("That " + Favorites.Label(profile.Browser) + " profile could not be read.");
		}
	}

	void SetImport(string text)
	{
		BookmarkImportStatus.Text = text;
		SettingsImportStatus.Text = text;
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
		if (!SamePath(dir, path) && !SiteDesk.IsInside(dir, path)) return;
		Reveal(path);
	}

	void OnOpenDownloadsFolder(object sender, RoutedEventArgs e) => Reveal(DownloadsDirectory());

	void OnOpenDownload(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement el || el.Tag is not DownloadRow row || row.Running || !CanOpen(row)) return;
		_openRow = row;
		var path = OpenPath(row);
		var folder = Directory.Exists(path) && !File.Exists(path);
		DownloadAskText.Text = folder
			? "Open the folder for " + row.Name + "?"
			: IsProgram(path) ? "Run " + row.Name + " on this PC?" : "Open " + row.Name + "?";
		DownloadAsk.Visibility = Visibility.Visible;
	}

	void OnApproveOpen(object sender, RoutedEventArgs e)
	{
		var row = _openRow;
		_openRow = null;
		DownloadAsk.Visibility = Visibility.Collapsed;
		if (row == null || !CanOpen(row)) return;
		var path = OpenPath(row);
		try
		{
			if (File.Exists(path))
				Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
			else
				Reveal(path);
		}
		catch (Exception ex)
		{
			SetNote(ex.Message);
		}
	}

	void OnDeclineOpen(object sender, RoutedEventArgs e)
	{
		_openRow = null;
		DownloadAsk.Visibility = Visibility.Collapsed;
	}

	static string OpenPath(DownloadRow row) => row.Torrent != null ? TorrentDesk.SaveRoot : row.Path;

	bool CanOpen(DownloadRow row)
	{
		var path = OpenPath(row);
		if (string.IsNullOrWhiteSpace(path)) return false;
		var downloads = DownloadsDirectory();
		if (SamePath(downloads, path) || SiteDesk.IsInside(downloads, path)) return true;
		return SiteDesk.IsInside(Path.Combine(Root, "opened-torrents"), path);
	}

	static bool IsProgram(string path)
	{
		var ext = Path.GetExtension(path);
		return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".com", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".scr", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".vbs", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".hta", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
	}

	void PaintDownloadButton()
	{
		var active = 0;
		double sum = 0;
		var known = 0;
		foreach (var row in _downloads)
		{
			if (!row.Running) continue;
			active++;
			if (row.Fraction > 0.001)
			{
				sum += row.Fraction;
				known++;
			}
		}
		var show = active > 0;
		DownloadTrack.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		DownloadArc.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		DownloadBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		if (!show)
		{
			StopRing();
			return;
		}
		DownloadCount.Text = active > 9 ? "9+" : active.ToString(CultureInfo.InvariantCulture);
		if (known == 0)
		{
			SetRing(0.22);
			StartRing();
			return;
		}
		StopRing();
		SetRing(sum / known);
	}

	void SetRing(double fraction)
	{
		const double full = Math.PI * 26.0 / 2.0;
		var draw = Math.Clamp(fraction, 0.04, 1) * full;
		var gap = Math.Max(0.01, full - draw);
		DownloadArc.StrokeDashArray = new DoubleCollection { draw, gap };
	}

	void StartRing()
	{
		if (_ringSpin) return;
		_ringSpin = true;
		DownloadSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-90, 270, TimeSpan.FromSeconds(1.1))
		{
			RepeatBehavior = RepeatBehavior.Forever
		});
	}

	void StopRing()
	{
		if (!_ringSpin) return;
		_ringSpin = false;
		DownloadSpin.BeginAnimation(RotateTransform.AngleProperty, null);
		DownloadSpin.Angle = -90;
	}

	static bool SamePath(string left, string right)
	{
		try
		{
			return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	static void Reveal(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = "explorer.exe",
					Arguments = "/select,\"" + path + "\"",
					UseShellExecute = true
				});
				return;
			}
			var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
			if (string.IsNullOrWhiteSpace(dir)) return;
			Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
		}
		catch
		{
		}
	}

	protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseUp(e);
		if (e.OriginalSource is not DependencyObject source) return;
		if (!PopupHeld() && _downloadsOpen && !IsWithin(DownloadPane, source) && !IsWithin(DownloadsButton, source))
			CloseDownloads();
		if (!PopupHeld() && _menuOpen && !IsWithin(MenuPane, source) && !IsWithin(MenuButton, source))
			CloseMenu();
		if (SuggestPopup.IsOpen && !IsWithin(AddressHost, source) && !IsWithin(SuggestPopup, source))
			CloseSuggest();
		if (HomeProviderPopup.IsOpen && !IsWithin(HomeProviderPane, source) && !IsWithin(HomeProviderButton, source))
			HomeProviderPopup.IsOpen = false;
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
		ClearShown();
		SettingsPage.Visibility = Visibility.Collapsed;
		PaintChrome();
	}

	void ShowSection(string name)
	{
		if (_settingsSection == "passwords" && name != "passwords") ClearShown();
		_settingsSection = name;
		PageGeneral.Visibility = name == "general" ? Visibility.Visible : Visibility.Collapsed;
		PageDownloads.Visibility = name == "downloads" ? Visibility.Visible : Visibility.Collapsed;
		PageVpn.Visibility = name == "vpn" ? Visibility.Visible : Visibility.Collapsed;
		PagePrivacy.Visibility = name == "privacy" ? Visibility.Visible : Visibility.Collapsed;
		PagePasswords.Visibility = name == "passwords" ? Visibility.Visible : Visibility.Collapsed;
		if (name == "passwords") RefreshLogins();
		MarkNav(NavGeneral, name == "general");
		MarkNav(NavDownloads, name == "downloads");
		MarkNav(NavVpn, name == "vpn");
		MarkNav(NavPrivacy, name == "privacy");
		MarkNav(NavPasswords, name == "passwords");
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

	void LoadBookBar()
	{
		try
		{
			var path = Path.Combine(Root, "bookmarks-bar.txt");
			if (File.Exists(path) && File.ReadAllText(path).Trim() == "0") _bookBar = false;
		}
		catch
		{
		}
	}

	void SaveBookBar()
	{
		try
		{
			Directory.CreateDirectory(Root);
			File.WriteAllText(Path.Combine(Root, "bookmarks-bar.txt"), _bookBar ? "1" : "0");
		}
		catch
		{
		}
	}

	void OnBookBarToggle(object sender, RoutedEventArgs e)
	{
		_bookBar = !_bookBar;
		SaveBookBar();
		CloseMenu();
		PaintChrome();
	}

	void OnBookBarOpen(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: SiteVisit visit }) return;
		GoUrl(visit.Url);
	}

	async Task StoreIcon(CoreWebView2 core)
	{
		try { await Favicon.Store(core); }
		catch { }
		await Dispatcher.InvokeAsync(FillLists);
	}

	void PaintHome()
	{
		HomeProviderButton.Content = HomeDesk.Label(HomeDesk.Provider);
		HomeWrap.Children.Clear();
		foreach (var item in HomeDesk.Items)
			HomeWrap.Children.Add(HomeTile(item));
		if (HomeDesk.Items.Count < HomeDesk.Max)
			HomeWrap.Children.Add(HomeAddTile());
	}

	Button HomeTile(HomeShortcut item)
	{
		var mark = new TextBlock
		{
			Text = item.Mark,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 18,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = Ink,
			IsHitTestVisible = false
		};
		void ShowMark() => mark.Visibility = item.Icon == null ? Visibility.Visible : Visibility.Collapsed;
		item.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName is null or nameof(HomeShortcut.Icon))
				mark.Dispatcher.Invoke(ShowMark);
		};
		ShowMark();
		var image = new Image
		{
			Width = 48,
			Height = 48,
			Stretch = Stretch.Uniform,
			IsHitTestVisible = false
		};
		image.Clip = new RectangleGeometry(new Rect(0, 0, 48, 48), 10, 10);
		image.SetBinding(Image.SourceProperty, new Binding(nameof(HomeShortcut.Icon)) { Source = item });
		var face = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
		face.Children.Add(new Border
		{
			Width = 48,
			Height = 48,
			CornerRadius = new CornerRadius(10),
			Background = TileFace,
			IsHitTestVisible = false
		});
		face.Children.Add(mark);
		face.Children.Add(image);
		var name = new TextBlock
		{
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 12,
			Foreground = Ink,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 8, 0, 0),
			MaxWidth = 88,
			IsHitTestVisible = false
		};
		name.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeShortcut.Name)) { Source = item });
		var body = new StackPanel { Width = 96, Margin = new Thickness(0, 8, 0, 4), IsHitTestVisible = false };
		body.Children.Add(face);
		body.Children.Add(name);
		var more = QuietButton("\uE712", 22);
		more.HorizontalAlignment = HorizontalAlignment.Right;
		more.VerticalAlignment = VerticalAlignment.Top;
		more.Opacity = 0;
		more.IsHitTestVisible = false;
		more.ToolTip = "Shortcut";
		var root = new Grid { Width = 104, Height = 108 };
		root.Children.Add(body);
		root.Children.Add(more);
		var button = QuietButton("", 104);
		button.Content = root;
		button.Tag = item;
		button.Height = 108;
		button.Margin = new Thickness(4);
		button.FontFamily = new FontFamily("Segoe UI");
		button.ToolTip = item.Url;
		button.Click += (_, _) =>
		{
			if (button.Tag is HomeShortcut shortcut) GoUrl(shortcut.Url);
		};
		var menu = DarkMenu(
			("Edit", (_, _) => OpenHomeEditor(item)),
			("Remove", (_, _) =>
			{
				HomeDesk.Remove(item);
				PaintHome();
			}));
		more.ContextMenu = menu;
		more.Click += (_, args) =>
		{
			args.Handled = true;
			menu.PlacementTarget = more;
			menu.IsOpen = true;
		};
		button.MouseEnter += (_, _) =>
		{
			more.Opacity = 1;
			more.IsHitTestVisible = true;
		};
		button.MouseLeave += (_, _) =>
		{
			if (menu.IsOpen) return;
			more.Opacity = 0;
			more.IsHitTestVisible = false;
		};
		menu.Closed += (_, _) =>
		{
			if (button.IsMouseOver) return;
			more.Opacity = 0;
			more.IsHitTestVisible = false;
		};
		return button;
	}

	Button HomeAddTile()
	{
		var plus = new TextBlock
		{
			Text = "\uE710",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 16,
			Foreground = Ink,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		var face = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
		face.Children.Add(new Border
		{
			Width = 48,
			Height = 48,
			CornerRadius = new CornerRadius(10),
			Background = TileFace,
			IsHitTestVisible = false
		});
		face.Children.Add(plus);
		var caption = new TextBlock
		{
			Text = "Add",
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 12,
			Foreground = Ink,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 8, 0, 0),
			IsHitTestVisible = false
		};
		var body = new StackPanel { Width = 96, Margin = new Thickness(0, 8, 0, 4) };
		body.Children.Add(face);
		body.Children.Add(caption);
		var button = QuietButton("", 104);
		button.Content = body;
		button.Height = 108;
		button.Margin = new Thickness(4);
		button.FontFamily = new FontFamily("Segoe UI");
		button.ToolTip = "Add a shortcut";
		button.Click += (_, _) => OpenHomeEditor(null);
		button.ContextMenu = DarkMenu(
			("Import", (_, _) => ImportHome()),
			("Export", (_, _) => ExportHome()),
			("Remove all", (_, _) =>
			{
				HomeDesk.Clear();
				PaintHome();
			}));
		return button;
	}

	static Button QuietButton(string glyph, double width) => new()
	{
		Content = glyph,
		Width = width,
		Height = width,
		Padding = new Thickness(0),
		Margin = new Thickness(0),
		Background = Brushes.Transparent,
		BorderThickness = new Thickness(0),
		Foreground = Ink,
		FontFamily = new FontFamily("Segoe MDL2 Assets"),
		FontSize = 12,
		Cursor = Cursors.Hand
	};

	static ContextMenu DarkMenu(params (string Header, RoutedEventHandler Click)[] items)
	{
		var menu = new ContextMenu
		{
			Background = PanelBrush,
			Foreground = Ink,
			BorderBrush = LineBrush
		};
		foreach (var item in items)
		{
			var entry = new MenuItem { Header = item.Header, Foreground = Ink, Background = Brushes.Transparent };
			entry.Click += item.Click;
			menu.Items.Add(entry);
		}
		return menu;
	}

	void OpenHomeEditor(HomeShortcut? item)
	{
		_homeEdit = item;
		HomeEditorTitle.Text = item == null ? "Add shortcut" : "Edit shortcut";
		HomeName.Text = item?.Name ?? "";
		HomeUrl.Text = item?.Url ?? "";
		HomeNote.Text = "";
		HomeProviderPopup.IsOpen = false;
		HomeEditor.Visibility = Visibility.Visible;
		HomeName.Focus();
	}

	void CloseHomeEditor()
	{
		HomeEditor.Visibility = Visibility.Collapsed;
		_homeEdit = null;
	}

	void OnHomeCancel(object sender, RoutedEventArgs e) => CloseHomeEditor();

	void OnHomeSave(object sender, RoutedEventArgs e)
	{
		var ok = _homeEdit == null
			? HomeDesk.TryAdd(HomeName.Text, HomeUrl.Text, out var error)
			: HomeDesk.TryUpdate(_homeEdit, HomeName.Text, HomeUrl.Text, out error);
		HomeNote.Text = error;
		if (!ok) return;
		CloseHomeEditor();
		PaintHome();
	}

	void OnHomeFieldKey(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		OnHomeSave(sender, e);
	}

	void OnHomeProvider(object sender, RoutedEventArgs e) =>
		HomeProviderPopup.IsOpen = !HomeProviderPopup.IsOpen;

	void PaintStart()
	{
		_startLock = true;
		HomePage.IsChecked = StartDesk.HomeMode != StartDesk.Custom;
		HomeCustom.IsChecked = StartDesk.HomeMode == StartDesk.Custom;
		TabHome.IsChecked = StartDesk.TabMode == StartDesk.Home;
		TabPage.IsChecked = StartDesk.TabMode == StartDesk.Page;
		TabCustom.IsChecked = StartDesk.TabMode == StartDesk.Custom;
		HomeCustomUrl.Text = StartDesk.HomeUrl;
		TabCustomUrl.Text = StartDesk.TabUrl;
		_startLock = false;
		PaintStartNotes();
	}

	void PaintStartNotes()
	{
		var homeCustom = StartDesk.HomeMode == StartDesk.Custom;
		var tabCustom = StartDesk.TabMode == StartDesk.Custom;
		HomeCustomUrl.Visibility = homeCustom ? Visibility.Visible : Visibility.Collapsed;
		TabCustomUrl.Visibility = tabCustom ? Visibility.Visible : Visibility.Collapsed;
		var homeBad = homeCustom && HomeCustomUrl.Text.Trim().Length > 0 && StartDesk.PageTarget(HomeCustomUrl.Text).Length == 0;
		var tabBad = tabCustom && TabCustomUrl.Text.Trim().Length > 0 && StartDesk.PageTarget(TabCustomUrl.Text).Length == 0;
		HomeCustomNote.Visibility = homeBad ? Visibility.Visible : Visibility.Collapsed;
		TabCustomNote.Visibility = tabBad ? Visibility.Visible : Visibility.Collapsed;
	}

	void OnStartChoice(object sender, RoutedEventArgs e)
	{
		if (_startLock || sender is not System.Windows.Controls.Primitives.ToggleButton box || box.IsChecked != true) return;
		var name = (sender as FrameworkElement)?.Name;
		if (name is "HomePage" or "HomeCustom")
			StartDesk.SetHome(name == "HomeCustom" ? StartDesk.Custom : StartDesk.Page, HomeCustomUrl.Text);
		else if (name is "TabHome" or "TabPage" or "TabCustom")
		{
			var mode = name == "TabCustom" ? StartDesk.Custom : name == "TabPage" ? StartDesk.Page : StartDesk.Home;
			StartDesk.SetTab(mode, TabCustomUrl.Text);
		}
		PaintStart();
	}

	void OnStartUrl(object sender, RoutedEventArgs e)
	{
		if (_startLock) return;
		StartDesk.SetHome(StartDesk.HomeMode, HomeCustomUrl.Text);
		StartDesk.SetTab(StartDesk.TabMode, TabCustomUrl.Text);
		PaintStartNotes();
	}

	void OnStartUrlKey(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		OnStartUrl(sender, e);
	}

	void OnPassFilter(object sender, RoutedEventArgs e)
	{
		if (_passLock || sender is not System.Windows.Controls.Primitives.ToggleButton box || box.IsChecked != true) return;
		_passHere = (sender as FrameworkElement)?.Name == "PassHere";
		if (_passHere && VaultDesk.TrySite(Active?.PageUrl, out var site)) _passSite = site;
		_passStamp = "";
		PaintPasswords();
	}

	void OnImportPasswords(object sender, RoutedEventArgs e)
	{
		var dlg = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
			Title = "Import passwords"
		};
		if (dlg.ShowDialog() != true) return;
		try
		{
			PassImportNote.Text = VaultDesk.ImportCsv(File.ReadAllText(dlg.FileName));
		}
		catch
		{
			PassImportNote.Text = "That file could not be read.";
		}
		RefreshLogins();
	}

	void PaintPasswords()
	{
		var page = Active?.PageUrl ?? "";
		if (page.Length == 0) page = Active?.View.CoreWebView2?.Source ?? "";
		var here = "";
		if (VaultDesk.TrySite(page, out var site) && _logins.Any(row => row.Site == site)) here = site;
		if (_passHere && here != _passSite) _passHere = false;
		var showFilter = here.Length > 0 && _logins.Length > 0;
		PassFilter.Visibility = showFilter ? Visibility.Visible : Visibility.Collapsed;
		_passLock = true;
		PassAll.IsChecked = !_passHere;
		PassHere.IsChecked = _passHere && showFilter;
		_passLock = false;
		var shown = _passHere && here.Length > 0 ? _logins.Where(row => row.Site == here).ToArray() : _logins;
		PassEmpty.Visibility = _logins.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
		PassForget.Visibility = _logins.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		var stamp = (_passHere ? "1:" : "0:") + here + "\n" + string.Join("\n", shown.Select(row => row.Site + "\t" + row.User));
		if (stamp == _passStamp) return;
		_passStamp = stamp;
		_passBoxes.Clear();
		PassRows.Children.Clear();
		foreach (var row in shown) PassRows.Children.Add(PassRow(row));
	}

	System.Windows.Controls.Border PassRow(VaultDesk.SavedLogin row)
	{
		var revealed = _shownPasses.TryGetValue(row.Site, out var pass) && pass.Length > 0;
		var box = new System.Windows.Controls.TextBox
		{
			Text = revealed ? pass : "••••••••",
			IsReadOnly = true,
			BorderThickness = new Thickness(0),
			Background = Brushes.Transparent,
			Foreground = Ink,
			FontFamily = new FontFamily("Consolas"),
			Margin = new Thickness(0, 4, 0, 0)
		};
		_passBoxes.Add(box);
		var toggle = new Button
		{
			Content = revealed ? "Hide" : "Show",
			MinWidth = 72,
			Margin = new Thickness(8, 0, 0, 0),
			Padding = new Thickness(10, 4, 10, 4),
			Tag = row.Site
		};
		toggle.Click += (_, _) => TogglePass(row.Site, box, toggle);
		var remove = new Button
		{
			Content = "Remove",
			Margin = new Thickness(8, 0, 0, 0),
			Padding = new Thickness(10, 4, 10, 4),
			Tag = row.Site
		};
		remove.Click += (_, _) => ForgetOne(row.Site);
		var actions = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		actions.Children.Add(toggle);
		actions.Children.Add(remove);
		var names = new StackPanel();
		names.Children.Add(new TextBlock
		{
			Text = VaultDesk.Label(row.Site),
			FontWeight = FontWeights.SemiBold,
			Foreground = Ink
		});
		names.Children.Add(new TextBlock
		{
			Text = row.User.Length == 0 ? "No username" : row.User,
			Foreground = Muted,
			Margin = new Thickness(0, 2, 0, 0)
		});
		var head = new DockPanel();
		DockPanel.SetDock(actions, Dock.Right);
		head.Children.Add(actions);
		head.Children.Add(names);
		var stack = new StackPanel();
		stack.Children.Add(head);
		stack.Children.Add(box);
		return new System.Windows.Controls.Border
		{
			BorderBrush = LineBrush,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(0, 8, 0, 8),
			Child = stack
		};
	}

	void TogglePass(string site, System.Windows.Controls.TextBox box, Button toggle)
	{
		if (_shownPasses.ContainsKey(site))
		{
			_shownPasses[site] = "";
			_shownPasses.Remove(site);
			box.Text = "••••••••";
			toggle.Content = "Show";
			return;
		}
		if (!VaultDesk.TryPassword(site, out _, out var pass) || pass.Length == 0)
		{
			SetNote("The saved password could not be opened.");
			return;
		}
		_shownPasses[site] = pass;
		box.Text = pass;
		toggle.Content = "Hide";
	}

	void ForgetOne(string site)
	{
		var core = AnyCore();
		var names = VaultDesk.CookieNames(site);
		VaultDesk.ForgetSite(site);
		if (core != null)
		{
			foreach (var name in names)
			{
				if (name.Length == 0) continue;
				try { core.CookieManager.DeleteCookies(name, site + "/"); } catch { }
			}
		}
		_liveSites.Remove(site);
		if (_shownPasses.TryGetValue(site, out var shown))
		{
			_shownPasses[site] = "";
			_shownPasses.Remove(site);
			shown = "";
		}
		if (_vaultPage == site) DismissVault();
		RefreshLogins();
		SetNote("Sign-in for " + VaultDesk.Label(site) + " was forgotten on this PC.");
	}

	void OnHomeProviderPick(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag }) HomeDesk.SetProvider(tag);
		HomeProviderPopup.IsOpen = false;
		HomeProviderButton.Content = HomeDesk.Label(HomeDesk.Provider);
	}

	void OnHomeSearch(object sender, RoutedEventArgs e) => RunHomeSearch();

	void OnHomeSearchKey(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		e.Handled = true;
		RunHomeSearch();
	}

	void RunHomeSearch()
	{
		var query = HomeSearch.Text.Trim();
		if (query.Length == 0) return;
		CloseHomeEditor();
		HomeProviderPopup.IsOpen = false;
		GoUrl(HomeDesk.SearchUrl(HomeDesk.Provider, query));
	}

	void OnHomeImport(object sender, RoutedEventArgs e) => ImportHome();

	void OnHomeExport(object sender, RoutedEventArgs e) => ExportHome();

	void ImportHome()
	{
		var dlg = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "Shortcut file|*.json|All files|*.*",
			Title = "Import shortcuts"
		};
		if (dlg.ShowDialog() != true) return;
		try
		{
			HomeDesk.TryImport(File.ReadAllText(dlg.FileName), out var note);
			HomeNote.Text = note;
		}
		catch (Exception ex)
		{
			HomeNote.Text = ex.Message;
		}
		PaintHome();
	}

	void ExportHome()
	{
		var dlg = new Microsoft.Win32.SaveFileDialog
		{
			Filter = "Shortcut file|*.json|All files|*.*",
			FileName = "home.json",
			Title = "Export shortcuts"
		};
		if (dlg.ShowDialog() != true) return;
		try
		{
			File.WriteAllText(dlg.FileName, HomeDesk.ExportText());
			HomeNote.Text = "Shortcuts exported.";
		}
		catch (Exception ex)
		{
			HomeNote.Text = ex.Message;
		}
	}

	static SolidColorBrush MakeBrush(byte r, byte g, byte b)
	{
		var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}
}

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using Guardex.Tunnel;

namespace Guardex.Browser;

public partial class BrowserWindow : Window
{
	readonly NotifyIcon _icon;
	readonly bool _startArmed;
	readonly bool _loaded;
	readonly string _bootError;
	bool _armed;
	bool _busy;
	bool _allowClose;
	bool _iconTunnel;
	bool _iconArmed;
	bool _cover;
	bool _raisedForPage;
	WindowState _stateBeforePage;
	string _notice = "";
	DateTime _noticeUntil;
	Icon? _currentIcon;

	public BrowserWindow(bool armed, bool loaded, string error)
	{
		_startArmed = armed;
		_armed = armed;
		_loaded = loaded;
		_bootError = error ?? "";
		InitializeComponent();
		_icon = new NotifyIcon { Visible = true, Text = "GUARDEX Browser" };
		var menu = new ContextMenuStrip();
		menu.Items.Add("Open", null, (_, _) => ShowFromTray());
		menu.Items.Add("Exit", null, (_, _) => Exit());
		_icon.ContextMenuStrip = menu;
		_icon.MouseClick += (_, e) =>
		{
			if (e.Button == MouseButtons.Left) ShowFromTray();
		};
		SourceInitialized += (_, _) =>
		{
			var source = (HwndSource?)PresentationSource.FromVisual(this);
			source?.AddHook(WindowProc);
		};
		Pages.MinimizeRequested += () => WindowState = WindowState.Minimized;
		Pages.MaximizeRequested += () =>
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		Pages.PageFullChanged += OnPageFull;
		Pages.CloseRequested += Close;
		Pages.TunnelToggleRequested += OnVpn;
		Pages.TunnelImportRequested += OnImport;
		StateChanged += (_, _) => Pages.SetRestore(WindowState == WindowState.Maximized);
		Loaded += (_, _) => Boot();
		Paint();
	}

	public void ShowFromTray()
	{
		Show();
		WindowState = WindowState.Normal;
		Activate();
	}

	void Boot()
	{
		Pages.UseProxy(SocksGate.Port);
		TorrentDesk.Load();
		if (_armed && _loaded)
		{
			_busy = true;
			Paint();
			Task.Run(() =>
			{
				VpnHub.Start();
				Dispatcher.BeginInvoke(() =>
				{
					_busy = false;
					TorrentDesk.Sync();
					Paint();
				});
			});
		}
		Pages.Wake();
		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) =>
		{
			if (!_busy) Paint();
			TorrentDesk.Sync();
			Pages.TickShelf();
		};
		timer.Start();
	}

	void OnVpn()
	{
		if (_busy) return;
		_notice = "";
		if (_armed) TurnOff();
		else TurnOn();
	}

	void TurnOn()
	{
		if (!VpnHub.HasConfig)
		{
			Notice("Import a WireGuard or OpenVPN file first.");
			return;
		}
		_armed = true;
		_busy = true;
		SocksGate.ForceHold = true;
		VpnHub.Require = true;
		TunnelStore.SaveArmed(true);
		Paint();
		Task.Run(() =>
		{
			VpnHub.Start();
			Dispatcher.BeginInvoke(() =>
			{
				_busy = false;
				TorrentDesk.Sync();
				Paint();
			});
		});
	}

	void TurnOff()
	{
		_busy = true;
		Paint();
		Task.Run(() =>
		{
			VpnHub.Stop();
			VpnHub.Require = false;
			VpnHub.DroppedWhileUp = false;
			VpnHub.HandshakeTimedOut = false;
			SocksGate.ForceHold = false;
			Dispatcher.BeginInvoke(() =>
			{
				_armed = false;
				_busy = false;
				TunnelStore.SaveArmed(false);
				TorrentDesk.Sync();
				Paint();
			});
		});
	}

	void OnImport()
	{
		if (_busy) return;
		var dlg = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "WireGuard or OpenVPN|*.conf;*.ovpn|All files|*.*",
			Title = "Import a VPN config"
		};
		if (dlg.ShowDialog() != true) return;
		string text;
		try { text = File.ReadAllText(dlg.FileName); }
		catch (Exception ex) { Notice(ex.Message); return; }
		if (!VpnHub.LoadConfig(text, out var error))
		{
			Notice(error);
			return;
		}
		try { TunnelStore.SaveConfig(text); }
		catch (Exception ex) { Notice(ex.Message); return; }
		var dns = VpnHub.Kind == "WireGuard" && !VpnHub.HasDns ? " This file has no DNS address, so names will not resolve while the tunnel is on." : "";
		Notice(VpnHub.Kind + " config imported." + dns);
		if (_armed)
		{
			SocksGate.ForceHold = true;
			VpnHub.Require = true;
			_busy = true;
			Paint();
			Task.Run(() =>
			{
				VpnHub.Start();
				Dispatcher.BeginInvoke(() => { _busy = false; TorrentDesk.Sync(); Paint(); });
			});
		}
	}

	void Notice(string text)
	{
		_notice = text ?? "";
		_noticeUntil = DateTime.UtcNow.AddSeconds(8);
		Paint();
	}

	void Paint()
	{
		string status;
		if (DateTime.UtcNow < _noticeUntil && _notice.Length > 0)
			status = _notice;
		else if (_armed && !VpnHub.HasConfig)
			status = string.IsNullOrEmpty(_bootError)
				? "Pages are stopped. Import a config, or turn the tunnel off."
				: "Pages are stopped. " + _bootError;
		else if (_armed && VpnHub.TunnelOn)
		{
			var dns = VpnHub.Kind == "WireGuard" && !VpnHub.HasDns ? " Names will not resolve until the config has a DNS address." : "";
			status = "Tunnel on (" + VpnHub.Kind + " " + VpnHub.EndpointHost + "). Pages use the tunnel." + dns;
		}
		else if (_armed)
		{
			var why = string.IsNullOrEmpty(VpnHub.LastError) ? _bootError : VpnHub.LastError;
			status = string.IsNullOrEmpty(why) ? "Tunnel is not up. Pages are stopped." : "Pages are stopped. " + why;
		}
		else if (VpnHub.HasConfig)
		{
			var dns = VpnHub.Kind == "WireGuard" && !VpnHub.HasDns ? " This file has no DNS address, so names will not resolve while the tunnel is on." : "";
			status = "Tunnel off (" + VpnHub.Kind + "). Pages use this PC's network." + dns;
		}
		else if (!_startArmed || _loaded)
			status = "Tunnel off. Pages use this PC's network.";
		else
			status = "Tunnel off.";
		Pages.ApplyTunnel(status, _armed ? "Turn off" : "Turn on", !_busy);

		var tunnel = VpnHub.TunnelOn;
		if (_currentIcon == null || tunnel != _iconTunnel || _armed != _iconArmed)
		{
			var next = TrayMark.Make(tunnel, _armed);
			var prev = _currentIcon;
			_icon.Icon = next;
			_currentIcon = next;
			prev?.Dispose();
			_iconTunnel = tunnel;
			_iconArmed = _armed;
		}
		_icon.Text = tunnel ? "GUARDEX Browser tunnel on" : _armed ? "GUARDEX Browser pages stopped" : "GUARDEX Browser";
	}

	void Exit()
	{
		_allowClose = true;
		Close();
	}

	void OnPageFull(bool full)
	{
		_cover = full;
		if (full)
		{
			_stateBeforePage = WindowState;
			_raisedForPage = WindowState != WindowState.Maximized;
			if (_raisedForPage) WindowState = WindowState.Maximized;
			else NudgeMax();
			return;
		}
		if (_raisedForPage)
		{
			_raisedForPage = false;
			WindowState = WindowState.Normal;
			return;
		}
		NudgeMax();
	}

	void NudgeMax()
	{
		if (WindowState != WindowState.Maximized) return;
		WindowState = WindowState.Normal;
		WindowState = WindowState.Maximized;
	}

	// A page fullscreen covers the monitor. A normal maximize stays in the work area.
	IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		const int WmGetMinMaxInfo = 0x0024;
		if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
		var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
		var monitor = MonitorFromWindow(hwnd, 2);
		var screen = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(monitor, ref screen)) return IntPtr.Zero;
		var area = screen.rcMonitor;
		var work = _cover ? area : screen.rcWork;
		info.ptMaxPosition.X = work.Left - area.Left;
		info.ptMaxPosition.Y = work.Top - area.Top;
		info.ptMaxSize.X = work.Right - work.Left;
		info.ptMaxSize.Y = work.Bottom - work.Top;
		Marshal.StructureToPtr(info, lParam, true);
		handled = true;
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
		if (_allowClose)
		{
			_icon.Visible = false;
			_currentIcon?.Dispose();
			_icon.Dispose();
			return;
		}
		e.Cancel = true;
		Hide();
	}
}

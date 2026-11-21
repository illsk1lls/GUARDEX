using System.IO;
using System.Windows;
using System.Windows.Forms;
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
		Pages.MinimizeRequested += () => WindowState = WindowState.Minimized;
		Pages.MaximizeRequested += () =>
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
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

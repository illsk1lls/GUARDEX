using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Guardex;

sealed class Shell
{
	readonly SessionStore _store = new();
	readonly LocalJudge _judge;
	readonly Enforcer _enforcer;
	readonly HostWatch _watch;
	readonly PostureWatch _posture;
	readonly RemoteGuard _remote;
	readonly LaunchGuard _launch;
	readonly ClipGuard _clip;
	readonly NotifyIcon _icon;
	readonly FlyoutWindow _flyout;
	readonly MainWindow _window;
	readonly LockdownWindow _lock;
	bool _enabled;

	[DllImport("user32.dll")]
	static extern bool MessageBeep(uint type);

	[DllImport("user32.dll")]
	static extern IntPtr MonitorFromPoint(Point pt, uint flags);

	[DllImport("shcore.dll")]
	static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

	public Shell()
	{
		_judge = new LocalJudge(_store);
		_judge.AskAuth = AskSignIn;
		_enforcer = new Enforcer(_store);
		_watch = new HostWatch(_store, _judge, _enforcer);
		_posture = new PostureWatch(_store, _judge);
		_remote = new RemoteGuard(_store);
		_launch = new LaunchGuard(_store, _judge);
		_clip = new ClipGuard(_store, _enforcer);
		_store.Canary = Environment.GetEnvironmentVariable("GUARDEX_CANARY") ?? "";
		_flyout = new FlyoutWindow();
		_window = new MainWindow(_store);
		_lock = new LockdownWindow(() => _enforcer.HasOutboundHold);
		_lock.EndRequested += () =>
		{
			_enforcer.EndLockdown();
			_lock.HideDown();
		};
		_icon = new NotifyIcon
		{
			Visible = true,
			Text = "GUARDEX"
		};
		_icon.MouseClick += (_, e) =>
		{
			if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
				System.Windows.Application.Current.Dispatcher.Invoke(ToggleFlyout);
		};
		_flyout.ToggleRequested += () => SetEnabled(!_enabled);
		_flyout.OpenRequested += ShowWindow;
		_flyout.ExitRequested += Exit;
		_flyout.ConnectRequested += url => _ = Connect(url);
		_window.ConnectRequested += url => _ = Connect(url);
		_flyout.EndpointEdited += url => SyncEndpoint(url, true);
		_window.EndpointEdited += url => SyncEndpoint(url, false);
		_judge.Noted += OnNoted;
		_judge.Judged += src =>
		{
			var verdict = _store.CopyVerdict(src);
			if (verdict != null) _enforcer.OnVerdict(verdict, true);
		};
		_posture.Alerted += Balloon;
		_remote.Alerted += Balloon;
		_launch.Alerted += Balloon;
		_clip.Alerted += OnClip;
		_window.EnforceRequested += on => _enforcer.SetEnabled(on);
		_window.ReleaseRequested += src => _enforcer.Release(src);
		_window.AllowChanged += (id, on) => _remote.SetAllowed(id, on);
		_window.AllowNoneRequested += () => _remote.AllowNone();
		_window.StopUnlistedRequested += on => _remote.SetStopEnabled(on);
		_window.AskAnswered += yes => Record(yes);
		_flyout.AskAnswered += yes => Record(yes);
		_flyout.AckFinished += () =>
		{
			if (_flyout.IsVisible) _flyout.SetEnabled(_enabled, _store.Copy());
		};
		_window.TelemetryRequested += QueueTelemetry;
		_enforcer.Start();
		_window.SetTelemetry(Telemetry.Wanted(), Telemetry.Wanted() ? "Checking the telemetry block." : "Telemetry blocking is off.", Telemetry.Wanted());
		if (Telemetry.Wanted()) QueueTelemetry(true);
		_remote.Start();
		_launch.Start();
		_clip.Hook();
		ShowEndpoints();
		SetEnabled(true);
	}

	public void Run()
	{
		System.Windows.Application.Current.Exit += (_, _) =>
		{
			_watch.Stop();
			_posture.Stop();
			_remote.Stop();
			_launch.Stop();
			_clip.Stop();
			_enforcer.Shutdown();
			_lock.AllowClose();
			_icon.Visible = false;
			_icon.Dispose();
		};
		System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
		{
			_icon.BalloonTipTitle = "GUARDEX";
			_icon.BalloonTipText = Telemetry.Elevated()
				? "GUARDEX is running"
				: "GUARDEX is running without administrator. Firewall rules, telemetry changes, and stopping a service are limited.";
			_icon.BalloonTipIcon = ToolTipIcon.Info;
			_icon.ShowBalloonTip(3000);
		}));
	}

	void ToggleFlyout()
	{
		if (_flyout.IsVisible)
		{
			_flyout.Hide();
			return;
		}
		_flyout.SetEnabled(_enabled, _store.Copy());
		_flyout.ShowEndpoint(_judge.BaseUrl, _judge.Ready);
		var cursor = Cursor.Position;
		var work = Screen.FromPoint(cursor).WorkingArea;
		var (sx, sy) = DpiScale(cursor);
		var maxH = Math.Max(220.0, (work.Height - 16) / sy);
		var maxW = Math.Max(280.0, (work.Width - 16) / sx);
		_flyout.MaxWidth = 800;
		_flyout.MaxHeight = 800;
		_flyout.Width = Math.Min(320, maxW);
		_flyout.Height = Math.Min(440, maxH);
		_flyout.MaxWidth = maxW;
		_flyout.MaxHeight = maxH;
		_flyout.Show();
		_flyout.UpdateLayout();
		var dipW = _flyout.ActualWidth > 1 ? _flyout.ActualWidth : _flyout.Width;
		var dipH = _flyout.ActualHeight > 1 ? _flyout.ActualHeight : _flyout.Height;
		var pxW = dipW * sx;
		var pxH = dipH * sy;
		var left = cursor.X - pxW + 24 * sx;
		var top = cursor.Y - pxH - 8 * sy;
		if (top < work.Top) top = cursor.Y + 8 * sy;
		if (left < work.Left + 4) left = work.Left + 4;
		if (left + pxW > work.Right - 4) left = Math.Max(work.Left + 4, work.Right - pxW - 4);
		if (top + pxH > work.Bottom - 4) top = Math.Max(work.Top + 4, work.Bottom - pxH - 4);
		if (top < work.Top) top = work.Top;
		var source = System.Windows.PresentationSource.FromVisual(_flyout);
		if (source?.CompositionTarget != null)
		{
			var dip = source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(left, top));
			_flyout.Left = dip.X;
			_flyout.Top = dip.Y;
		}
		else
		{
			_flyout.Left = left / sx;
			_flyout.Top = top / sy;
		}
		_flyout.Activate();
	}

	static (double X, double Y) DpiScale(Point px)
	{
		try
		{
			var monitor = MonitorFromPoint(px, 2);
			if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dx, out var dy) == 0 && dx >= 96 && dy >= 96)
				return (dx / 96.0, dy / 96.0);
		}
		catch { }
		return (1, 1);
	}

	void Record(bool yes)
	{
		var line = AskAck.Line(yes, _store.Copy().AskSubject);
		_window.ShowRecorded(line);
		_flyout.ShowRecorded(line);
		_judge.Answer(yes);
	}

	void ShowWindow()
	{
		_flyout.Hide();
		if (!_window.IsVisible) _window.Show();
		if (_window.WindowState == System.Windows.WindowState.Minimized)
			_window.WindowState = System.Windows.WindowState.Normal;
		_window.Activate();
		_window.ShowEndpoint(_judge.BaseUrl, _judge.Ready, _judge.ModelName, _judge.Status);
		_window.RefreshNow();
	}

	void ShowEndpoints()
	{
		_flyout.ShowEndpoint(_judge.BaseUrl, _judge.Ready);
		_window.ShowEndpoint(_judge.BaseUrl, _judge.Ready, _judge.ModelName, _judge.Status);
	}

	void SyncEndpoint(string url, bool fromFlyout)
	{
		_judge.SetEndpoint(url);
		if (fromFlyout)
			_window.ShowEndpoint(_judge.BaseUrl, _judge.Ready, _judge.ModelName, _judge.Status);
		else
			_flyout.ShowEndpoint(_judge.BaseUrl, _judge.Ready);
	}

	SignIn? AskSignIn(string endpoint)
	{
		SignIn? creds = null;
		void Show()
		{
			var dlg = new AuthWindow(endpoint);
			if (_window.IsVisible) dlg.Owner = _window;
			if (dlg.ShowDialog() == true) creds = dlg.Creds;
		}
		var app = System.Windows.Application.Current;
		if (app == null) return null;
		if (app.Dispatcher.CheckAccess()) Show();
		else app.Dispatcher.Invoke(Show);
		return creds;
	}

	async Task Connect(string url)
	{
		_flyout.SetBusy(true);
		_window.SetBusy(true);
		try
		{
			await _judge.ConnectAsync(url);
		}
		finally
		{
			var app = System.Windows.Application.Current;
			app?.Dispatcher.Invoke(() =>
			{
				_flyout.SetBusy(false);
				_window.SetBusy(false);
				ShowEndpoints();
			});
		}
	}

	void OnNoted(ModelNote note)
	{
		var app = System.Windows.Application.Current;
		if (app == null) return;
		app.Dispatcher.Invoke(() =>
		{
			ShowEndpoints();
			if (_flyout.IsVisible) _flyout.SetEnabled(_enabled, _store.Copy());
			if (!note.Alert || note.Text.Length == 0) return;
			MessageBeep(0x30);
			var text = note.Text;
			var subject = _store.Copy().AskSubject;
			if (subject.Length > 0 && text == _store.Copy().Ask)
				text = text + "\n" + subject.Replace("\n", " · ");
			_icon.BalloonTipTitle = "GUARDEX";
			_icon.BalloonTipText = text.Length <= 220 ? text : text[..220];
			_icon.BalloonTipIcon = ToolTipIcon.Warning;
			_icon.ShowBalloonTip(4000);
		});
	}

	void QueueTelemetry(bool on)
	{
		Task.Run(() =>
		{
			string status;
			try { status = Telemetry.Apply(on); }
			catch (Exception ex) { status = ex.Message; }
			System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _window.SetTelemetry(Telemetry.Wanted(), status));
		});
	}

	void SetEnabled(bool on)
	{
		_enabled = on;
		if (on)
		{
			_store.SetModel(_judge.ModelName + " at " + _judge.BaseUrl);
			_watch.Start();
			_posture.Start();
		}
		else
		{
			_watch.Stop();
			_posture.Stop();
		}
		_icon.Icon = TrayArt.Make(on);
		_icon.Text = on ? "GUARDEX is on" : "GUARDEX is off";
		if (_flyout.IsVisible) _flyout.SetEnabled(on, _store.Copy());
	}

	void Exit()
	{
		_flyout.Hide();
		_posture.Stop();
		_remote.Stop();
		_launch.Stop();
		_clip.Stop();
		_enforcer.Shutdown();
		_lock.AllowClose();
		_window.AllowClose();
		_window.Close();
		System.Windows.Application.Current.Shutdown();
	}

	void OnClip(string text)
	{
		Balloon(text);
		var app = System.Windows.Application.Current;
		if (app == null) return;
		app.Dispatcher.BeginInvoke(new Action(() =>
		{
			try
			{
				_flyout.Hide();
				_lock.ShowFront(text);
			}
			catch { }
		}));
	}

	void Balloon(string text)
	{
		if (text.Length == 0) return;
		var app = System.Windows.Application.Current;
		if (app == null) return;
		app.Dispatcher.BeginInvoke(new Action(() =>
		{
			MessageBeep(0x30);
			_icon.BalloonTipTitle = "GUARDEX";
			_icon.BalloonTipText = text.Length <= 220 ? text : text[..220];
			_icon.BalloonTipIcon = ToolTipIcon.Warning;
			_icon.ShowBalloonTip(6000);
		}));
	}
}

static class TrayArt
{
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool DestroyIcon(IntPtr handle);

	public static Icon Make(bool on)
	{
		using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		using (var g = Graphics.FromImage(bmp))
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(Color.Transparent);
			var fill = on ? Color.FromArgb(143, 181, 115) : Color.FromArgb(110, 118, 106);
			using var brush = new SolidBrush(fill);
			g.FillPolygon(brush, new[]
			{
				new Point(16, 2),
				new Point(28, 8),
				new Point(26, 18),
				new Point(16, 30),
				new Point(6, 18),
				new Point(4, 8)
			});
		}
		var handle = bmp.GetHicon();
		try
		{
			using var tmp = Icon.FromHandle(handle);
			return (Icon)tmp.Clone();
		}
		finally
		{
			DestroyIcon(handle);
		}
	}
}

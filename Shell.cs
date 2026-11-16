using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Guardex;

sealed class Shell
{
	readonly SessionStore _store = new();
	readonly LocalJudge _judge;
	readonly HostWatch _watch;
	readonly NotifyIcon _icon;
	readonly FlyoutWindow _flyout;
	readonly MainWindow _window;
	bool _enabled;

	public Shell()
	{
		_judge = new LocalJudge(_store);
		_watch = new HostWatch(_store, _judge);
		_store.Canary = Environment.GetEnvironmentVariable("GUARDEX_CANARY") ?? "";
		_flyout = new FlyoutWindow();
		_window = new MainWindow(_store);
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
		SetEnabled(true);
	}

	public void Run()
	{
		System.Windows.Application.Current.Exit += (_, _) =>
		{
			_watch.Stop();
			_icon.Visible = false;
			_icon.Dispose();
		};
	}

	void ToggleFlyout()
	{
		if (_flyout.IsVisible)
		{
			_flyout.Hide();
			return;
		}
		_flyout.SetEnabled(_enabled, _store.Copy());
		var area = System.Windows.SystemParameters.WorkArea;
		var cursor = Cursor.Position;
		_flyout.Left = Math.Clamp(cursor.X - _flyout.Width + 24, area.Left + 8, area.Right - _flyout.Width - 8);
		_flyout.Top = cursor.Y - _flyout.Height - 8;
		if (_flyout.Top < area.Top) _flyout.Top = cursor.Y + 8;
		_flyout.Show();
		_flyout.Activate();
	}

	void ShowWindow()
	{
		_flyout.Hide();
		if (!_window.IsVisible) _window.Show();
		if (_window.WindowState == System.Windows.WindowState.Minimized)
			_window.WindowState = System.Windows.WindowState.Normal;
		_window.Activate();
		_window.RefreshNow();
	}

	void SetEnabled(bool on)
	{
		_enabled = on;
		if (on)
		{
			_store.SetModel(_judge.ModelName + " at " + _judge.BaseUrl);
			_watch.Start();
		}
		else
		{
			_watch.Stop();
		}
		_icon.Icon = TrayArt.Make(on);
		_icon.Text = on ? "GUARDEX is on" : "GUARDEX is off";
		if (_flyout.IsVisible) _flyout.SetEnabled(on, _store.Copy());
	}

	void Exit()
	{
		_flyout.Hide();
		_window.AllowClose();
		_window.Close();
		System.Windows.Application.Current.Shutdown();
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

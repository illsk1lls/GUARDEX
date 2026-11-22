using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Guardex;

public partial class LockdownWindow : Window
{
	readonly Func<bool> _held;
	readonly DispatcherTimer _timer;
	bool _allow;
	bool _front;

	public event Action? EndRequested;

	public LockdownWindow(Func<bool> held)
	{
		_held = held;
		InitializeComponent();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_timer.Tick += (_, _) => HoldFront();
		Deactivated += (_, _) =>
		{
			if (!_front || !IsVisible) return;
			Dispatcher.BeginInvoke(new Action(HoldFront));
		};
	}

	public void ShowFront(string text)
	{
		var split = text.Split('\n', 2);
		Headline.Text = split[0];
		Detail.Text = split.Length > 1 ? split[1] : "";
		_front = true;
		if (!IsVisible) Show();
		UpdateLayout();
		Place();
		Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
		HoldFront();
		if (!_timer.IsEnabled) _timer.Start();
	}

	public void HideDown()
	{
		_front = false;
		if (IsVisible) Hide();
	}

	public void AllowClose() => _allow = true;

	void OnEnd(object sender, RoutedEventArgs e) => EndRequested?.Invoke();

	void HoldFront()
	{
		if (!_front || !IsVisible) return;
		if (!_held())
		{
			HideDown();
			return;
		}
		if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
		if (!IsActive)
		{
			Topmost = false;
			Topmost = true;
			Activate();
		}
	}

	void Place()
	{
		var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
		var source = PresentationSource.FromVisual(this);
		if (source?.CompositionTarget == null)
		{
			Left = area.Left;
			Top = area.Top;
			return;
		}
		var from = source.CompositionTarget.TransformFromDevice;
		var origin = from.Transform(new System.Windows.Point(area.Left, area.Top));
		var far = from.Transform(new System.Windows.Point(area.Right, area.Bottom));
		var width = Math.Max(1, far.X - origin.X);
		var height = Math.Max(1, far.Y - origin.Y);
		Left = origin.X + Math.Max(0, (width - ActualWidth) / 2);
		Top = origin.Y + Math.Max(0, (height - ActualHeight) / 2);
	}

	protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
	{
		var shutting = System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == true;
		if (!_allow && !shutting)
		{
			e.Cancel = true;
			return;
		}
		_timer.Stop();
		base.OnClosing(e);
	}
}

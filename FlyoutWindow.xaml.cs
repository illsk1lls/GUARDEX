using System.Windows;
using System.Windows.Threading;
using Media = System.Windows.Media;

namespace Guardex;

public partial class FlyoutWindow : Window
{
	readonly Media.Brush _green = Freeze(143, 181, 115);
	readonly Media.Brush _muted = Freeze(142, 160, 136);
	readonly Media.Brush _ask = Freeze(228, 236, 223);
	DispatcherTimer? _ackTimer;
	bool _acking;
	string _ackLine = "";

	public event Action? ToggleRequested;
	public event Action? OpenRequested;
	public event Action? ExitRequested;
	public event Action<string>? ConnectRequested;
	public event Action<string>? EndpointEdited;
	public event Action<bool>? AskAnswered;
	public event Action? AckFinished;

	public FlyoutWindow()
	{
		InitializeComponent();
		if (!Telemetry.Elevated()) AdminLine.Visibility = Visibility.Visible;
		Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
		{
			if (!IsActive && !_acking) Hide();
		}));
	}

	public void ShowRecorded(string line)
	{
		_acking = true;
		_ackLine = line;
		PaintAck();
		_ackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
		_ackTimer.Stop();
		_ackTimer.Tick -= OnAckTick;
		_ackTimer.Tick += OnAckTick;
		_ackTimer.Start();
	}

	public void SetEnabled(bool on, Dash dash)
	{
		StateLine.Text = on ? "On" : "Off";
		StateLine.Foreground = new Media.SolidColorBrush(on ? Media.Color.FromRgb(143, 181, 115) : Media.Color.FromRgb(142, 160, 136));
		Track.Background = new Media.SolidColorBrush(on ? Media.Color.FromRgb(63, 107, 69) : Media.Color.FromRgb(58, 64, 54));
		Knob.HorizontalAlignment = on ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
		if (_acking)
		{
			PaintAck();
			return;
		}
		YesButton.IsEnabled = true;
		NoButton.IsEnabled = true;
		AskText.Foreground = _ask;
		var asking = dash.Ask.Length > 0;
		AskRow.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
		AskButtons.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
		AskText.Text = dash.Ask;
		AskSubject.Text = dash.AskSubject;
		AskSubject.Visibility = dash.AskSubject.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
		if (dash.Banner.Length > 0)
		{
			Hint.Text = dash.Banner;
			Hint.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(240, 180, 180));
			return;
		}
		Hint.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(142, 160, 136));
		var n = dash.Sources.Count;
		var top = n == 0 ? "No clients in the last hour." : n + " source" + (n == 1 ? "" : "s") + " in the last hour.";
		var blocks = dash.BlockCount == 0 ? "" : " " + dash.BlockCount + " blocked.";
		var host = dash.FindingCount == 0 ? "" : " " + dash.FindingCount + " start items to review.";
		Hint.Text = on
			? "Watching inbound connections. " + top + blocks + host
			: "Paused. The model is not being called. " + top + blocks + host;
	}

	public void ShowEndpoint(string url, bool ready)
	{
		if (!Endpoint.IsKeyboardFocusWithin)
			Endpoint.Text = url;
		ReadyMark.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
	}

	public void SetBusy(bool busy)
	{
		ConnectButton.IsEnabled = !busy;
		ConnectButton.Content = busy ? "Connecting" : "Connect";
	}

	void OnToggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		e.Handled = true;
		ToggleRequested?.Invoke();
	}

	void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

	void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

	void OnAskYes(object sender, RoutedEventArgs e) => Answer(true);

	void OnAskNo(object sender, RoutedEventArgs e) => Answer(false);

	void Answer(bool yes)
	{
		if (_acking || YesButton.IsEnabled == false) return;
		YesButton.IsEnabled = false;
		NoButton.IsEnabled = false;
		AskAnswered?.Invoke(yes);
	}

	void PaintAck()
	{
		AskRow.Visibility = Visibility.Visible;
		AskButtons.Visibility = Visibility.Visible;
		AskText.Text = _ackLine;
		AskText.Foreground = _green;
		AskSubject.Visibility = Visibility.Collapsed;
		YesButton.IsEnabled = false;
		NoButton.IsEnabled = false;
		Hint.Text = "Recorded.";
		Hint.Foreground = _green;
	}

	void OnAckTick(object? sender, EventArgs e)
	{
		_ackTimer?.Stop();
		_acking = false;
		YesButton.IsEnabled = true;
		NoButton.IsEnabled = true;
		AskText.Foreground = _ask;
		Hint.Foreground = _muted;
		AckFinished?.Invoke();
		if (!IsActive) Hide();
	}

	static Media.SolidColorBrush Freeze(byte r, byte g, byte b)
	{
		var brush = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	void OnConnect(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke(Endpoint.Text);

	void OnEndpointLostFocus(object sender, RoutedEventArgs e) => EndpointEdited?.Invoke(Endpoint.Text);
}

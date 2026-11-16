using System.Windows;
using Media = System.Windows.Media;

namespace Guardex;

public partial class FlyoutWindow : Window
{
	public event Action? ToggleRequested;
	public event Action? OpenRequested;
	public event Action? ExitRequested;

	public FlyoutWindow()
	{
		InitializeComponent();
		Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
		{
			if (!IsActive) Hide();
		}));
	}

	public void SetEnabled(bool on, Dash dash)
	{
		StateLine.Text = on ? "On" : "Off";
		StateLine.Foreground = new Media.SolidColorBrush(on ? Media.Color.FromRgb(143, 181, 115) : Media.Color.FromRgb(142, 160, 136));
		Track.Background = new Media.SolidColorBrush(on ? Media.Color.FromRgb(63, 107, 69) : Media.Color.FromRgb(58, 64, 54));
		Knob.HorizontalAlignment = on ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
		var n = dash.Sources.Count;
		var top = n == 0 ? "No clients in the last hour." : n + " source" + (n == 1 ? "" : "s") + " in the last hour.";
		Hint.Text = on
			? "Watching inbound connections. " + top
			: "Paused. The model is not being called. " + top;
	}

	void OnToggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		e.Handled = true;
		ToggleRequested?.Invoke();
	}

	void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

	void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}

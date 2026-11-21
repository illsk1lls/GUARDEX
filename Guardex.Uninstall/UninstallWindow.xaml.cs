using System.Windows;

namespace Guardex.Uninstall;

public partial class UninstallWindow : Window
{
	bool _busy;

	public UninstallWindow()
	{
		InitializeComponent();
		var version = UninstallWork.InstalledVersion();
		Intro.Text = version.Length == 0
			? "Remove GUARDEX and GUARDEX Browser from this PC. The .NET runtime and WebView2 are left installed, because other programs use them."
			: "Remove GUARDEX " + version + " and GUARDEX Browser from this PC. The .NET runtime and WebView2 are left installed, because other programs use them.";
	}

	void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	void OnClose(object sender, RoutedEventArgs e)
	{
		if (_busy) return;
		Close();
	}

	async void OnRemove(object sender, RoutedEventArgs e)
	{
		if (_busy) return;
		_busy = true;
		var data = DataBox.IsChecked == true;
		var rules = RulesBox.IsChecked == true;
		Choices.Visibility = Visibility.Collapsed;
		CancelButton.Visibility = Visibility.Collapsed;
		Bar.Visibility = Visibility.Visible;
		Bar.Value = 0;
		RemoveButton.IsEnabled = false;
		Intro.Text = "Removing GUARDEX from this PC.";
		StepText.Text = "0%  Starting.";
		var progress = new Progress<(int Done, int Total, string Text)>(tick =>
		{
			var pct = UninstallWork.Percent(tick.Done, tick.Total);
			Bar.Value = pct;
			StepText.Text = pct + "%  " + tick.Text;
		});
		try
		{
			await Task.Run(() => UninstallWork.Remove(data, rules, progress));
			Bar.Value = 100;
			StepText.Text = "100%  GUARDEX is removed.";
			Intro.Text = "GUARDEX has been removed from this PC. The .NET runtime and WebView2 are still installed.";
			RemoveButton.Content = "Close";
			RemoveButton.IsEnabled = true;
			_busy = false;
			RemoveButton.Click -= OnRemove;
			RemoveButton.Click += (_, _) => Close();
		}
		catch (Exception ex)
		{
			StepText.Text = ex.Message;
			Intro.Text = "Removal stopped.";
			RemoveButton.IsEnabled = true;
			CancelButton.Visibility = Visibility.Visible;
			Choices.Visibility = Visibility.Visible;
			_busy = false;
		}
	}
}

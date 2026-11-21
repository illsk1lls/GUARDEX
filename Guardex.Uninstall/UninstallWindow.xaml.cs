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

	void OnRemove(object sender, RoutedEventArgs e)
	{
		if (_busy) return;
		_busy = true;
		RemoveButton.IsEnabled = false;
		DataBox.IsEnabled = false;
		RulesBox.IsEnabled = false;
		try
		{
			StepText.Text = "Removing GUARDEX.";
			UninstallWork.Remove(DataBox.IsChecked == true, RulesBox.IsChecked == true);
			StepText.Text = "GUARDEX is removed.";
			RemoveButton.Content = "Close";
			RemoveButton.IsEnabled = true;
			_busy = false;
			RemoveButton.Click -= OnRemove;
			RemoveButton.Click += (_, _) => Close();
		}
		catch (Exception ex)
		{
			StepText.Text = ex.Message;
			RemoveButton.IsEnabled = true;
			DataBox.IsEnabled = true;
			RulesBox.IsEnabled = true;
			_busy = false;
		}
	}
}

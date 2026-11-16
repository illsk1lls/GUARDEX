using System.Windows;
using System.Windows.Threading;

namespace Guardex;

public partial class MainWindow : Window
{
	readonly SessionStore _store;
	readonly DispatcherTimer _timer;
	bool _allowClose;
	string _selected = "";

	public MainWindow(SessionStore store)
	{
		_store = store;
		InitializeComponent();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_timer.Tick += (_, _) => RefreshNow();
		_timer.Start();
	}

	public void AllowClose() => _allowClose = true;

	public void RefreshNow()
	{
		var dash = _store.Copy();
		Summary.Text = dash.Summary;
		var listen = dash.ListenCount == 0 ? "none" : dash.ListenCount + "  " + dash.ListenText;
		Status.Text = "tcp " + dash.Tcp + "    http.sys " + dash.Http + "    model " + dash.Model + "    listening " + listen;
		Feed.Text = dash.Feed.Count == 0 ? "Waiting for traffic." : string.Join(Environment.NewLine, dash.Feed);

		var keep = _selected;
		Sources.ItemsSource = dash.Sources;
		if (keep.Length > 0)
		{
			foreach (SourceRow row in Sources.Items)
			{
				if (row.Src == keep)
				{
					Sources.SelectedItem = row;
					break;
				}
			}
		}
		if (Sources.SelectedItem is SourceRow selected)
			Detail.Text = selected.Detail;
		else if (dash.Sources.Count == 0)
			Detail.Text = "No inbound clients yet.";
	}

	void OnSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		if (Sources.SelectedItem is not SourceRow row) return;
		_selected = row.Src;
		Detail.Text = row.Detail;
	}

	void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_allowClose) return;
		e.Cancel = true;
		Hide();
	}
}

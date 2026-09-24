using GlassesRemote.Server.Alerts;

namespace GlassesRemote.Server.Ui;

/// <summary>Recent connection attempts and other probe signals, newest first.</summary>
internal sealed class AlertsForm : Form
{
    private readonly AlertLog _alerts;
    private readonly ListView _list;

    public AlertsForm(AlertLog alerts)
    {
        _alerts = alerts;

        Text = "Glasses remote – recent alerts";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 420);
        Font = new Font("Segoe UI", 9.5f);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _list.Columns.Add("Time", 150);
        _list.Columns.Add("Kind", 170);
        _list.Columns.Add("From", 130);
        _list.Columns.Add("Detail", 290);

        var refresh = new Button { Text = "Refresh", Dock = DockStyle.Bottom, Height = 34 };
        refresh.Click += (_, _) => Reload();

        Controls.Add(_list);
        Controls.Add(refresh);
        Reload();
    }

    public void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var alert in _alerts.Recent())
        {
            _list.Items.Add(new ListViewItem(
            [
                alert.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                alert.Kind.ToString(),
                alert.RemoteAddress ?? "?",
                alert.Detail,
            ]));
        }
        _list.EndUpdate();
    }
}

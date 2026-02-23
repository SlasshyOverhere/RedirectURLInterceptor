using System.Globalization;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class UpdateProgressForm : Form
{
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        Text = "Preparing update..."
    };

    private readonly Label _detailLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        Text = string.Empty
    };

    private readonly ProgressBar _progressBar = new()
    {
        Dock = DockStyle.Fill,
        Minimum = 0,
        Maximum = 100,
        Style = ProgressBarStyle.Marquee
    };

    public UpdateProgressForm()
    {
        Text = "Updating Slasshy Url Interceptor";
        Width = 460;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        TopMost = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_progressBar, 0, 1);
        layout.Controls.Add(_detailLabel, 0, 2);

        Controls.Add(layout);
    }

    public void SetStatus(string statusText, bool indeterminate = true, string? detail = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(statusText, indeterminate, detail)));
            return;
        }

        _statusLabel.Text = statusText;
        _detailLabel.Text = detail ?? string.Empty;

        if (indeterminate)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
        }
        else
        {
            if (_progressBar.Style != ProgressBarStyle.Continuous)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
            }
        }
    }

    public void Apply(UpdateDownloadProgress progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Apply(progress)));
            return;
        }

        _statusLabel.Text = progress.StatusText;

        if (progress.IsIndeterminate || progress.TotalBytes is null || progress.TotalBytes <= 0)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _detailLabel.Text = progress.DownloadedBytes > 0
                ? $"{FormatBytes(progress.DownloadedBytes)} downloaded"
                : string.Empty;
            return;
        }

        if (_progressBar.Style != ProgressBarStyle.Continuous)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
        }

        var total = Math.Max(1L, progress.TotalBytes.Value);
        var percent = (int)Math.Clamp(progress.DownloadedBytes * 100L / total, 0, 100);
        _progressBar.Value = percent;
        _detailLabel.Text = $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(total)} ({percent}%)";
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        const double mb = 1024 * kb;
        const double gb = 1024 * mb;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.00} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0.00} KB";
        }

        return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
    }
}

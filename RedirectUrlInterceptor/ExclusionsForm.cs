using System.Diagnostics;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class ExclusionsForm : Form
{
    private readonly CheckedListBox _runningAppsList = new()
    {
        CheckOnClick = true,
        Dock = DockStyle.Fill,
        IntegralHeight = false
    };

    private readonly TextBox _manualInput = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical
    };

    private readonly HashSet<string> _initialExcluded;

    public ExclusionsForm(IEnumerable<string> currentExclusions)
    {
        _initialExcluded = currentExclusions
            .Select(ProcessNameHelper.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Text = "Excluded Applications";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 540;
        Height = 600;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(10)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "Select apps you want to ignore when they open browser redirect URLs."
        };

        var refreshButton = new Button
        {
            Text = "Refresh Running Apps",
            AutoSize = true
        };
        refreshButton.Click += (_, _) => PopulateRunningApps();

        var manualLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "Manual entries (one app per line, with or without .exe):"
        };

        _manualInput.Text = string.Join(Environment.NewLine, _initialExcluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        var saveButton = new Button
        {
            Text = "Save",
            AutoSize = true
        };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true
        };
        cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        actionPanel.Controls.Add(saveButton);
        actionPanel.Controls.Add(cancelButton);

        root.Controls.Add(intro);
        root.Controls.Add(_runningAppsList);
        root.Controls.Add(refreshButton);
        root.Controls.Add(manualLabel);
        root.Controls.Add(_manualInput);
        root.Controls.Add(actionPanel);

        Controls.Add(root);
        PopulateRunningApps();
    }

    public IReadOnlyList<string> ExcludedProcesses { get; private set; } = [];

    private void PopulateRunningApps()
    {
        var running = GetRunningAppNames();

        _runningAppsList.BeginUpdate();
        try
        {
            _runningAppsList.Items.Clear();
            foreach (var app in running)
            {
                var index = _runningAppsList.Items.Add(app);
                if (_initialExcluded.Contains(app))
                {
                    _runningAppsList.SetItemChecked(index, true);
                }
            }
        }
        finally
        {
            _runningAppsList.EndUpdate();
        }
    }

    private void SaveAndClose()
    {
        var finalSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _runningAppsList.CheckedItems)
        {
            var normalized = ProcessNameHelper.Normalize(item?.ToString());
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                finalSet.Add(normalized);
            }
        }

        foreach (var manual in ParseManualEntries(_manualInput.Text))
        {
            finalSet.Add(manual);
        }

        ExcludedProcesses = finalSet
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DialogResult = DialogResult.OK;
        Close();
    }

    private static List<string> GetRunningAppNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                // Show desktop-visible applications to keep the list practical.
                if (process.MainWindowHandle == IntPtr.Zero && string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    continue;
                }

                var normalized = ProcessNameHelper.Normalize(process.ProcessName);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                result.Add(normalized);
            }
            catch
            {
                // Ignore inaccessible system processes.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ParseManualEntries(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ProcessNameHelper.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

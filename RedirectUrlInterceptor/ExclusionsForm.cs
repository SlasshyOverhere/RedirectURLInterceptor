using System.Diagnostics;
using System.Management;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class ExclusionsForm : Form
{
    private static readonly HashSet<string> InfrastructureProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle",
        "system",
        "registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "sihost",
        "winlogon",
        "dwm",
        "taskhostw",
        "runtimebroker",
        "searchhost",
        "searchapp",
        "startmenuexperiencehost",
        "shellexperiencehost",
        "applicationframehost",
        "audiodg",
        "spoolsv",
        "wudfhost",
        "dllhost",
        "conhost",
        "rundll32",
        "wmiadap",
        "wmiprvse"
    };

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
    private readonly HashSet<string> _currentRunningApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingManualInput;
    private bool _manualInputEdited;

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
            Text = "Select apps you want to ignore when they open browser redirect URLs. Helper/background processes are included."
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
        _manualInput.TextChanged += (_, _) =>
        {
            if (!_syncingManualInput)
            {
                _manualInputEdited = true;
            }
        };

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
        _currentRunningApps.Clear();
        _currentRunningApps.UnionWith(running);

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

        if (!_manualInputEdited)
        {
            SyncManualInputFromInitialExclusions();
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

    private void SyncManualInputFromInitialExclusions()
    {
        var manualOnly = _initialExcluded
            .Where(name => !_currentRunningApps.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        _syncingManualInput = true;
        try
        {
            _manualInput.Text = string.Join(Environment.NewLine, manualOnly);
        }
        finally
        {
            _syncingManualInput = false;
        }
    }

    private static List<string> GetRunningAppNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleProcessIds = new HashSet<int>();
        var sessionProcessIds = new HashSet<int>();
        var currentSessionId = TryGetCurrentSessionId();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                if (process.SessionId != currentSessionId)
                {
                    continue;
                }

                sessionProcessIds.Add(process.Id);
                if (process.MainWindowHandle != IntPtr.Zero || !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    visibleProcessIds.Add(process.Id);
                }

                var normalized = ProcessNameHelper.Normalize(process.ProcessName);
                if (IsCandidateProcessName(normalized))
                {
                    result.Add(normalized);
                }

                var exeName = TryGetExecutableName(process);
                if (IsCandidateProcessName(exeName))
                {
                    result.Add(exeName);
                }
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

        var snapshots = SnapshotProcesses();
        if (snapshots.Count > 0)
        {
            var candidatePids = new HashSet<int>(sessionProcessIds);

            foreach (var snapshot in snapshots.Values)
            {
                if (visibleProcessIds.Contains(snapshot.ProcessId))
                {
                    candidatePids.Add(snapshot.ProcessId);
                    if (snapshot.ParentProcessId > 0)
                    {
                        candidatePids.Add(snapshot.ParentProcessId);
                    }
                }

                if (visibleProcessIds.Contains(snapshot.ParentProcessId))
                {
                    candidatePids.Add(snapshot.ProcessId);
                    candidatePids.Add(snapshot.ParentProcessId);
                }
            }

            foreach (var pid in candidatePids)
            {
                if (!snapshots.TryGetValue(pid, out var snapshot))
                {
                    continue;
                }

                var normalized = ProcessNameHelper.Normalize(snapshot.Name);
                if (IsCandidateProcessName(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        return result.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int TryGetCurrentSessionId()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch
        {
            return 1;
        }
    }

    private static string TryGetExecutableName(Process process)
    {
        try
        {
            return ProcessNameHelper.Normalize(process.MainModule?.FileName);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<int, ProcessSnapshot> SnapshotProcesses()
    {
        var snapshot = new Dictionary<int, ProcessSnapshot>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
            using var results = searcher.Get();
            foreach (ManagementObject row in results)
            {
                var processId = ReadInt(row["ProcessId"]);
                if (processId <= 0)
                {
                    continue;
                }

                var parentProcessId = ReadInt(row["ParentProcessId"]);
                var name = row["Name"] as string ?? string.Empty;
                snapshot[processId] = new ProcessSnapshot(processId, parentProcessId, name);
            }
        }
        catch
        {
            // Fall back to Process.GetProcesses() data only.
        }

        return snapshot;
    }

    private static int ReadInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        return value switch
        {
            int direct => direct,
            uint direct => unchecked((int)direct),
            long direct => unchecked((int)direct),
            ulong direct => unchecked((int)direct),
            _ => Convert.ToInt32(value)
        };
    }

    private static bool IsCandidateProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return !InfrastructureProcessNames.Contains(name);
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

    private readonly record struct ProcessSnapshot(int ProcessId, int ParentProcessId, string Name);
}

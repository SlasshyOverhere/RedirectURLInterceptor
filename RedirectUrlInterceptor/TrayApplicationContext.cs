using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly FileLogger _logger;
    private readonly JsonlCaptureWriter _captureWriter;
    private readonly InterceptorEngine _engine;
    private readonly Icon _appIcon;

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleInterceptionItem;
    private readonly ToolStripMenuItem _toggleRedirectResolutionItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _toggleForwardToBrowserItem;
    private readonly ToolStripMenuItem _forwardBrowserItem;

    private AppSettings _settings;
    private readonly SynchronizationContext _uiContext;

    public TrayApplicationContext(FileLogger logger)
    {
        _logger = logger;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load(AppPaths.ConfigPath, _logger);

        _captureWriter = new JsonlCaptureWriter(AppPaths.LogsDirectory, _logger);
        _engine = new InterceptorEngine(_captureWriter, _logger);
        _engine.RecordCaptured += OnRecordCaptured;
        _engine.ApplySettings(_settings);

        if (_settings.InterceptionEnabled)
        {
            _engine.Start();
        }

        _statusItem = new ToolStripMenuItem
        {
            Enabled = false
        };

        _toggleInterceptionItem = new ToolStripMenuItem();
        _toggleInterceptionItem.Click += async (_, _) => await ToggleInterceptionAsync();

        _toggleRedirectResolutionItem = new ToolStripMenuItem("Resolve Redirect Chain")
        {
            Checked = _settings.ResolveRedirects,
            CheckOnClick = true
        };
        _toggleRedirectResolutionItem.Click += (_, _) => ToggleRedirectResolution();

        var exclusionsItem = new ToolStripMenuItem("Excluded Apps...");
        exclusionsItem.Click += (_, _) => OpenExclusionsDialog();

        _toggleForwardToBrowserItem = new ToolStripMenuItem("Open Intercepted Links In Browser")
        {
            Checked = _settings.ForwardInterceptedLinksToBrowser,
            CheckOnClick = true
        };
        _toggleForwardToBrowserItem.Click += (_, _) => ToggleForwardToBrowser();

        _forwardBrowserItem = new ToolStripMenuItem();
        _forwardBrowserItem.Click += (_, _) => SetForwardBrowser();

        var openDefaultAppsItem = new ToolStripMenuItem("Open Default Apps Settings");
        openDefaultAppsItem.Click += (_, _) => OpenDefaultAppsSettings();

        _startupItem = new ToolStripMenuItem("Launch At Startup")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupItem.Click += (_, _) => ToggleLaunchAtStartup();

        var openLogsItem = new ToolStripMenuItem("Open Logs Folder");
        openLogsItem.Click += (_, _) => OpenFolder(AppPaths.LogsDirectory);

        var openDataItem = new ToolStripMenuItem("Open App Data Folder");
        openDataItem.Click += (_, _) => OpenFolder(AppPaths.DataDirectory);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += async (_, _) => await ExitAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleInterceptionItem);
        menu.Items.Add(_toggleRedirectResolutionItem);
        menu.Items.Add(exclusionsItem);
        menu.Items.Add(_toggleForwardToBrowserItem);
        menu.Items.Add(_forwardBrowserItem);
        menu.Items.Add(openDefaultAppsItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openLogsItem);
        menu.Items.Add(openDataItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield;
        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = AppIdentity.DisplayName,
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => OpenFolder(AppPaths.LogsDirectory);
        UpdateMenuState();

        _trayIcon.ShowBalloonTip(
            2000,
            AppIdentity.DisplayName,
            _settings.InterceptionEnabled ? "Interception is running." : "Interception is currently off.",
            ToolTipIcon.Info);

        _logger.Info($"Tray app started. Logs: {_captureWriter.GetCurrentLogPath()}");
    }

    protected override void ExitThreadCore()
    {
        _engine.RecordCaptured -= OnRecordCaptured;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();

        _engine.Dispose();
        _captureWriter.Dispose();

        base.ExitThreadCore();
    }

    private void OnRecordCaptured(InterceptRecord record)
    {
        _uiContext.Post(
            _ =>
            {
                try
                {
                    var url = record.Url.Length > 90 ? record.Url[..90] + "..." : record.Url;
                    _trayIcon.ShowBalloonTip(
                        1600,
                        AppIdentity.DisplayName,
                        $"URL intercepted and copied:\n{url}",
                        ToolTipIcon.Info);
                }
                catch
                {
                }
            },
            null);
    }

    private async Task ToggleInterceptionAsync()
    {
        _toggleInterceptionItem.Enabled = false;

        try
        {
            if (_engine.IsRunning)
            {
                await _engine.StopAsync();
                _settings.InterceptionEnabled = false;
                _logger.Info("Interception turned off.");
            }
            else
            {
                _engine.Start();
                _settings.InterceptionEnabled = true;
                _logger.Info("Interception turned on.");
            }

            _settings.Save(AppPaths.ConfigPath, _logger);
            UpdateMenuState();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to toggle interception.", ex);
            _trayIcon.ShowBalloonTip(2500, AppIdentity.DisplayName, "Failed to change interception state.", ToolTipIcon.Error);
        }
        finally
        {
            _toggleInterceptionItem.Enabled = true;
        }
    }

    private void ToggleRedirectResolution()
    {
        try
        {
            _settings.ResolveRedirects = _toggleRedirectResolutionItem.Checked;
            _settings.Save(AppPaths.ConfigPath, _logger);
            _engine.ApplySettings(_settings);
            _logger.Info($"Redirect resolution set to {_settings.ResolveRedirects}.");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to toggle redirect resolution.", ex);
        }
    }

    private void OpenExclusionsDialog()
    {
        using var dialog = new ExclusionsForm(_settings.ExcludedParentProcesses);
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings.ExcludedParentProcesses = dialog.ExcludedProcesses.ToList();
        _settings.Normalize();
        _settings.Save(AppPaths.ConfigPath, _logger);
        _engine.ApplySettings(_settings);
        _logger.Info($"Updated excluded apps count: {_settings.ExcludedParentProcesses.Count}");
    }

    private void ToggleLaunchAtStartup()
    {
        try
        {
            var enable = _startupItem.Checked;
            StartupManager.SetEnabled(enable, Application.ExecutablePath);
            _startupItem.Checked = StartupManager.IsEnabled();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to toggle launch at startup.", ex);
            _startupItem.Checked = StartupManager.IsEnabled();
            _trayIcon.ShowBalloonTip(2500, AppIdentity.DisplayName, "Could not update startup setting.", ToolTipIcon.Error);
        }
    }

    private void SetForwardBrowser()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                Filter = "Executable (*.exe)|*.exe",
                Title = "Select Browser Executable"
            };

            if (!string.IsNullOrWhiteSpace(_settings.ForwardBrowserPath) && File.Exists(_settings.ForwardBrowserPath))
            {
                dialog.FileName = _settings.ForwardBrowserPath;
            }

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            _settings.ForwardBrowserPath = dialog.FileName;
            _settings.Save(AppPaths.ConfigPath, _logger);
            UpdateMenuState();
            _trayIcon.ShowBalloonTip(2000, AppIdentity.DisplayName, "Forward browser updated.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to set forward browser.", ex);
            _trayIcon.ShowBalloonTip(2500, AppIdentity.DisplayName, "Could not update forward browser.", ToolTipIcon.Error);
        }
    }

    private void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open default apps settings.", ex);
            _trayIcon.ShowBalloonTip(2500, AppIdentity.DisplayName, "Unable to open Default Apps settings.", ToolTipIcon.Error);
        }
    }

    private void ToggleForwardToBrowser()
    {
        try
        {
            _settings.ForwardInterceptedLinksToBrowser = _toggleForwardToBrowserItem.Checked;
            _settings.Save(AppPaths.ConfigPath, _logger);
            UpdateMenuState();
            _trayIcon.ShowBalloonTip(
                1800,
                AppIdentity.DisplayName,
                _settings.ForwardInterceptedLinksToBrowser ? "Forwarding to browser is ON." : "Forwarding to browser is OFF.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to toggle forwarding to browser.", ex);
            _trayIcon.ShowBalloonTip(2500, AppIdentity.DisplayName, "Could not update forward setting.", ToolTipIcon.Error);
        }
    }

    private async Task ExitAsync()
    {
        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Error while stopping engine during exit.", ex);
        }

        ExitThread();
    }

    private void UpdateMenuState()
    {
        var running = _engine.IsRunning;
        _statusItem.Text = running ? "Status: Interception ON" : "Status: Interception OFF";
        _toggleInterceptionItem.Text = running ? "Turn Interception Off" : "Turn Interception On";
        _toggleRedirectResolutionItem.Checked = _settings.ResolveRedirects;
        _toggleForwardToBrowserItem.Checked = _settings.ForwardInterceptedLinksToBrowser;

        if (string.IsNullOrWhiteSpace(_settings.ForwardBrowserPath))
        {
            _settings.ForwardBrowserPath = BrowserLocator.TryFindFirstInstalled();
            if (!string.IsNullOrWhiteSpace(_settings.ForwardBrowserPath))
            {
                _settings.Save(AppPaths.ConfigPath, _logger);
            }
        }

        var displayName = string.IsNullOrWhiteSpace(_settings.ForwardBrowserPath)
            ? "Not Set"
            : Path.GetFileName(_settings.ForwardBrowserPath);

        _forwardBrowserItem.Text = $"Set Forward Browser... (Current: {displayName})";
        _forwardBrowserItem.Enabled = _settings.ForwardInterceptedLinksToBrowser;
    }

    private void OpenFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to open folder: {folderPath}", ex);
            _trayIcon.ShowBalloonTip(2000, AppIdentity.DisplayName, "Unable to open folder.", ToolTipIcon.Error);
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace GitAutoTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _repositoriesMenu;
    private readonly string _appFolder;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly List<RepositoryRuntime> _repositories = [];

    private AppConfig _config = new();
    private bool _paused;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _appFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitAutoTray");
        Directory.CreateDirectory(_appFolder);

        _configPath = Path.Combine(_appFolder, "config.json");
        _logPath = Path.Combine(_appFolder, "GitAutoTray.log");

        _statusItem = new ToolStripMenuItem("Loading...") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause all watching", null, (_, _) => TogglePause());
        _repositoriesMenu = new ToolStripMenuItem("Repositories");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_repositoriesMenu);
        menu.Items.Add("Commit all now", null, async (_, _) => await CommitAllAsync("manual"));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open configuration", null, (_, _) => OpenConfiguration());
        menu.Items.Add("Reload configuration", null, (_, _) => ReloadConfiguration());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Git Auto Tray",
            ContextMenuStrip = menu,
            Visible = true
        };

        LoadConfig();
        ConfigureRepositories();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), JsonOptions())
                          ?? new AppConfig();
            }
            else
            {
                _config = AppConfig.CreateExample();
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            Log($"Config load failed: {ex}");
            _config = new AppConfig();
            ShowBalloon("Configuration error", "Could not read config.json. Check the log.", ToolTipIcon.Error);
        }
    }

    private void SaveConfig()
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private void ReloadConfiguration()
    {
        LoadConfig();
        ConfigureRepositories();
        ShowBalloon("Configuration reloaded", $"Configured {_repositories.Count} valid repository(s).", ToolTipIcon.Info);
    }

    private void ConfigureRepositories()
    {
        DisposeRepositoryRuntimes();
        _repositoriesMenu.DropDownItems.Clear();

        foreach (var repositoryConfig in _config.Repositories.Where(r => r.Enabled))
        {
            var repoPath = NormalizePath(repositoryConfig.RepositoryPath);
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                Log($"Skipped missing repository: {repositoryConfig.RepositoryPath}");
                AddInvalidRepositoryMenuItem(repositoryConfig, "Folder not found");
                continue;
            }

            if (!Directory.Exists(Path.Combine(repoPath, ".git")) &&
                !File.Exists(Path.Combine(repoPath, ".git")))
            {
                Log($"Skipped non-Git folder: {repoPath}");
                AddInvalidRepositoryMenuItem(repositoryConfig, "Not a Git repository");
                continue;
            }

            repositoryConfig.RepositoryPath = repoPath;
            var runtime = new RepositoryRuntime(repositoryConfig, DebounceElapsedAsync);
            runtime.Watcher = CreateWatcher(runtime);
            runtime.Watcher.EnableRaisingEvents = !_paused;
            _repositories.Add(runtime);
            AddRepositoryMenu(runtime);
            Log($"Watching repository: {runtime.DisplayName} ({repoPath})");
        }

        RefreshStatus();
    }

    private FileSystemWatcher CreateWatcher(RepositoryRuntime runtime)
    {
        var watcher = new FileSystemWatcher(runtime.Config.RepositoryPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size,
            InternalBufferSize = 32 * 1024
        };

        watcher.Changed += (_, e) => OnFileChanged(runtime, e);
        watcher.Created += (_, e) => OnFileChanged(runtime, e);
        watcher.Deleted += (_, e) => OnFileChanged(runtime, e);
        watcher.Renamed += (_, e) => OnFileChanged(runtime, e);
        watcher.Error += (_, e) =>
        {
            Log($"Watcher error for {runtime.DisplayName}: {e.GetException()}");
            ShowBalloon("Watcher error", $"{runtime.DisplayName}: some file events may have been missed.", ToolTipIcon.Warning);
        };

        return watcher;
    }

    private void OnFileChanged(RepositoryRuntime runtime, FileSystemEventArgs e)
    {
        if (_paused || runtime.Paused || ShouldIgnore(runtime.Config, e.FullPath))
            return;

        Log($"[{runtime.DisplayName}] Change detected: {e.ChangeType} {e.FullPath}");
        runtime.Timer.Change(TimeSpan.FromSeconds(runtime.Config.DebounceSeconds), Timeout.InfiniteTimeSpan);
    }

    private static bool ShouldIgnore(RepositoryConfig config, string fullPath)
    {
        string relative;
        try
        {
            relative = NormalizeRelativePath(Path.GetRelativePath(config.RepositoryPath, fullPath));
        }
        catch
        {
            return true;
        }

        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == "..")
            return true;

        foreach (var ignored in config.IgnoredDirectories)
        {
            var normalizedIgnored = NormalizeRelativePath(ignored).TrimEnd(Path.DirectorySeparatorChar);
            if (relative.Equals(normalizedIgnored, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(normalizedIgnored + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var fileName = Path.GetFileName(relative);
        if (config.IgnoredFileSuffixes.Any(suffix =>
                fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (config.TrackedFiles.Count == 0)
            return false;

        return !config.TrackedFiles
            .Select(NormalizeRelativePath)
            .Any(tracked => relative.Equals(tracked, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DebounceElapsedAsync(RepositoryRuntime runtime)
    {
        await CommitAndPushAsync(runtime, "automatic");
    }

    private async Task CommitAllAsync(string reason)
    {
        foreach (var runtime in _repositories)
            await CommitAndPushAsync(runtime, reason);
    }

    private async Task CommitAndPushAsync(RepositoryRuntime runtime, string reason)
    {
        if (_disposed || (_paused || runtime.Paused) && reason == "automatic")
            return;

        if (!await runtime.GitLock.WaitAsync(0))
        {
            Log($"[{runtime.DisplayName}] Git operation skipped because another operation is running.");
            return;
        }

        try
        {
            SetRepositoryStatus(runtime, "Checking changes...");

            var paths = runtime.Config.TrackedFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var addArgs = new List<string> { "add" };
            if (paths.Count == 0)
            {
                addArgs.Add("--all");
            }
            else
            {
                addArgs.Add("--");
                addArgs.AddRange(paths);
            }

            var add = await RunGitAsync(runtime.Config.RepositoryPath, addArgs);
            if (add.ExitCode != 0)
            {
                ReportFailure(runtime, "git add failed", add);
                return;
            }

            var diffArgs = new List<string> { "diff", "--cached", "--quiet" };
            if (paths.Count > 0)
            {
                diffArgs.Add("--");
                diffArgs.AddRange(paths);
            }

            var diff = await RunGitAsync(runtime.Config.RepositoryPath, diffArgs);
            if (diff.ExitCode == 0)
            {
                SetRepositoryStatus(runtime, "No changes");
                Log($"[{runtime.DisplayName}] Nothing to commit.");
                RestoreRepositoryStatusLater(runtime);
                return;
            }

            if (diff.ExitCode != 1)
            {
                ReportFailure(runtime, "Unable to inspect staged changes", diff);
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            var commitArgs = new List<string>
            {
                "commit", "-m", $"wgitwatch autoCommit {timestamp}"
            };
            if (paths.Count > 0)
            {
                commitArgs.Add("--");
                commitArgs.AddRange(paths);
            }

            var commit = await RunGitAsync(runtime.Config.RepositoryPath, commitArgs);
            if (commit.ExitCode != 0)
            {
                ReportFailure(runtime, "git commit failed", commit);
                return;
            }

            if (!runtime.Config.PushEnabled)
            {
                Log($"[{runtime.DisplayName}] Local commit completed; push disabled ({reason}).");
                SetRepositoryStatus(runtime, "Committed locally");
                ShowBalloon("Git Auto Tray", $"{runtime.DisplayName}: changes committed locally.", ToolTipIcon.Info);
                RestoreRepositoryStatusLater(runtime);
                return;
            }

            var push = await RunGitAsync(runtime.Config.RepositoryPath, ["push"]);
            if (push.ExitCode != 0)
            {
                ReportFailure(runtime, "Commit created, but push failed", push);
                return;
            }

            Log($"[{runtime.DisplayName}] Commit and push completed ({reason}).");
            SetRepositoryStatus(runtime, "Pushed successfully");
            ShowBalloon("Git Auto Tray", $"{runtime.DisplayName}: changes committed and pushed.", ToolTipIcon.Info);
            RestoreRepositoryStatusLater(runtime);
        }
        catch (Exception ex)
        {
            Log($"[{runtime.DisplayName}] Unexpected error: {ex}");
            SetRepositoryStatus(runtime, "Unexpected error");
            ShowBalloon("Git Auto Tray error", $"{runtime.DisplayName}: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            runtime.GitLock.Release();
        }
    }

    private static async Task<ProcessResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(-1, await stdoutTask, "Git operation timed out after two minutes.");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private void ReportFailure(RepositoryRuntime runtime, string title, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

        details = details.Trim();
        Log($"[{runtime.DisplayName}] {title}. Exit code {result.ExitCode}. {details}");
        SetRepositoryStatus(runtime, title);
        ShowBalloon($"{runtime.DisplayName}: {title}", Truncate(details, 220), ToolTipIcon.Error);
    }

    private void AddRepositoryMenu(RepositoryRuntime runtime)
    {
        var root = new ToolStripMenuItem(runtime.DisplayName);
        runtime.StatusMenuItem = new ToolStripMenuItem("Watching") { Enabled = false };
        runtime.PauseMenuItem = new ToolStripMenuItem("Pause", null, (_, _) => ToggleRepositoryPause(runtime));
        root.DropDownItems.Add(runtime.StatusMenuItem);
        root.DropDownItems.Add(new ToolStripSeparator());
        var commitActionText = runtime.Config.PushEnabled ? "Commit and push now" : "Commit locally now";
        root.DropDownItems.Add(commitActionText, null, async (_, _) => await CommitAndPushAsync(runtime, "manual"));
        root.DropDownItems.Add(runtime.PauseMenuItem);
        root.DropDownItems.Add("Open repository", null, (_, _) => OpenRepository(runtime.Config.RepositoryPath));
        _repositoriesMenu.DropDownItems.Add(root);
    }

    private void AddInvalidRepositoryMenuItem(RepositoryConfig config, string reason)
    {
        var name = string.IsNullOrWhiteSpace(config.Name)
            ? Path.GetFileName(config.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : config.Name;
        var item = new ToolStripMenuItem($"{name} - {reason}") { Enabled = false };
        _repositoriesMenu.DropDownItems.Add(item);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        foreach (var runtime in _repositories)
            runtime.Watcher.EnableRaisingEvents = !_paused && !runtime.Paused;

        _pauseItem.Text = _paused ? "Resume all watching" : "Pause all watching";
        RefreshStatus();
    }

    private void ToggleRepositoryPause(RepositoryRuntime runtime)
    {
        runtime.Paused = !runtime.Paused;
        runtime.Watcher.EnableRaisingEvents = !_paused && !runtime.Paused;
        runtime.PauseMenuItem!.Text = runtime.Paused ? "Resume" : "Pause";
        SetRepositoryStatus(runtime, runtime.Paused ? "Paused" : "Watching");
        RefreshStatus();
    }

    private void SetRepositoryStatus(RepositoryRuntime runtime, string text)
    {
        void Set()
        {
            if (runtime.StatusMenuItem is not null)
                runtime.StatusMenuItem.Text = text;
            RefreshStatus();
        }

        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.BeginInvoke(Set);
        else
            Set();
    }

    private async void RestoreRepositoryStatusLater(RepositoryRuntime runtime)
    {
        await Task.Delay(3000);
        if (!_disposed)
            SetRepositoryStatus(runtime, runtime.Paused ? "Paused" : "Watching");
    }

    private void RefreshStatus()
    {
        var active = _repositories.Count(r => !r.Paused);
        var text = _paused
            ? $"Paused - {_repositories.Count} configured"
            : _repositories.Count == 0
                ? "No valid repositories configured"
                : $"Watching {active} of {_repositories.Count} repositories";
        _statusItem.Text = text;
        _repositoriesMenu.Enabled = _repositoriesMenu.DropDownItems.Count > 0;
    }

    private void OpenConfiguration()
    {
        if (!File.Exists(_configPath))
            SaveConfig();
        Process.Start(new ProcessStartInfo("notepad.exe", _configPath) { UseShellExecute = true });
    }

    private static void OpenRepository(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void OpenLog()
    {
        if (!File.Exists(_logPath))
            File.WriteAllText(_logPath, "Git Auto Tray log\r\n");
        Process.Start(new ProcessStartInfo("notepad.exe", _logPath) { UseShellExecute = true });
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (_disposed)
            return;

        void Show()
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = text;
            _trayIcon.BalloonTipIcon = icon;
            _trayIcon.ShowBalloonTip(4000);
        }

        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.BeginInvoke(Show);
        else
            Show();
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the tray app.
        }
    }

    private void DisposeRepositoryRuntimes()
    {
        foreach (var runtime in _repositories)
            runtime.Dispose();
        _repositories.Clear();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    protected override void ExitThreadCore()
    {
        _disposed = true;
        DisposeRepositoryRuntimes();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class RepositoryRuntime : IDisposable
{
    private readonly Func<RepositoryRuntime, Task> _onDebounce;

    public RepositoryRuntime(RepositoryConfig config, Func<RepositoryRuntime, Task> onDebounce)
    {
        Config = config;
        _onDebounce = onDebounce;
        Timer = new System.Threading.Timer(async _ => await _onDebounce(this));
    }

    public RepositoryConfig Config { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Config.Name)
        ? Path.GetFileName(Config.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : Config.Name;
    public FileSystemWatcher Watcher { get; set; } = null!;
    public System.Threading.Timer Timer { get; }
    public SemaphoreSlim GitLock { get; } = new(1, 1);
    public bool Paused { get; set; }
    public ToolStripMenuItem? StatusMenuItem { get; set; }
    public ToolStripMenuItem? PauseMenuItem { get; set; }

    public void Dispose()
    {
        Timer.Dispose();
        Watcher?.Dispose();
        GitLock.Dispose();
    }
}

internal sealed class AppConfig
{
    public List<RepositoryConfig> Repositories { get; set; } = [];

    public static AppConfig CreateExample() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "Example repository",
                RepositoryPath = @"C:\path\to\your\git\project",
                PushEnabled = true,
                TrackedFiles = []
            }
        ]
    };
}

internal sealed class RepositoryConfig
{
    public string Name { get; set; } = "";
    public string RepositoryPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool PushEnabled { get; set; } = true;
    public int DebounceSeconds { get; set; } = 20;

    // Empty means watch and commit all non-ignored changes in the repository.
    public List<string> TrackedFiles { get; set; } = [];

    public List<string> IgnoredDirectories { get; set; } =
    [
        ".git", "bin", "obj", "node_modules", "dist", "build", "target", ".idea", ".vs"
    ];

    public List<string> IgnoredFileSuffixes { get; set; } =
    [
        ".tmp", ".temp", ".swp", ".log", "~"
    ];
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

## About GitAutoTray

GitAutoTray is a lightweight Windows system tray application that automatically monitors one or more local Git repositories for file changes. When a configured file is modified, the app waits for a short debounce period, stages the relevant changes, creates a timestamped commit, and pushes it to the repository’s configured remote.

The application is designed for files that change frequently and need to be backed up to Git without requiring manual commits. Each repository is monitored independently, with its own watched-file list, debounce delay, pause state, and Git operation lock. This allows multiple repositories to be active at the same time without changes in one repository interfering with another.

GitAutoTray uses Windows `FileSystemWatcher`, so it does not repeatedly scan repository folders in the background. Instead, Windows notifies the application when a file changes. The app then waits until file activity has stopped before running Git, helping group several rapid saves into a single commit.

Note: created using ChatGPT

### Features

* Monitor multiple Git repositories simultaneously
* Watch selected files or an entire repository
* Independent debounce timing for each repository
* Automatic `git add`, `git commit`, and `git push`
* Skip commits when there are no staged changes
* Prevent overlapping Git operations within the same repository
* Pause or resume individual repository watchers
* Manually trigger a commit and push from the tray menu
* Open repository folders, configuration, and logs from the tray
* Display Windows notifications for successful commits and errors
* Store configuration outside the application directory
* Support portable and self-contained Windows builds

### Typical Workflow

1. GitAutoTray starts when the user signs in.
2. Windows reports a change in a configured repository.
3. The app checks whether the changed file is being monitored.
4. The repository’s debounce timer starts or resets.
5. After file activity stops, the app stages the configured files.
6. If staged changes exist, the app creates a timestamped commit.
7. The commit is pushed to the repository’s configured upstream branch.
8. The result is written to the log and shown through a tray notification.

GitAutoTray is intended to supplement normal Git usage rather than replace it. Each repository should already have a valid remote, upstream branch, `.gitignore`, and non-interactive Git authentication configured before automatic pushing is enabled.


## Requirements

- Windows 10/11
- Git available as `git.exe` in PATH
- .NET 8 SDK for building
- Git credentials configured so `git push` works without an interactive prompt

## Run

```powershell
dotnet run
```

On first run, the app creates:

```text
%LOCALAPPDATA%\GitAutoTray\config.json
```

Right-click the tray icon and choose **Open configuration**. Edit the file, save it, then select **Reload configuration**.

## Multiple-repository configuration

```json
{
  "Repositories": [
    {
      "Name": "Repo One",
      "RepositoryPath": "D:\\Projects\\RepoOne",
      "Enabled": true,
      "DebounceSeconds": 20,
      "TrackedFiles": [
        "data\\results.json"
      ],
      "IgnoredDirectories": [
        ".git", "bin", "obj", "node_modules", "dist", "build", "target", ".idea", ".vs"
      ],
      "IgnoredFileSuffixes": [
        ".tmp", ".temp", ".swp", ".log", "~"
      ]
    },
    {
      "Name": "Repo Two",
      "RepositoryPath": "C:\\Work\\RepoTwo",
      "Enabled": true,
      "DebounceSeconds": 30,
      "TrackedFiles": []
    }
  ]
}
```

`TrackedFiles` contains paths relative to the repository root.

- An empty `TrackedFiles` list watches and commits the entire repository, subject to ignores.
- A non-empty list watches, stages, and commits only those files.
- Each repository can have a different debounce delay.
- Set `Enabled` to `false` to keep a repository in the config without watching it.

## Tray menu

Each configured repository gets its own submenu with:

- Status
- Commit and push now
- Pause/Resume
- Open repository

There are also global controls to commit all repositories, pause all watching, open/reload configuration, and open the log.

## Publish a standalone EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

## Testing

### 1. Test Git manually first

In each repository:

```powershell
git status
git push
```

Make sure `git push` finishes without asking for credentials.

### 2. Use two temporary test repositories

Create two local repositories and two bare remotes:

```powershell
mkdir C:\GitTrayTest
cd C:\GitTrayTest

git init --bare RemoteOne.git
git init --bare RemoteTwo.git

git clone RemoteOne.git RepoOne
git clone RemoteTwo.git RepoTwo

cd RepoOne
"initial" | Set-Content watched.txt
git add watched.txt
git commit -m "Initial"
git push -u origin main

cd ..\RepoTwo
"initial" | Set-Content watched.txt
git add watched.txt
git commit -m "Initial"
git push -u origin main
```

If your Git uses `master` instead of `main`, push the current branch shown by:

```powershell
git branch --show-current
```

Configure both repositories in `config.json` with `TrackedFiles` set to `["watched.txt"]` and a short `DebounceSeconds` value such as `5`.

### 3. Trigger each watcher

```powershell
"change one" | Add-Content C:\GitTrayTest\RepoOne\watched.txt
"change two" | Add-Content C:\GitTrayTest\RepoTwo\watched.txt
```

After the debounce delay, verify:

```powershell
git -C C:\GitTrayTest\RepoOne log -1 --oneline
git -C C:\GitTrayTest\RepoTwo log -1 --oneline
```

### 4. Verify file filtering

Create or edit a file not listed in `TrackedFiles`. No commit should be created. Then edit `watched.txt`; a commit should be created.

### 5. Verify no-change behavior

Use **Commit and push now** without modifying anything. The repository status should briefly show `No changes`, and no empty commit should be created.

### 6. Verify independent operation

Pause Repo One from its submenu. Modify both repositories. Repo Two should commit; Repo One should not until resumed and changed again.

### 7. Inspect failures

Open the tray menu and select **Open log**. Logs are stored at:

```text
%LOCALAPPDATA%\GitAutoTray\GitAutoTray.log
```

## Important behavior

- `git add --all` is used only when `TrackedFiles` is empty.
- When `TrackedFiles` is populated, only those paths are staged and committed.
- A failed push leaves the commit safely in the local repository.
- The app does not automatically pull, merge, rebase, or resolve conflicts.
- `.gitignore` does not stop changes to files that Git already tracks.

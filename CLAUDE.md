# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**git.kit** is a Windows WPF desktop app (.NET 10, MVVM) that replicates commits between
branches of a git repository, via **cherry-pick** or **diff integration**. When git cannot
merge automatically, the user resolves conflicts in **TortoiseGitMerge**, then finishes the
replication and optionally pushes.

The app opens on a **home screen** (`ShellViewModel` hosts a 6-tab shell: Início · Replicar
branch · Cherry-pick · User Stories · Processos · Log) offering these flows:
- **Replicar branch** — replicates *all* commits of a source branch onto a new branch based
  on a **user-chosen destination** (the PR target, e.g. `develop`/`master`); the new branch
  name is suffixed by the destination and the PR title is pre-filled. Then it opens a
  **Pull Request** via `gh` with the chosen reviewers.
- **Cherry-pick** — replicates the *selected* commits onto a chosen target branch.
- **User Stories** — loads Azure DevOps user stories (ones where the dev has child Tasks,
  plus unassigned ones) via **REST + PAT** (`IAzureDevOpsService`/`AzureDevOpsService`;
  settings kept in the embedded DB). "Atribuir a mim" assigns the US and creates an
  **Active child Task** for the dev. The dev writes a **technical plan** (persisted per US)
  and hits Executar (repo + branch fields inline): a background **agent job** clones,
  creates the branch and runs the **Claude CLI** (`IAgentRunner`/`ClaudeAgentRunner`,
  turn-based `claude -p` / `--continue`, prompt via **stdin**, default
  `--permission-mode acceptEdits`). When a turn ends the job goes to
  `JobStatus.WaitingForInput` and the Shell notifies (sound + status bar); the dev opens
  the process popup (`AgentWindow`, a console shell over the CLI) to chat. "Solicitar
  commit" asks the agent for a one-line intent, builds **`Ab#{usId} {intent}`** for
  approval; on approve the app runs `CommitAllAsync` and enables push (no PR).
  Persistence lives in an **embedded SQLite DB** (`%LOCALAPPDATA%\git.kit\gitkit.db`,
  `GitKit.Core/Data/AppDatabase.cs`): DevOps/agent settings (PAT stored in plain text,
  local machine scope), technical plans, and agent session transcripts.

Both flows accept a **GitHub URL or a local repository path** in the repository field
(`RepositorySourceResolver`): for a URL, branches/commits/collaborators are listed via `gh`
(`IGitHubService`/`GitHubService`) **without cloning**; for a local path they're read
straight from the repo via git, and the real remote (for push/PR) is taken from the repo's
`origin`. Only when the user hits Replicar does a **background job** (`BackgroundJobService`)
clone (from the URL/cache, or the local path) and run the replication — re-pointing `origin`
at the real remote so push/PR go there. Jobs appear
in the **Processos** tab; clicking one recovers it to the main screen, and on conflict the
job pauses (`JobStatus.NeedsConflictResolution`) and resumes after the user resolves it in
the existing conflicts window. `gh` must be installed and authenticated (`gh auth login`).

The codebase (comments, XML docs, UI strings, user manual) is written in **Portuguese** —
match that language when editing existing code and user-facing text.

The WPF UI follows an Apple-inspired design system (spec in `design.md`) implemented as a
merged `ResourceDictionary` at `src/GitKit.App/Styles/DesignSystem.xaml` (color/typography
tokens, pill buttons, hairline "card" GroupBoxes, templated ComboBox/TextBox/TabItem/DataGrid).
Style controls by referencing that dictionary's keys — never hard-code hex colors in views.

## Commands

```powershell
dotnet build GitKit.slnx                                          # build everything
dotnet test tests/GitKit.Core.Tests/GitKit.Core.Tests.csproj     # run all tests
dotnet run --project src/GitKit.App/GitKit.App.csproj            # run the WPF app
```

Run a single test by name:

```powershell
dotnet test tests/GitKit.Core.Tests/GitKit.Core.Tests.csproj --filter "FullyQualifiedName~<TestMethodName>"
```

The app has a hidden mode used to regenerate manual screenshots: `--screenshots <dir>`
(see `src/GitKit.App/Screenshots/ScreenshotGenerator.cs`, invoked from `App.xaml.cs`).

## Prerequisites for running / testing

- **.NET SDK 10** (WPF app targets `net10.0-windows`, so the app builds only on Windows).
- **git** on `PATH` — every git operation shells out to the CLI. The test suite creates
  and deletes real temporary git repositories, so tests need git installed.
- **TortoiseGit** (optional) — only needed for manual conflict resolution at runtime.

## Architecture

Two projects plus tests, wired by manual DI (no container):

- **`GitKit.Core`** (`net10.0`, no UI dependency) — all domain logic, fully testable
  without WPF.
- **`GitKit.App`** (`net10.0-windows`, WPF, `AssemblyName = GitKit`) — MVVM UI.
- **`GitKit.Core.Tests`** (xunit) — integration tests against temporary git repos.

Dependency composition happens in `src/GitKit.App/App.xaml.cs::OnStartup` ("poor man's
DI"): it constructs `ProcessRunner → GitService` **and `GitHubService`**, attaches a
`GitCommandLogger` to both (`Attach(IGitCommandSource)` — git and gh share the
`CommandExecuted` event), creates a `WorkspaceService` and kicks off background cleanup, then
builds `TortoiseGitLauncher → DialogService → ConflictResolutionCoordinator →
BackgroundJobService → ShellViewModel` and shows `MainWindow`. There is no DI container; add
new dependencies by threading them through this method.

`ShellViewModel` is the window DataContext and owns the child VMs (`HomeViewModel`,
`BranchReplicationViewModel`, `CherryPickViewModel`, `ProcessesViewModel`), the unified
git/gh log, and tab navigation. `BackgroundJobService` runs each replication as a
`JobViewModel` (in-memory only, one `CancellationTokenSource` per job) and drives the
clone/replicate/push/PR pipeline plus conflict pause/resume. Branch-replication ranges use
`GitService.ReplicateBranchAsync` (sequential cherry-pick, resumable via a `startIndex`);
cherry-pick loops `ReplicateCommitAsync` per selected commit.

`WorkspaceService` owns the temp working copies: short-path root `C:\gtk\<n>` (fallback
`%TEMP%\gtk`), integer-named folders. At startup, `SnapshotExistingFolders()` is called
**before** any new folder is created, and `CleanupAsync` deletes that snapshot in the
background — so copies made during the current session are never removed. `GitCommandLogger`
persists every git command to `%LOCALAPPDATA%\git.kit\logs\git-<timestamp>.log` (separate
from work folders, so cleanup never touches logs).

`RepositoryCache` (Core, `IRepositoryCache`) speeds up URL clones: it keeps a
`git clone --mirror` per remote under `%LOCALAPPDATA%\git.kit\cache`, tracked in
`cache-index.json`. `EnsureCacheAsync(url)` creates the mirror on first use or
`fetch --all --prune`s it thereafter, returning the mirror path (or `null` on any
failure — except cancellation, which is rethrown so the caller doesn't fall back to a
direct clone the user just aborted). `BackgroundJobService.CloneWorkingCopyAsync` then
clones the working copy **from the mirror** and
re-points `origin` to the real URL (so push and `gh pr create` target the real remote); on
`null` it falls back to a direct clone. Cache and logs live outside the `C:\gtk` cleanup scope.

### git integration is 100% CLI

There is **no libgit2 / LibGit2Sharp**. All git access flows through
`IProcessRunner`/`ProcessRunner`, which runs the `git` executable and captures
stdout/stderr. `GitService` (implements `IGitService`) is the only place that builds git
command strings; it raises `CommandExecuted` after every invocation so the UI can log
each command (the UI log and the file log are **off by default**, toggled by the checkbox
on the Log tab → `ShellViewModel.IsLogEnabled` / `GitCommandLogger.Enabled`). `GitHubService`
is the analogous single place that builds `gh` command strings and raises the same event.

`ProcessRunner` splits output on `\n`, `\r`, or `\r\n` and delivers each line through an
optional `onOutputLine` callback as it arrives — this is how `git clone --progress`
percentages reach the status bar (`IProgress<string>` parameters on clone/cache methods).
Blank lines are preserved in the captured output (commit-message reconstruction depends on
the subject/body separator). Cancelling the token **kills the git process tree** (no
orphaned processes) and throws `OperationCanceledException`; each `JobViewModel`
owns one `CancellationTokenSource`, wired to the Cancelar button in the
status bar.

Critical detail: `ProcessRunner` forces `LC_ALL=C.UTF-8`, `GIT_TERMINAL_PROMPT=0`, and
`GIT_EDITOR=true` on the child process. This is load-bearing — `GitService` **parses git's
English text output** (e.g. detecting empty cherry-picks via "is now empty" / "nothing to
commit"), and `GIT_EDITOR=true` keeps `cherry-pick --continue` from blocking on an editor.
Don't remove these or add parsing that depends on localized output.

`GitService` injects the git executable path via its constructor
(`GitService(IProcessRunner, string gitExecutable = "git")`) — tests can point this
elsewhere. Structured output is parsed using a `\x1f` (unit separator) field delimiter in
`for-each-ref` / `log --format` strings. Branch local-vs-remote is decided
**deterministically** from the full `%(refname)` (`refs/heads/` vs `refs/remotes/`), not by
guessing from the short name. Commit messages are written to a temp file and passed via
`git commit -F` to avoid command-line escaping issues.

### Replication flow (the core behavior)

`GitService.ReplicateCommitAsync` orchestrates: (1) verify a clean working tree,
(2) `PrepareDestinationAsync` checks out the destination branch — creating it if it
doesn't exist, based on the **name suffix** (ends with `dev` → branch from `develop`,
otherwise from `master` with fallback to `main`; bases are searched among both local and
`origin/*` refs), then (3) runs the chosen strategy. Results are modeled as
`ReplicationResult` with distinct outcomes: **Ok**, **AlreadyApplied** (empty/no-op —
not an error), **Conflicts**, **Failure**.

When a strategy hits conflicts, `ReplicateCommitAsync` returns `Conflicts` and the UI
opens the conflict window. `ContinueReplicationAsync` finishes the job: it first checks
each unmerged file for leftover conflict markers (`<<<<<<<` / `>>>>>>>`) **before**
staging, because `git add` would erase the unmerged state; then `add -A` and
`cherry-pick --continue` (or a fresh commit for diff-integration). A resolution identical
to the destination yields `AlreadyApplied`, not a failure.

### Working always on a temporary copy

The app never mutates the user's original repository. A URL is cloned to a unique temp
folder and the copy's `origin` is re-pointed at the real remote URL so `push` targets the
true upstream. This clone/re-point logic lives in `BackgroundJobService.CloneWorkingCopyAsync`
(see `SetRemoteUrlAsync` usage).

### Conflict resolution & TortoiseGit

`ConflictResolutionCoordinator` (App layer) drives conflict UX over
`ITortoiseGitLauncher`/`TortoiseGitLauncher` (Core). Preferred path is TortoiseGitProc's
`conflicteditor` command, which extracts index stages itself and **preserves the file's
original encoding/line endings**. The manual fallback (extracting stages 1/2/3 =
base/ours/theirs via `IGitService.ExtractConflictStageAsync` and launching
`TortoiseGitMerge` directly) also preserves the blob bytes exactly — extraction uses
`git checkout-index --stage=<n> --temp` (writes straight to a file), never stdout capture,
which would corrupt binaries/UTF-16 and normalize line endings.
TortoiseGit executables are auto-located under Program Files or `PATH`; if not found, the
user is prompted to pick one at first use. Stage convention throughout: **2 = ours =
destination, 3 = theirs = origin** of the commit being replicated.

## Testing conventions

Tests are **integration tests**, not mocks: `TestRepository` (`CreateAsync`) spins up a
real temp git repo (`init -b main`, test user config, `commit.gpgsign false`) and deletes
it on `Dispose`. `CreateAsync(withSpaceInPath: true)` reproduces real `%TEMP%` paths that
contain spaces (e.g. `C:\Users\Paulo Rogerio\...`) — quoting bugs in git argument strings
show up here, so keep coverage for spaced paths when touching command construction.

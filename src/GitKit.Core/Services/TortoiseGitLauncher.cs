using System.Diagnostics;

namespace GitKit.Core.Services;

/// <summary>
/// Localiza e executa as ferramentas do TortoiseGit: o <c>TortoiseGitProc.exe</c>
/// (automação) e o <c>TortoiseGitMerge.exe</c> (editor de conflitos/merge).
/// </summary>
public sealed class TortoiseGitLauncher : ITortoiseGitLauncher
{
    private const string ProcExe = "TortoiseGitProc.exe";
    private const string MergeExe = "TortoiseGitMerge.exe";

    private string? _procPath;
    private string? _mergePath;

    public TortoiseGitLauncher(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            ApplyFromPath(explicitPath);
        else
            ResolveFromKnownLocations();
    }

    public bool IsAvailable => Exists(_procPath) || Exists(_mergePath);

    public string? ExecutablePath => Exists(_procPath) ? _procPath : _mergePath;

    public bool IsMergeToolAvailable => Exists(_mergePath);

    public string? MergeToolPath => _mergePath;

    public bool TrySetExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        ApplyFromPath(path);
        return IsAvailable;
    }

    // Dado qualquer executável do TortoiseGit, deriva os dois (ambos ficam no
    // mesmo diretório "bin" da instalação).
    private void ApplyFromPath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return;

        var proc = Path.Combine(dir, ProcExe);
        var merge = Path.Combine(dir, MergeExe);

        _procPath = File.Exists(proc) ? proc : _procPath;
        _mergePath = File.Exists(merge) ? merge : _mergePath;
    }

    private void ResolveFromKnownLocations()
    {
        var binDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseGit", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TortoiseGit", "bin"),
        };

        foreach (var dir in binDirs)
        {
            if (Directory.Exists(dir))
                ApplyFromPath(Path.Combine(dir, ProcExe));
        }

        // Tenta também via PATH.
        if (!IsAvailable)
        {
            var fromPath = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(dir => Path.Combine(dir, ProcExe))
                .FirstOrDefault(File.Exists);

            if (fromPath is not null)
                ApplyFromPath(fromPath);
        }
    }

    private static bool Exists(string? path) => path is not null && File.Exists(path);

    public bool OpenResolveDialog(string repositoryPath) => LaunchProc("resolve", repositoryPath, repositoryPath);

    public bool OpenCommitDialog(string repositoryPath) => LaunchProc("commit", repositoryPath, repositoryPath);

    public bool OpenConflictEditor(string repositoryPath, string conflictedFile)
    {
        var fullPath = Path.IsPathRooted(conflictedFile)
            ? conflictedFile
            : Path.Combine(repositoryPath, conflictedFile.Replace('/', Path.DirectorySeparatorChar));

        // /command:conflicteditor delega ao editor de conflitos (TortoiseGitMerge).
        return LaunchProc("conflicteditor", fullPath, repositoryPath);
    }

    public bool OpenMerge(
        string baseFile, string mineFile, string theirsFile, string mergedFile,
        string? baseName = null, string? mineName = null, string? theirsName = null, string? mergedName = null)
    {
        if (!IsMergeToolAvailable)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _mergePath!,
                UseShellExecute = false,
                // Aspas só no valor (parser do TortoiseGit) — preserva espaços.
                Arguments = TortoiseGitArguments.BuildMerge(
                    baseFile, mineFile, theirsFile, mergedFile,
                    baseName, mineName, theirsName, mergedName),
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool LaunchProc(string command, string path, string workingDirectory)
    {
        if (!Exists(_procPath))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _procPath!,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
                // Aspas só no valor (parser do TortoiseGit) — preserva espaços.
                Arguments = TortoiseGitArguments.BuildProc(command, path),
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

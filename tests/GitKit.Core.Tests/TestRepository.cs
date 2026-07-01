using GitKit.Core.Services;

namespace GitKit.Core.Tests;

/// <summary>
/// Cria um repositório git temporário e descartável para os testes.
/// </summary>
public sealed class TestRepository : IDisposable
{
    private readonly ProcessRunner _runner = new();

    public string Path { get; }

    private TestRepository(string path) => Path = path;

    public static async Task<TestRepository> CreateAsync(bool withSpaceInPath = false)
    {
        // Opcionalmente cria o repositório em um caminho COM ESPAÇO, reproduzindo
        // %TEMP% real (ex.: C:\Users\Paulo Rogerio\AppData\Local\Temp).
        var folder = (withSpaceInPath ? "git kit teste " : "gitkit-test-") + Guid.NewGuid().ToString("N");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), folder);
        Directory.CreateDirectory(dir);
        var repo = new TestRepository(dir);

        await repo.GitAsync("init -b main");
        await repo.GitAsync("config user.email test@gitkit.local");
        await repo.GitAsync("config user.name \"git.kit test\"");
        await repo.GitAsync("config commit.gpgsign false");
        return repo;
    }

    public async Task<string> CommitFileAsync(string relativePath, string content, string message)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await GitAsync($"add \"{relativePath}\"");

        // Usa -F com arquivo para que a mensagem possa conter aspas/barras livremente.
        var messageFile = System.IO.Path.Combine(Path, ".git", $"COMMIT_MSG_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(messageFile, message);
        try
        {
            await GitAsync($"commit -F \"{messageFile}\"");
        }
        finally
        {
            try { File.Delete(messageFile); } catch { }
        }

        var head = await RunAsync("rev-parse HEAD");
        return head.StandardOutput.Trim();
    }

    public Task GitAsync(string args) => RunAsync(args);

    public async Task<GitKit.Core.Models.GitCommandResult> RunAsync(string args)
    {
        var result = await _runner.RunAsync("git", args, Path);
        if (!result.Success)
            throw new InvalidOperationException($"git {args} falhou:\n{result.CombinedOutput}");
        return result;
    }

    public Task<GitKit.Core.Models.GitCommandResult> RunRawAsync(string args)
        => _runner.RunAsync("git", args, Path);

    public void Dispose()
    {
        try
        {
            // Remove atributos read-only que o git costuma deixar em objetos.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Limpeza best-effort.
        }
    }
}

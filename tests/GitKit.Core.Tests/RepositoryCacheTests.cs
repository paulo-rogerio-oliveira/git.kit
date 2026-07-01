using GitKit.Core.Services;
using Xunit;

namespace GitKit.Core.Tests;

public sealed class RepositoryCacheTests
{
    private static (RepositoryCache cache, string cacheRoot) NewCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "git.kit-cache-" + Guid.NewGuid().ToString("N"));
        return (new RepositoryCache(new GitService(new ProcessRunner()), root), root);
    }

    [Fact]
    public async Task EnsureCache_creates_mirror_and_registers_in_json()
    {
        using var source = await TestRepository.CreateAsync();
        await source.CommitFileAsync("a.txt", "v1", "commit 1");
        await source.GitAsync("branch feature");

        var (cache, root) = NewCache();
        try
        {
            var mirror = await cache.EnsureCacheAsync(source.Path);

            Assert.NotNull(mirror);
            Assert.True(File.Exists(Path.Combine(mirror!, "HEAD")), "o cache deve ser um repositório bare (com HEAD)");
            // O índice JSON foi criado e contém a URL.
            var indexPath = Path.Combine(root, "cache-index.json");
            Assert.True(File.Exists(indexPath));
            Assert.Contains(source.Path.Replace("\\", "\\\\"), File.ReadAllText(indexPath));

            // A cópia de trabalho pode ser clonada do cache e traz os branches como origin/*.
            var work = Path.Combine(root, "work");
            var clone = await new GitService(new ProcessRunner()).CloneAsync(mirror!, work);
            Assert.True(clone.Success, clone.CombinedOutput);
            var branches = await new GitService(new ProcessRunner()).GetBranchesAsync(work);
            Assert.Contains(branches, b => b is { Name: "origin/feature", IsRemote: true });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EnsureCache_reuses_and_updates_existing_mirror()
    {
        using var source = await TestRepository.CreateAsync();
        await source.CommitFileAsync("a.txt", "v1", "commit 1");

        var (cache, root) = NewCache();
        try
        {
            var first = await cache.EnsureCacheAsync(source.Path);
            Assert.NotNull(first);

            // Novo commit no "remote".
            await source.CommitFileAsync("a.txt", "v2", "commit 2");

            // Segunda chamada deve reutilizar o MESMO diretório e atualizar o espelho.
            var second = await cache.EnsureCacheAsync(source.Path);
            Assert.Equal(first, second);

            // O espelho reflete o commit novo.
            var log = await new GitService(new ProcessRunner()).GetCommitsAsync(first!, "main");
            Assert.Contains(log, c => c.Subject == "commit 2");

            // Não duplicou entradas no índice.
            var json = File.ReadAllText(Path.Combine(root, "cache-index.json"));
            var occurrences = json.Split("\"Url\"").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IRepositoryCache"/>. Cada remote vira um clone
/// <c>--mirror</c> em <c>&lt;raiz&gt;\&lt;nome&gt;-&lt;hash&gt;.git</c>, registrado
/// em <c>cache-index.json</c> na raiz do cache.
/// </summary>
public sealed class RepositoryCache : IRepositoryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly IGitService _git;
    private readonly string _cacheRoot;
    private readonly string _indexPath;

    public RepositoryCache(IGitService git, string cacheRoot)
    {
        _git = git;
        _cacheRoot = cacheRoot;
        _indexPath = Path.Combine(_cacheRoot, "cache-index.json");
    }

    public async Task<string?> EnsureCacheAsync(string repositoryUrl, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var url = repositoryUrl.Trim();
            if (url.Length == 0)
                return null;

            Directory.CreateDirectory(_cacheRoot);
            var index = LoadIndex();
            var key = NormalizeUrl(url);
            var entry = index.Entries.Find(e => NormalizeUrl(e.Url) == key);

            // Cache existente e válido → atualiza (melhor-esforço) e reutiliza.
            if (entry is not null && CacheLooksValid(entry.Path))
            {
                await _git.UpdateCacheAsync(entry.Path, progress, ct).ConfigureAwait(false);
                entry.Url = url;
                entry.UpdatedUtc = DateTimeOffset.UtcNow;
                SaveIndex(index);
                return entry.Path;
            }

            // Entrada obsoleta (pasta sumiu/corrompida): remove antes de recriar.
            if (entry is not null)
            {
                index.Entries.Remove(entry);
                TryDelete(entry.Path);
            }

            var cacheDir = Path.Combine(_cacheRoot, BuildKey(url));
            TryDelete(cacheDir);

            var clone = await _git.CloneMirrorAsync(url, cacheDir, progress, ct).ConfigureAwait(false);
            if (!clone.Success)
            {
                TryDelete(cacheDir);
                return null;
            }

            index.Entries.Add(new RepositoryCacheEntry
            {
                Url = url,
                Path = cacheDir,
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            SaveIndex(index);
            return cacheDir;
        }
        catch (OperationCanceledException)
        {
            // Cancelamento pedido pelo usuário não é falha de cache: propaga
            // (senão o chamador recairia num clone direto que ele quis abortar).
            throw;
        }
        catch
        {
            // O cache é uma otimização: qualquer falha inesperada faz o chamador
            // recair no clone direto do remote.
            return null;
        }
    }

    private RepositoryCacheIndex LoadIndex()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                var json = File.ReadAllText(_indexPath, Utf8NoBom);
                return JsonSerializer.Deserialize<RepositoryCacheIndex>(json, JsonOptions) ?? new RepositoryCacheIndex();
            }
        }
        catch
        {
            // Índice corrompido → recomeça do zero.
        }
        return new RepositoryCacheIndex();
    }

    private void SaveIndex(RepositoryCacheIndex index)
    {
        try
        {
            var json = JsonSerializer.Serialize(index, JsonOptions);
            File.WriteAllText(_indexPath, json, Utf8NoBom);
        }
        catch
        {
            // Persistência do índice é melhor-esforço.
        }
    }

    // Um espelho válido é um diretório de repositório git (bare) — tem um arquivo HEAD.
    private static bool CacheLooksValid(string path)
        => !string.IsNullOrEmpty(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "HEAD"));

    // Chave estável de comparação: minúscula, sem barra final nem sufixo ".git".
    private static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('/', '\\').ToLowerInvariant();
        if (u.EndsWith(".git", StringComparison.Ordinal))
            u = u[..^4];
        return u;
    }

    // Nome de pasta legível + hash curto da URL para evitar colisões.
    private static string BuildKey(string url)
    {
        var normalized = NormalizeUrl(url);
        var lastSegment = normalized.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "repo";
        var name = new string(lastSegment.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        if (string.IsNullOrWhiteSpace(name))
            name = "repo";

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var shortHash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        return $"{name}-{shortHash}.git";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignora */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Melhor-esforço.
        }
    }
}

using System.IO;

namespace GitKit.App.Services;

/// <summary>
/// Gerencia as pastas de trabalho (cópias temporárias dos repositórios) em uma
/// raiz de caminho CURTO, e faz a limpeza das pastas deixadas por sessões
/// anteriores. Caminhos curtos (ex.: C:\gtk\1) evitam estourar o limite de
/// caminho do Windows quando o git cria arquivos profundamente aninhados.
/// </summary>
public sealed class WorkspaceService
{
    /// <summary>Raiz onde as pastas de trabalho são criadas.</summary>
    public string WorkRoot { get; }

    public WorkspaceService() => WorkRoot = ResolveWorkRoot();

    /// <summary>
    /// Cria uma pasta de trabalho nova (&lt;raiz&gt;\&lt;n&gt;), usando o menor
    /// inteiro livre. Tolera corrida com outra instância.
    /// </summary>
    public string CreateWorkFolder()
    {
        for (var i = 1; ; i++)
        {
            var target = Path.Combine(WorkRoot, i.ToString());
            if (Directory.Exists(target))
                continue;

            try
            {
                Directory.CreateDirectory(target);
                return target;
            }
            catch (IOException)
            {
                // Corrida com outra instância: tenta o próximo inteiro.
            }
        }
    }

    /// <summary>
    /// Lista as pastas de trabalho já existentes na raiz (nomeadas por inteiro).
    /// Deve ser chamado no INÍCIO, antes de criar novas pastas, para que a limpeza
    /// remova apenas o que sobrou de sessões anteriores — nunca as pastas criadas
    /// durante o uso atual.
    /// </summary>
    public IReadOnlyList<string> SnapshotExistingFolders()
    {
        try
        {
            return Directory.EnumerateDirectories(WorkRoot)
                .Where(dir => int.TryParse(Path.GetFileName(dir), out _))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Remove, em background e em melhor-esforço, as pastas informadas. Pastas
    /// em uso (ex.: por outra instância) são ignoradas silenciosamente.
    /// </summary>
    public Task CleanupAsync(IEnumerable<string> folders, CancellationToken ct = default)
    {
        var list = folders.ToArray();
        if (list.Length == 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            foreach (var folder in list)
            {
                if (ct.IsCancellationRequested)
                    break;
                TryDeleteDirectory(folder);
            }
        }, ct);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            // git costuma deixar objetos com atributo read-only; remove antes de apagar.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignora */ }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Melhor-esforço: uma pasta em uso é simplesmente deixada para a próxima vez.
        }
    }

    private static string ResolveWorkRoot()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var preferred = Path.Combine(drive + Path.DirectorySeparatorChar, "gtk");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "gtk");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}

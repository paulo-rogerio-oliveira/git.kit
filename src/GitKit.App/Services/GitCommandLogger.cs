using System.IO;
using System.Text;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.Services;

/// <summary>
/// Persiste o log dos comandos git executados em um arquivo por sessão, numa
/// pasta SEPARADA das cópias de trabalho (%LOCALAPPDATA%\git.kit\logs), para que
/// a limpeza das pastas de trabalho nunca apague o histórico de utilização.
/// </summary>
public sealed class GitCommandLogger
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _gate = new();

    /// <summary>Pasta onde os arquivos de log são gravados.</summary>
    public string LogDirectory { get; }

    /// <summary>Arquivo de log desta sessão.</summary>
    public string LogFilePath { get; }

    /// <summary>
    /// Habilita a gravação. Desligado por padrão (acompanha o checkbox de log da
    /// UI): com o log desabilitado, nenhum arquivo é criado.
    /// </summary>
    public bool Enabled { get; set; }

    public GitCommandLogger()
    {
        LogDirectory = ResolveLogDirectory();
        LogFilePath = Path.Combine(LogDirectory, $"git-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>Passa a registrar cada comando executado pelo serviço git informado.</summary>
    public void Attach(IGitService git) => git.CommandExecuted += Write;

    private void Write(GitCommandResult result)
    {
        if (!Enabled)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] $ ").AppendLine(result.Command);
            var output = result.CombinedOutput;
            if (!string.IsNullOrWhiteSpace(output))
                sb.AppendLine(output);
            sb.AppendLine();

            lock (_gate)
                File.AppendAllText(LogFilePath, sb.ToString(), Utf8NoBom);
        }
        catch
        {
            // Log é melhor-esforço; nunca deve interromper a operação de git.
        }
    }

    private static string ResolveLogDirectory()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(baseDir, "git.kit", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "git.kit", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}

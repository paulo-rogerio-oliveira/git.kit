namespace GitKit.Core.Services;

/// <summary>
/// Abre o TortoiseGit para resolução manual de conflitos.
/// </summary>
public interface ITortoiseGitLauncher
{
    /// <summary>Indica se alguma ferramenta do TortoiseGit foi localizada.</summary>
    bool IsAvailable { get; }

    /// <summary>Caminho de um executável do TortoiseGit, se conhecido.</summary>
    string? ExecutablePath { get; }

    /// <summary>Indica se o <c>TortoiseGitMerge.exe</c> foi localizado.</summary>
    bool IsMergeToolAvailable { get; }

    /// <summary>Caminho do <c>TortoiseGitMerge.exe</c>, se conhecido.</summary>
    string? MergeToolPath { get; }

    /// <summary>
    /// Define manualmente o caminho do <c>TortoiseGitProc.exe</c>
    /// (selecionado pelo usuário). Retorna true se o arquivo for válido.
    /// </summary>
    bool TrySetExecutable(string path);

    /// <summary>
    /// Abre a janela de resolução de conflitos do TortoiseGit para o
    /// repositório informado. Retorna true se o processo foi iniciado.
    /// </summary>
    bool OpenResolveDialog(string repositoryPath);

    /// <summary>
    /// Abre o editor de conflitos via <c>TortoiseGitProc /command:conflicteditor</c>
    /// (delega ao TortoiseGitMerge). Fallback quando não dá para chamar o merge direto.
    /// Retorna true se o processo foi iniciado.
    /// </summary>
    bool OpenConflictEditor(string repositoryPath, string conflictedFile);

    /// <summary>
    /// Abre o <b>TortoiseGitMerge.exe</b> diretamente em modo merge 3-way,
    /// recebendo os arquivos base/mine/theirs e gravando o resultado em
    /// <paramref name="mergedFile"/>. Retorna true se o processo foi iniciado.
    /// </summary>
    bool OpenMerge(
        string baseFile, string mineFile, string theirsFile, string mergedFile,
        string? baseName = null, string? mineName = null, string? theirsName = null, string? mergedName = null);

    /// <summary>Abre a janela de commit do TortoiseGit (para concluir após resolver).</summary>
    bool OpenCommitDialog(string repositoryPath);
}

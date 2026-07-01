using System.IO;
using GitKit.Core.Services;

namespace GitKit.App.Services;

/// <summary>
/// Centraliza a abertura do TortoiseGitMerge para um arquivo em conflito,
/// incluindo a localização do executável e a extração das versões base/destino/origem.
/// </summary>
public sealed class ConflictResolutionCoordinator
{
    private readonly IGitService _git;
    private readonly ITortoiseGitLauncher _tortoise;
    private readonly IDialogService _dialogs;

    public ConflictResolutionCoordinator(IGitService git, ITortoiseGitLauncher tortoise, IDialogService dialogs)
    {
        _git = git;
        _tortoise = tortoise;
        _dialogs = dialogs;
    }

    /// <summary>
    /// Garante um TortoiseGit utilizável; se não localizado, pede o executável ao usuário.
    /// </summary>
    public bool EnsureTortoiseAvailable()
    {
        if (_tortoise.IsAvailable)
            return true;

        var picked = _dialogs.PickFile(
            "Localize um executável do TortoiseGit (TortoiseGitProc.exe ou TortoiseGitMerge.exe)",
            "TortoiseGit (Proc/Merge)|TortoiseGitProc.exe;TortoiseGitMerge.exe|Executáveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(picked))
            return false;

        if (!_tortoise.TrySetExecutable(picked))
        {
            _dialogs.ShowError("TortoiseGit", "O arquivo selecionado não é um executável válido.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Abre o TortoiseGitMerge para o arquivo em conflito informado. Extrai as três
    /// versões (base/destino/origem) do índice; se o merge direto não estiver
    /// disponível, usa o conflicteditor do TortoiseGitProc. Retorna true se algo abriu.
    /// </summary>
    public async Task<bool> OpenMergeForFileAsync(string repositoryPath, string file)
    {
        if (!EnsureTortoiseAvailable())
        {
            _dialogs.ShowError("TortoiseGit", "O TortoiseGit não foi localizado nem selecionado.");
            return false;
        }

        // Caminho preferencial: o conflicteditor do TortoiseGitProc. O próprio
        // TortoiseGit extrai as versões do índice PRESERVANDO a codificação e as
        // quebras de linha originais do arquivo (e marca como resolvido ao concluir).
        // Evita a troca de encoding que ocorre ao extrairmos as versões nós mesmos.
        if (_tortoise.OpenConflictEditor(repositoryPath, file))
            return true;

        // Fallback (apenas se o TortoiseGitProc não estiver disponível): chama o
        // TortoiseGitMerge diretamente com as versões extraídas por nós.
        if (!_tortoise.IsMergeToolAvailable)
        {
            _dialogs.ShowError("TortoiseGitMerge",
                $"Não foi possível abrir o editor de conflitos para '{file}'.");
            return false;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "git.kit", "merge", Guid.NewGuid().ToString("N"));
        var safe = file.Replace('/', '_').Replace('\\', '_');

        // Estágios do índice: 1=base (ancestral), 2=ours (destino), 3=theirs (origem).
        var basePath = await _git.ExtractConflictStageAsync(repositoryPath, file, 1, Path.Combine(workDir, safe + ".base.txt"));
        var minePath = await _git.ExtractConflictStageAsync(repositoryPath, file, 2, Path.Combine(workDir, safe + ".destino.txt"));
        var theirsPath = await _git.ExtractConflictStageAsync(repositoryPath, file, 3, Path.Combine(workDir, safe + ".origem.txt"));

        // Sem ours/theirs não há merge 3-way possível por aqui.
        if (minePath is null || theirsPath is null)
        {
            _dialogs.ShowError("TortoiseGitMerge",
                $"Não foi possível obter as versões do arquivo '{file}' para o merge.");
            return false;
        }

        // Conflito de adição (sem ancestral): usa um base vazio.
        if (basePath is null)
        {
            basePath = Path.Combine(workDir, safe + ".base.txt");
            Directory.CreateDirectory(workDir);
            await File.WriteAllTextAsync(basePath, string.Empty);
        }

        // O arquivo "merged" é o próprio arquivo da árvore de trabalho.
        var mergedFile = Path.Combine(repositoryPath, file.Replace('/', Path.DirectorySeparatorChar));

        var opened = _tortoise.OpenMerge(
            basePath, minePath, theirsPath, mergedFile,
            baseName: $"{file} (base)",
            mineName: $"{file} (destino)",
            theirsName: $"{file} (origem)",
            mergedName: file);

        if (!opened)
        {
            _dialogs.ShowError("TortoiseGitMerge",
                $"Não foi possível abrir o TortoiseGitMerge para '{file}'.");
            return false;
        }

        return true;
    }
}

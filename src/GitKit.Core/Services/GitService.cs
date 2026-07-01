using System.Globalization;
using System.Text;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IGitService"/> orquestrando a CLI do git.
/// </summary>
public sealed class GitService : IGitService
{
    // Hash da árvore vazia do git — usado como "pai" de um commit raiz.
    private const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private const char FieldSep = '\x1f';

    private readonly IProcessRunner _runner;
    private readonly string _gitExecutable;

    public GitService(IProcessRunner runner, string gitExecutable = "git")
    {
        _runner = runner;
        _gitExecutable = gitExecutable;
    }

    public event Action<GitCommandResult>? CommandExecuted;

    private async Task<GitCommandResult> GitAsync(string arguments, string? workingDirectory, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        var result = await _runner.RunAsync(_gitExecutable, arguments, workingDirectory, onOutputLine, ct).ConfigureAwait(false);
        CommandExecuted?.Invoke(result);
        return result;
    }

    // Converte um IProgress opcional no callback de linha do runner.
    private static Action<string>? AsLineCallback(IProgress<string>? progress)
        => progress is null ? null : progress.Report;

    public async Task<bool> IsGitAvailableAsync(CancellationToken ct = default)
    {
        var result = await GitAsync("--version", null, ct).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        var result = await GitAsync("rev-parse --is-inside-work-tree", path, ct).ConfigureAwait(false);
        return result.Success && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GitCommandResult> CloneAsync(string repositoryUrl, string destinationDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        // Clona o conteúdo do repositório dentro do diretório selecionado.
        var args = $"clone --progress \"{repositoryUrl}\" \"{destinationDirectory}\"";
        return GitAsync(args, null, ct, AsLineCallback(progress));
    }

    public Task<GitCommandResult> CloneMirrorAsync(string repositoryUrl, string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // O git cria o diretório de destino; garante apenas o pai.
        var parent = Path.GetDirectoryName(cacheDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var args = $"clone --mirror --progress \"{repositoryUrl}\" \"{cacheDirectory}\"";
        return GitAsync(args, null, ct, AsLineCallback(progress));
    }

    public Task<GitCommandResult> UpdateCacheAsync(string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
        // fetch --all --prune equivale ao 'remote update --prune' num espelho,
        // mas aceita --progress (o 'remote update' não repassa a flag).
        => GitAsync("fetch --all --prune --progress", cacheDirectory, ct, AsLineCallback(progress));

    public Task<GitCommandResult> FetchAsync(string repositoryPath, CancellationToken ct = default)
        => GitAsync("fetch --all --prune", repositoryPath, ct);

    public async Task<string> GetRemoteUrlAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await GitAsync("remote get-url origin", repositoryPath, ct).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    public async Task<GitCommandResult> SetRemoteUrlAsync(string repositoryPath, string remoteUrl, CancellationToken ct = default)
    {
        // Tenta atualizar; se o remote não existir, adiciona.
        var setUrl = await GitAsync($"remote set-url origin \"{remoteUrl}\"", repositoryPath, ct).ConfigureAwait(false);
        if (setUrl.Success)
            return setUrl;

        return await GitAsync($"remote add origin \"{remoteUrl}\"", repositoryPath, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default)
    {
        // refname (completo) | refname:short | HEAD-marker
        // O refname completo (refs/heads/... vs refs/remotes/...) diz de forma
        // DETERMINÍSTICA se o branch é local ou remoto — sem heurística por nome.
        var format = $"%(refname){FieldSep}%(refname:short){FieldSep}%(HEAD)";
        var result = await GitAsync(
            $"for-each-ref --format=\"{format}\" refs/heads refs/remotes",
            repositoryPath, ct).ConfigureAwait(false);

        if (!result.Success)
            return Array.Empty<GitBranch>();

        var branches = new List<GitBranch>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(FieldSep);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                continue;

            var fullRef = parts[0].Trim();
            var name = parts[1].Trim();

            // Ignora o ponteiro simbólico origin/HEAD.
            if (fullRef.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;

            var isRemote = fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var isCurrent = parts.Length > 2 && parts[2].Trim() == "*";

            branches.Add(new GitBranch(name, isRemote, isCurrent));
        }

        return branches;
    }

    // Formato de uma linha por commit para 'git log', delimitado por \x1f.
    private static readonly string LogFormat = $"%H{FieldSep}%an{FieldSep}%aI{FieldSep}%s";

    public async Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repositoryPath, string branch, int max = 100, int skip = 0, CancellationToken ct = default)
    {
        var skipArg = skip > 0 ? $" --skip {skip}" : string.Empty;
        var result = await GitAsync(
            $"log \"{branch}\" -n {max}{skipArg} --format=\"{LogFormat}\"",
            repositoryPath, ct).ConfigureAwait(false);

        return result.Success ? ParseCommits(result.StandardOutput) : Array.Empty<GitCommit>();
    }

    public async Task<IReadOnlyList<GitCommit>> SearchCommitsAsync(string repositoryPath, string branch, string term, int max = 100, CancellationToken ct = default)
    {
        var text = term.Trim();
        if (text.Length == 0)
            return await GetCommitsAsync(repositoryPath, branch, max, 0, ct).ConfigureAwait(false);

        // --fixed-strings: o termo é literal (não regex); -i via --regexp-ignore-case.
        // --grep e --author combinados no MESMO comando exigem ambos; roda em
        // separado e mescla para obter a semântica OU (mensagem OU autor).
        var escaped = text.Replace("\"", "\\\"");
        var common = $"log \"{branch}\" -n {max} --fixed-strings --regexp-ignore-case --format=\"{LogFormat}\"";

        var byMessage = await GitAsync($"{common} --grep=\"{escaped}\"", repositoryPath, ct).ConfigureAwait(false);
        var byAuthor = await GitAsync($"{common} --author=\"{escaped}\"", repositoryPath, ct).ConfigureAwait(false);

        var merged = new Dictionary<string, GitCommit>(StringComparer.Ordinal);
        foreach (var result in new[] { byMessage, byAuthor })
        {
            if (!result.Success)
                continue;
            foreach (var commit in ParseCommits(result.StandardOutput))
                merged.TryAdd(commit.Hash, commit);
        }

        return merged.Values
            .OrderByDescending(c => c.Date)
            .Take(max)
            .ToArray();
    }

    private static IReadOnlyList<GitCommit> ParseCommits(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(FieldSep);
            if (parts.Length < 4)
                continue;

            var date = DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            commits.Add(new GitCommit(parts[0].Trim(), parts[1].Trim(), date, parts[3]));
        }

        return commits;
    }

    public async Task<ReplicationResult> ReplicateCommitAsync(
        string repositoryPath,
        GitCommit commit,
        string destinationBranch,
        ReplicationMode mode,
        CancellationToken ct = default)
    {
        // 1) Garante árvore de trabalho limpa antes de iniciar.
        var status = await GitAsync("status --porcelain", repositoryPath, ct).ConfigureAwait(false);
        if (status.Success && !string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return ReplicationResult.Failure(
                "A árvore de trabalho possui alterações pendentes. Faça commit/stash antes de replicar.",
                repositoryPath);
        }

        // 2) Prepara o branch de destino: usa o existente ou cria um novo
        //    (baseado em develop/master conforme o sufixo do nome).
        var (checkout, localBranch, baseInfo) = await PrepareDestinationAsync(repositoryPath, destinationBranch, ct).ConfigureAwait(false);
        if (!checkout.Success)
        {
            return ReplicationResult.Failure(
                $"Não foi possível preparar o branch de destino '{destinationBranch}'.\n{checkout.CombinedOutput}",
                repositoryPath);
        }

        var result = mode switch
        {
            ReplicationMode.CherryPick => await CherryPickAsync(repositoryPath, commit, ct).ConfigureAwait(false),
            ReplicationMode.DiffIntegration => await DiffIntegrationAsync(repositoryPath, commit, ct).ConfigureAwait(false),
            _ => ReplicationResult.Failure("Estratégia de replicação desconhecida.", repositoryPath)
        };

        // Associa o nome do branch LOCAL preparado (é o ref correto para o push).
        result = result.WithBranch(localBranch);
        return baseInfo is null ? result : result.WithPrefix(baseInfo);
    }

    /// <summary>
    /// Coloca o branch de destino em checkout. Se ele não existir, cria um novo
    /// branch baseado em <c>develop</c> (nome terminado em "dev") ou <c>master</c>.
    /// Retorna também uma mensagem informando a base usada, quando um branch é criado.
    /// </summary>
    private async Task<(GitCommandResult Result, string LocalBranch, string? BaseInfo)> PrepareDestinationAsync(
        string repositoryPath, string branch, CancellationToken ct)
    {
        var name = branch.Trim();

        // Caso o usuário tenha selecionado um branch remoto explicitamente (origin/x).
        if (name.StartsWith("origin/", StringComparison.Ordinal))
        {
            var local = name["origin/".Length..];
            var track = await GitAsync($"checkout -B \"{local}\" --track \"{name}\"", repositoryPath, ct).ConfigureAwait(false);
            return (track, local, null);
        }

        // Branch local já existe → apenas faz checkout.
        if (await RefExistsAsync(repositoryPath, $"refs/heads/{name}", ct).ConfigureAwait(false))
            return (await GitAsync($"checkout \"{name}\"", repositoryPath, ct).ConfigureAwait(false), name, null);

        // Existe como remoto (origin/<name>) → cria local rastreando o remoto.
        if (await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{name}", ct).ConfigureAwait(false))
        {
            var track = await GitAsync($"checkout -B \"{name}\" --track \"origin/{name}\"", repositoryPath, ct).ConfigureAwait(false);
            return (track, name, null);
        }

        // Branch novo → determina a base pelo sufixo do nome.
        var endsWithDev = name.EndsWith("dev", StringComparison.OrdinalIgnoreCase);
        var candidates = endsWithDev ? new[] { "develop" } : new[] { "master", "main" };

        var baseRef = await ResolveExistingRefAsync(repositoryPath, candidates, ct).ConfigureAwait(false);
        if (baseRef is null)
        {
            var wanted = string.Join("/", candidates);
            return (new GitCommandResult("(resolução de base)", 1, string.Empty,
                $"Não foi possível localizar o branch base '{wanted}' para criar '{name}'."), name, null);
        }

        var create = await GitAsync($"checkout -b \"{name}\" \"{baseRef}\"", repositoryPath, ct).ConfigureAwait(false);
        var info = create.Success
            ? $"Novo branch '{name}' criado a partir de '{baseRef}'."
            : null;
        return (create, name, info);
    }

    /// <summary>Verifica se uma referência específica existe no repositório.</summary>
    private async Task<bool> RefExistsAsync(string repositoryPath, string fullRef, CancellationToken ct)
    {
        var result = await GitAsync($"show-ref --verify --quiet \"{fullRef}\"", repositoryPath, ct).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>Retorna a primeira base existente (local ou origin/&lt;nome&gt;) entre os candidatos.</summary>
    private async Task<string?> ResolveExistingRefAsync(string repositoryPath, IEnumerable<string> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            if (await RefExistsAsync(repositoryPath, $"refs/heads/{candidate}", ct).ConfigureAwait(false))
                return candidate;
            if (await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{candidate}", ct).ConfigureAwait(false))
                return $"origin/{candidate}";
        }

        return null;
    }

    private async Task<ReplicationResult> CherryPickAsync(string repositoryPath, GitCommit commit, CancellationToken ct)
    {
        // -x registra a origem do commit na mensagem.
        var result = await GitAsync($"cherry-pick -x {commit.Hash}", repositoryPath, ct).ConfigureAwait(false);
        if (result.Success)
        {
            return ReplicationResult.Ok(
                $"Commit {commit.ShortHash} replicado via cherry-pick com sucesso.", repositoryPath);
        }

        if (await HasConflictsAsync(repositoryPath, ct).ConfigureAwait(false))
        {
            return ReplicationResult.Conflicts(
                $"Cherry-pick do commit {commit.ShortHash} gerou conflitos que não puderam ser resolvidos automaticamente.\n" +
                "Resolva os conflitos no TortoiseGit e conclua o commit.",
                repositoryPath);
        }

        // Cherry-pick vazio: as mudanças já existem no destino (nada a aplicar).
        if (IsEmptyCherryPick(result))
        {
            // --skip encerra a operação de cherry-pick sem criar commit vazio.
            await GitAsync("cherry-pick --skip", repositoryPath, ct).ConfigureAwait(false);
            return ReplicationResult.AlreadyApplied(
                $"O commit {commit.ShortHash} já está presente no branch de destino — nada a replicar.",
                repositoryPath);
        }

        // Falha sem conflito — aborta para deixar a árvore limpa.
        await GitAsync("cherry-pick --abort", repositoryPath, ct).ConfigureAwait(false);
        return ReplicationResult.Failure(
            $"Falha no cherry-pick do commit {commit.ShortHash}.\n{result.CombinedOutput}", repositoryPath);
    }

    private async Task<ReplicationResult> DiffIntegrationAsync(string repositoryPath, GitCommit commit, CancellationToken ct)
    {
        var patchPath = Path.Combine(Path.GetTempPath(), $"gitkit-{commit.ShortHash}-{Guid.NewGuid():N}.patch");

        try
        {
            // Determina o pai; se for commit raiz usa a árvore vazia.
            var parentResult = await GitAsync($"rev-parse --verify --quiet {commit.Hash}^", repositoryPath, ct).ConfigureAwait(false);
            var baseRef = parentResult.Success && !string.IsNullOrWhiteSpace(parentResult.StandardOutput)
                ? $"{commit.Hash}^"
                : EmptyTreeHash;

            // Gera o diff diretamente em arquivo para preservar a codificação do patch.
            var diff = await GitAsync(
                $"diff --binary {baseRef} {commit.Hash} --output=\"{patchPath}\"",
                repositoryPath, ct).ConfigureAwait(false);

            if (!diff.Success)
            {
                return ReplicationResult.Failure(
                    $"Não foi possível gerar o diff do commit {commit.ShortHash}.\n{diff.CombinedOutput}", repositoryPath);
            }

            if (new FileInfo(patchPath).Length == 0)
            {
                return ReplicationResult.AlreadyApplied(
                    $"O commit {commit.ShortHash} não produziu alterações para integrar — nada a replicar.", repositoryPath);
            }

            // Aplica com fallback de merge 3-way, indexando o resultado.
            var apply = await GitAsync(
                $"apply --3way --index \"{patchPath}\"", repositoryPath, ct).ConfigureAwait(false);

            if (apply.Success)
            {
                var message = $"{commit.Subject}\n\n(diff integrado a partir de {commit.ShortHash})";
                var commitResult = await CommitAsync(repositoryPath, message, ct).ConfigureAwait(false);
                return commitResult.Success
                    ? ReplicationResult.Ok($"Diff do commit {commit.ShortHash} integrado e commitado com sucesso.", repositoryPath)
                    : ReplicationResult.Failure($"Diff aplicado, mas o commit falhou.\n{commitResult.CombinedOutput}", repositoryPath);
            }

            if (await HasConflictsAsync(repositoryPath, ct).ConfigureAwait(false))
            {
                return ReplicationResult.Conflicts(
                    $"A integração do diff do commit {commit.ShortHash} gerou conflitos que não puderam ser resolvidos automaticamente.\n" +
                    "Resolva os conflitos no TortoiseGit e conclua o commit.",
                    repositoryPath);
            }

            return ReplicationResult.Failure(
                $"Não foi possível aplicar o diff do commit {commit.ShortHash} automaticamente.\n{apply.CombinedOutput}\n" +
                "Resolva manualmente no TortoiseGit.",
                repositoryPath);
        }
        finally
        {
            try { if (File.Exists(patchPath)) File.Delete(patchPath); } catch { /* ignora limpeza */ }
        }
    }

    private async Task<GitCommandResult> CommitAsync(string repositoryPath, string message, CancellationToken ct)
    {
        // Grava a mensagem em arquivo e usa 'commit -F': evita qualquer problema de
        // escape na linha de comando (aspas, barras, %, quebras de linha).
        var messageFile = Path.Combine(Path.GetTempPath(), $"gitkit-msg-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(messageFile, message, new UTF8Encoding(false), ct).ConfigureAwait(false);
            return await GitAsync($"commit -F \"{messageFile}\"", repositoryPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(messageFile)) File.Delete(messageFile); } catch { /* ignora limpeza */ }
        }
    }

    /// <summary>
    /// Reconstrói a mensagem de um cherry-pick concluído manualmente: a mensagem
    /// original do commit + o mesmo trailer que o <c>git cherry-pick -x</c> adiciona.
    /// </summary>
    private async Task<string> BuildCherryPickMessageAsync(string repositoryPath, GitCommit commit, CancellationToken ct)
    {
        var raw = await GitAsync($"log -1 --format=%B {commit.Hash}", repositoryPath, ct).ConfigureAwait(false);
        var original = raw.Success && !string.IsNullOrWhiteSpace(raw.StandardOutput)
            ? raw.StandardOutput.TrimEnd('\r', '\n')
            : commit.Subject;
        return $"{original}\n\n(cherry picked from commit {commit.Hash})";
    }

    private static bool IsEmptyCherryPick(GitCommandResult result)
    {
        var output = result.CombinedOutput;
        return output.Contains("is now empty", StringComparison.OrdinalIgnoreCase)
               || output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
               || output.Contains("allow-empty", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasConflictsAsync(string repositoryPath, CancellationToken ct)
    {
        var files = await GetConflictedFilesAsync(repositoryPath, ct).ConfigureAwait(false);
        return files.Count > 0;
    }

    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync(string repositoryPath, CancellationToken ct = default)
    {
        // git diff --name-only --diff-filter=U lista os arquivos não mesclados.
        var result = await GitAsync("diff --name-only --diff-filter=U", repositoryPath, ct).ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<string>();

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<ConflictEntry>> GetConflictsAsync(string repositoryPath, CancellationToken ct = default)
    {
        // core.quotepath=false evita escapar caracteres não-ASCII nos caminhos.
        var result = await GitAsync("-c core.quotepath=false status --porcelain", repositoryPath, ct).ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<ConflictEntry>();

        var list = new List<ConflictEntry>();
        foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4)
                continue;

            var x = line[0];
            var y = line[1];
            if (!IsUnmerged(x, y))
                continue;

            var code = $"{x}{y}";
            var path = line[3..].Trim();
            list.Add(new ConflictEntry(path, code, DescribeConflict(code)));
        }

        return list;
    }

    private static bool IsUnmerged(char x, char y)
        => x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    private static string DescribeConflict(string code) => code switch
    {
        "DD" => "Ambos excluíram",
        "AU" => "Adicionado por nós",
        "UD" => "Excluído por eles",
        "UA" => "Adicionado por eles",
        "DU" => "Excluído por nós",
        "AA" => "Adicionado por ambos",
        "UU" => "Ambos modificaram",
        _ => "Conflito",
    };

    public async Task<string?> ExtractConflictStageAsync(
        string repositoryPath, string file, int stage, string destinationPath, CancellationToken ct = default)
    {
        // checkout-index --temp grava o blob do estágio direto em um arquivo
        // temporário (bytes intactos — capturar via stdout corromperia binários
        // e alteraria codificação/quebras de linha) e imprime "<temp>\t<caminho>".
        var result = await GitAsync(
            $"checkout-index --stage={stage} --temp -- \"{file}\"",
            repositoryPath, ct).ConfigureAwait(false);
        if (!result.Success)
            return null;

        var line = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.Contains('\t'));
        if (line is null)
            return null;

        var tempPath = Path.Combine(repositoryPath, line[..line.IndexOf('\t')]);
        if (!File.Exists(tempPath))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(tempPath, destinationPath, overwrite: true);
        return destinationPath;
    }

    public Task<GitCommandResult> AbortReplicationAsync(string repositoryPath, ReplicationMode mode, CancellationToken ct = default)
    {
        var command = mode == ReplicationMode.CherryPick ? "cherry-pick --abort" : "merge --abort";
        return GitAsync(command, repositoryPath, ct);
    }

    public async Task<ReplicationResult> ContinueReplicationAsync(
        string repositoryPath, GitCommit commit, ReplicationMode mode, CancellationToken ct = default)
    {
        // Arquivos que ainda contêm marcadores de conflito não estão resolvidos.
        // (Checagem ANTES de estagiar: 'git add' removeria o estado não-mesclado.)
        var conflicted = await GetConflictedFilesAsync(repositoryPath, ct).ConfigureAwait(false);
        var unresolved = new List<string>();
        foreach (var file in conflicted)
        {
            var full = Path.Combine(repositoryPath, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && ContainsConflictMarkers(await File.ReadAllTextAsync(full, ct).ConfigureAwait(false)))
                unresolved.Add(file);
        }

        if (unresolved.Count > 0)
        {
            return ReplicationResult.Conflicts(
                "Ainda há conflitos não resolvidos: " + string.Join(", ", unresolved) +
                ".\nResolva no TortoiseGitMerge (e salve) antes de concluir.",
                repositoryPath);
        }

        // Estagia os arquivos já resolvidos (idempotente).
        await GitAsync("add -A", repositoryPath, ct).ConfigureAwait(false);

        // Segurança: nenhum estado não-mesclado deve restar no índice.
        if (await HasConflictsAsync(repositoryPath, ct).ConfigureAwait(false))
        {
            return ReplicationResult.Conflicts(
                "Ainda há arquivos em conflito no índice. Resolva todos antes de concluir.",
                repositoryPath);
        }

        var branch = await GetCurrentBranchAsync(repositoryPath, ct).ConfigureAwait(false);

        if (mode == ReplicationMode.CherryPick)
        {
            // Se a resolução ficou idêntica ao destino, não há o que commitar.
            var emptyResolution = (await GitAsync("diff --cached --quiet HEAD", repositoryPath, ct).ConfigureAwait(false)).Success;
            if (emptyResolution)
            {
                await GitAsync("cherry-pick --skip", repositoryPath, ct).ConfigureAwait(false);
                return ReplicationResult
                    .AlreadyApplied(
                        $"A resolução ficou idêntica ao branch de destino — não há mudanças novas para commitar " +
                        $"(o conteúdo do commit {commit.ShortHash} já está refletido em '{branch}').\n" +
                        "Nada a commitar; o branch pode ser enviado (push) assim mesmo.",
                        repositoryPath)
                    .WithBranch(branch);
            }

            // Conclui com 'commit -F' em vez de 'cherry-pick --continue': este último
            // finaliza a mensagem com cleanup=strip, que REMOVE linhas iniciadas por
            // '#' (ex.: assuntos com número de issue), deixando só o trailer "(cherry
            // picked from commit ...)". 'commit -F' usa cleanup=whitespace e preserva
            // a mensagem; o autor original é mantido a partir de CHERRY_PICK_HEAD.
            var pickMessage = await BuildCherryPickMessageAsync(repositoryPath, commit, ct).ConfigureAwait(false);
            var picked = await CommitAsync(repositoryPath, pickMessage, ct).ConfigureAwait(false);
            return picked.Success
                ? ReplicationResult
                    .Ok($"Cherry-pick do commit {commit.ShortHash} concluído após a resolução de conflitos.", repositoryPath)
                    .WithBranch(branch)
                : ReplicationResult.Failure(
                    $"Não foi possível concluir o cherry-pick.\n{picked.CombinedOutput}", repositoryPath);
        }

        // Integração de diff: se a resolução não deixou nada estagiado vs HEAD,
        // o resultado é idêntico ao destino — nada a commitar.
        var hasStaged = !(await GitAsync("diff --cached --quiet HEAD", repositoryPath, ct).ConfigureAwait(false)).Success;
        if (!hasStaged)
        {
            return ReplicationResult
                .AlreadyApplied(
                    $"A resolução ficou idêntica ao branch de destino — não há mudanças novas para commitar " +
                    $"(o conteúdo do commit {commit.ShortHash} já está refletido em '{branch}').\n" +
                    "Nada a commitar; o branch pode ser enviado (push) assim mesmo.",
                    repositoryPath)
                .WithBranch(branch);
        }

        var message = $"{commit.Subject}\n\n(diff integrado a partir de {commit.ShortHash})";
        var commitResult = await CommitAsync(repositoryPath, message, ct).ConfigureAwait(false);
        return commitResult.Success
            ? ReplicationResult.Ok($"Integração do diff de {commit.ShortHash} concluída após a resolução de conflitos.", repositoryPath).WithBranch(branch)
            : ReplicationResult.Failure($"Não foi possível concluir o commit.\n{commitResult.CombinedOutput}", repositoryPath);
    }

    // Detecta marcadores de conflito deixados pelo git em um arquivo de texto.
    private static bool ContainsConflictMarkers(string content)
        => content.Contains("<<<<<<<", StringComparison.Ordinal)
           && content.Contains(">>>>>>>", StringComparison.Ordinal);

    private async Task<string> GetCurrentBranchAsync(string repositoryPath, CancellationToken ct)
    {
        var result = await GitAsync("rev-parse --abbrev-ref HEAD", repositoryPath, ct).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    public Task<GitCommandResult> PushAsync(string repositoryPath, string branch, bool setUpstream = true, CancellationToken ct = default)
    {
        var upstream = setUpstream ? "-u " : string.Empty;
        return GitAsync($"push {upstream}origin \"{branch}\"", repositoryPath, ct);
    }
}

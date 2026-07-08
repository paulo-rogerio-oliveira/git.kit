using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Operações de alto nível sobre um repositório git, realizadas via CLI.
/// </summary>
public interface IGitService : IGitCommandSource
{
    /// <summary>Verifica se o git está disponível no PATH.</summary>
    Task<bool> IsGitAvailableAsync(CancellationToken ct = default);

    /// <summary>Verifica se o diretório informado é a raiz de um repositório git.</summary>
    Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Clona <paramref name="repositoryUrl"/> dentro de <paramref name="destinationDirectory"/>.
    /// <paramref name="progress"/> (opcional) recebe as linhas de progresso do git em tempo real.
    /// </summary>
    Task<GitCommandResult> CloneAsync(string repositoryUrl, string destinationDirectory, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Cria um clone <c>--mirror</c> (bare, com todas as refs) de
    /// <paramref name="repositoryUrl"/> em <paramref name="cacheDirectory"/>, usado como
    /// cache local para clones de trabalho rápidos.
    /// <paramref name="progress"/> (opcional) recebe as linhas de progresso do git em tempo real.
    /// </summary>
    Task<GitCommandResult> CloneMirrorAsync(string repositoryUrl, string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Atualiza um cache espelho (<c>fetch --all --prune</c>), trazendo as refs mais
    /// recentes do remote de origem.
    /// <paramref name="progress"/> (opcional) recebe as linhas de progresso do git em tempo real.
    /// </summary>
    Task<GitCommandResult> UpdateCacheAsync(string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Atualiza as referências remotas (<c>git fetch --all --prune</c>).</summary>
    Task<GitCommandResult> FetchAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Retorna a URL do remote <c>origin</c> do repositório, ou string vazia
    /// se não houver remote configurado.
    /// </summary>
    Task<string> GetRemoteUrlAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Define a URL do remote <c>origin</c> do repositório (cria o remote se necessário).
    /// </summary>
    Task<GitCommandResult> SetRemoteUrlAsync(string repositoryPath, string remoteUrl, CancellationToken ct = default);

    /// <summary>Lista branches locais e remotos.</summary>
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Lista os commits mais recentes do branch informado. <paramref name="skip"/>
    /// pula os N primeiros (paginação: "carregar mais").
    /// </summary>
    Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repositoryPath, string branch, int max = 100, int skip = 0, CancellationToken ct = default);

    /// <summary>
    /// Busca commits em TODO o histórico do branch cuja mensagem (assunto/corpo)
    /// ou autor contenham <paramref name="term"/> (texto literal, sem distinção de
    /// maiúsculas), do mais recente para o mais antigo.
    /// </summary>
    Task<IReadOnlyList<GitCommit>> SearchCommitsAsync(string repositoryPath, string branch, string term, int max = 100, CancellationToken ct = default);

    /// <summary>
    /// Replica <paramref name="commit"/> do branch de origem no branch de destino,
    /// usando a estratégia escolhida. O branch de destino é colocado em checkout.
    /// </summary>
    Task<ReplicationResult> ReplicateCommitAsync(
        string repositoryPath,
        GitCommit commit,
        string destinationBranch,
        ReplicationMode mode,
        CancellationToken ct = default);

    /// <summary>Aborta uma operação de replicação em andamento (cherry-pick/merge).</summary>
    Task<GitCommandResult> AbortReplicationAsync(string repositoryPath, ReplicationMode mode, CancellationToken ct = default);

    /// <summary>
    /// Lista os commits presentes em <paramref name="sourceRef"/> e ausentes em
    /// <paramref name="baseRef"/> (<c>baseRef..sourceRef</c>), do mais antigo para o
    /// mais novo — a ordem correta para um cherry-pick sequencial.
    /// </summary>
    Task<IReadOnlyList<GitCommit>> ListCommitsBetweenAsync(
        string repositoryPath, string baseRef, string sourceRef, CancellationToken ct = default);

    /// <summary>
    /// Lista apenas os commits CRIADOS no próprio branch <paramref name="sourceRef"/>
    /// (do mais antigo para o mais novo): exclui <paramref name="baseRef"/> e também as
    /// linhas principais existentes (develop/master/main), para não replicar o que o
    /// branch apenas HERDOU da base em que foi criado.
    /// </summary>
    Task<IReadOnlyList<GitCommit>> ListBranchOwnCommitsAsync(
        string repositoryPath, string sourceRef, string baseRef, CancellationToken ct = default);

    /// <summary>
    /// Replica sequencialmente os <paramref name="commits"/> (a partir de
    /// <paramref name="startIndex"/>) num novo branch <paramref name="newBranch"/>
    /// criado a partir de <paramref name="baseRef"/> (quando <paramref name="startIndex"/>
    /// é 0). Para no primeiro conflito, devolvendo o commit pendente e o índice para
    /// retomada após a resolução manual (via <see cref="ContinueReplicationAsync"/>).
    /// </summary>
    Task<BranchReplicationResult> ReplicateBranchAsync(
        string repositoryPath,
        IReadOnlyList<GitCommit> commits,
        int startIndex,
        string newBranch,
        string baseRef,
        ReplicationMode mode,
        CancellationToken ct = default);

    /// <summary>
    /// Conclui uma replicação após o usuário resolver os conflitos manualmente:
    /// estagia os arquivos resolvidos e finaliza (cherry-pick --continue ou commit).
    /// </summary>
    Task<ReplicationResult> ContinueReplicationAsync(
        string repositoryPath, GitCommit commit, ReplicationMode mode, CancellationToken ct = default);

    /// <summary>Lista os caminhos dos arquivos atualmente em conflito (não mesclados).</summary>
    Task<IReadOnlyList<string>> GetConflictedFilesAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Lista os arquivos em conflito com metadados (tipo de conflito) para exibição.
    /// </summary>
    Task<IReadOnlyList<ConflictEntry>> GetConflictsAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Extrai o conteúdo de um estágio do índice de um arquivo em conflito
    /// (1 = ancestral comum/base, 2 = nosso/destino, 3 = deles/origem) para
    /// <paramref name="destinationPath"/>, preservando os bytes do blob
    /// (codificação e arquivos binários intactos). Retorna o caminho gravado,
    /// ou null se o estágio não existir (ex.: conflito de adição sem ancestral).
    /// </summary>
    Task<string?> ExtractConflictStageAsync(
        string repositoryPath, string file, int stage, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Envia (<c>git push</c>) o branch informado para o remote <c>origin</c>,
    /// definindo o upstream quando solicitado.
    /// </summary>
    Task<GitCommandResult> PushAsync(string repositoryPath, string branch, bool setUpstream = true, CancellationToken ct = default);

    /// <summary>Cria (ou reposiciona) e faz checkout do branch (<c>checkout -B</c>).</summary>
    Task<GitCommandResult> CheckoutNewBranchAsync(string repositoryPath, string branch, CancellationToken ct = default);

    /// <summary>
    /// Estagia todas as alterações (<c>add -A</c>) e commita com a mensagem informada
    /// (via arquivo, preservando quebras de linha e caracteres especiais).
    /// </summary>
    Task<GitCommandResult> CommitAllAsync(string repositoryPath, string message, CancellationToken ct = default);

    /// <summary>
    /// Configura o <c>gh</c> como credential helper do git NESTE repositório (local),
    /// para que o <c>git push</c> em URLs HTTPS de <paramref name="host"/> (ex.:
    /// <c>github.com</c>) autentique com o token já logado no gh — evitando que o push
    /// falhe por falta de credenciais. Inócuo para remotes SSH.
    /// </summary>
    Task<GitCommandResult> ConfigureGhCredentialHelperAsync(string repositoryPath, string host, CancellationToken ct = default);
}

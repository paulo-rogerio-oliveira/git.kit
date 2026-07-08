using System.Text;
using GitKit.Core.Models;
using GitKit.Core.Services;
using Xunit;

namespace GitKit.Core.Tests;

public sealed class GitServiceTests
{
    private static GitService NewService() => new(new ProcessRunner());

    [Fact]
    public async Task GetBranches_returns_local_branches()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "hello", "primeiro");
        await repo.GitAsync("branch feature");

        var git = NewService();
        var branches = await git.GetBranchesAsync(repo.Path);

        Assert.Contains(branches, b => b.Name == "main");
        Assert.Contains(branches, b => b.Name == "feature");
        Assert.Contains(branches, b => b is { Name: "main", IsCurrent: true });
    }

    [Fact]
    public async Task GetBranches_local_branch_named_like_remote_is_not_remote()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");
        // Branch LOCAL cujo nome tem a forma "origin/...": a decisão remoto/local
        // deve vir do refname completo (refs/heads/...), não do prefixo do nome.
        await repo.GitAsync("branch origin/legacy");

        var git = NewService();
        var branches = await git.GetBranchesAsync(repo.Path);

        var branch = branches.Single(b => b.Name == "origin/legacy");
        Assert.False(branch.IsRemote);
    }

    [Fact]
    public async Task GetBranches_marks_remote_tracking_branches_as_remote()
    {
        using var source = await TestRepository.CreateAsync();
        await source.CommitFileAsync("a.txt", "x", "base");
        await source.GitAsync("branch feature/x");

        var temp = Path.Combine(Path.GetTempPath(), "git.kit-remotecls-" + Guid.NewGuid().ToString("N"));
        var git = NewService();
        try
        {
            await git.CloneAsync(source.Path, temp);
            var branches = await git.GetBranchesAsync(temp);

            Assert.Contains(branches, b => b is { Name: "origin/feature/x", IsRemote: true });
            Assert.Contains(branches, b => b is { Name: "main", IsRemote: false });
            // O ponteiro simbólico origin/HEAD não deve aparecer.
            Assert.DoesNotContain(branches, b => b.Name.EndsWith("/HEAD"));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DiffIntegration_preserves_commit_message_with_quotes_and_backslashes()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");
        await repo.GitAsync("branch destino");

        await repo.GitAsync("checkout -b origem");
        var subject = "corrige \"aspas\" e \\barra no assunto";
        var hash = await repo.CommitFileAsync("nova.txt", "conteudo", subject);
        await repo.GitAsync("checkout main");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, subject);

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.DiffIntegration);
        Assert.Equal(ReplicationStatus.Success, result.Status);

        // A mensagem (com aspas/barras) deve chegar íntegra ao commit de destino.
        var top = (await git.GetCommitsAsync(repo.Path, "destino"))[0];
        Assert.Equal(subject, top.Subject);
    }

    [Fact]
    public async Task GetCommits_returns_commits_of_branch()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "v1", "commit um");
        await repo.CommitFileAsync("a.txt", "v2", "commit dois");

        var git = NewService();
        var commits = await git.GetCommitsAsync(repo.Path, "main");

        Assert.Equal(2, commits.Count);
        Assert.Equal("commit dois", commits[0].Subject); // mais recente primeiro
        Assert.Equal("commit um", commits[1].Subject);
    }

    [Fact]
    public async Task Replicate_CherryPick_applies_commit_to_destination()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");

        // Branch de destino sem o arquivo de feature.
        await repo.GitAsync("branch destino");

        // Branch de origem com um novo arquivo.
        await repo.GitAsync("checkout -b origem");
        var commits0 = await NewService().GetCommitsAsync(repo.Path, "origem");
        var sourceCommitHash = await repo.CommitFileAsync("feature.txt", "nova feature", "adiciona feature");

        await repo.GitAsync("checkout main");

        var git = NewService();
        var commit = new GitCommit(sourceCommitHash, "autor", DateTimeOffset.Now, "adiciona feature");

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Path, "feature.txt")));

        // E o destino agora contém o commit.
        var destinoCommits = await git.GetCommitsAsync(repo.Path, "destino");
        Assert.Contains(destinoCommits, c => c.Subject.Contains("adiciona feature"));
    }

    [Fact]
    public async Task Replicate_DiffIntegration_applies_changes_to_destination()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");
        await repo.GitAsync("branch destino");

        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("nova.txt", "conteudo novo", "feature via diff");
        await repo.GitAsync("checkout main");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "feature via diff");

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.DiffIntegration);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Path, "nova.txt")));
    }

    [Fact]
    public async Task Replicate_CherryPick_with_conflict_requests_manual_resolution()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");

        // Destino altera o arquivo de uma forma.
        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "alterado no destino\n", "muda no destino");

        // Origem altera a MESMA linha de outra forma.
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var conflictHash = await repo.CommitFileAsync("shared.txt", "alterado na origem\n", "muda na origem");

        var git = NewService();
        var commit = new GitCommit(conflictHash, "autor", DateTimeOffset.Now, "muda na origem");

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, result.Status);
        Assert.True(result.RequiresManualResolution);

        // O arquivo em conflito deve ser listado (alvo do TortoiseGitMerge).
        var conflicted = await git.GetConflictedFilesAsync(repo.Path);
        Assert.Contains("shared.txt", conflicted);

        // E as três versões (base/destino/origem) devem ser extraíveis para o merge.
        var dir = Path.Combine(Path.GetTempPath(), "git.kit-stages-" + Guid.NewGuid().ToString("N"));
        try
        {
            var basePath = await git.ExtractConflictStageAsync(repo.Path, "shared.txt", 1, Path.Combine(dir, "b.txt"));
            var minePath = await git.ExtractConflictStageAsync(repo.Path, "shared.txt", 2, Path.Combine(dir, "m.txt"));
            var theirsPath = await git.ExtractConflictStageAsync(repo.Path, "shared.txt", 3, Path.Combine(dir, "t.txt"));

            Assert.NotNull(basePath);
            Assert.NotNull(minePath);
            Assert.NotNull(theirsPath);
            Assert.Contains("original", await File.ReadAllTextAsync(basePath!));
            Assert.Contains("destino", await File.ReadAllTextAsync(minePath!));
            Assert.Contains("origem", await File.ReadAllTextAsync(theirsPath!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Replicate_CherryPick_already_present_reports_already_applied()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");

        // Origem adiciona um arquivo.
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("feature.txt", "conteudo", "adiciona feature");

        // Destino já contém EXATAMENTE a mesma mudança (cherry-pick anterior).
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b destino");
        await repo.CommitFileAsync("feature.txt", "conteudo", "mesma feature no destino");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "adiciona feature");

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.AlreadyApplied, result.Status);

        // E a árvore de trabalho ficou limpa (cherry-pick não pendente).
        var status = await repo.RunRawAsync("status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    [Fact]
    public async Task Replicate_to_existing_remote_branch_reports_local_branch_name()
    {
        // Repositório de origem com main, feature/2 e um branch de trabalho.
        using var source = await TestRepository.CreateAsync();
        await source.CommitFileAsync("base.txt", "base", "base");
        await source.GitAsync("branch feature/2");
        await source.GitAsync("checkout -b origem");
        var hash = await source.CommitFileAsync("feature.txt", "x", "feature");

        // Clona para uma cópia: branches viram origin/* (remote-tracking).
        var temp = Path.Combine(Path.GetTempPath(), "git.kit-remotebr-" + Guid.NewGuid().ToString("N"));
        var git = NewService();
        try
        {
            await git.CloneAsync(source.Path, temp);

            // Destino é o branch REMOTO origin/feature/2 (como ao selecioná-lo na UI).
            var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "feature");
            var result = await git.ReplicateCommitAsync(temp, commit, "origin/feature/2", ReplicationMode.CherryPick);

            Assert.Equal(ReplicationStatus.Success, result.Status);
            // O branch para push deve ser o LOCAL 'feature/2', não 'origin/feature/2'.
            Assert.Equal("feature/2", result.BranchName);

            // E o checkout atual é o branch local feature/2.
            var head = (await new ProcessRunner().RunAsync("git", "rev-parse --abbrev-ref HEAD", temp)).StandardOutput.Trim();
            Assert.Equal("feature/2", head);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Replicate_creates_new_branch_from_master_by_default()
    {
        using var repo = await TestRepository.CreateAsync();
        // main será nosso "master".
        await repo.CommitFileAsync("base.txt", "base", "base");
        await repo.GitAsync("branch master");

        // Commit de origem.
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("feature.txt", "x", "feature");
        await repo.GitAsync("checkout master");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "feature");

        // Branch de destino inexistente, sem sufixo 'dev' → base master.
        var result = await git.ReplicateCommitAsync(repo.Path, commit, "feature/nova", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.Contains("master", result.Message);

        var head = (await repo.RunRawAsync("rev-parse --abbrev-ref HEAD")).StandardOutput.Trim();
        Assert.Equal("feature/nova", head);
    }

    [Fact]
    public async Task Replicate_creates_new_branch_from_develop_when_name_ends_with_dev()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");
        await repo.GitAsync("branch master");
        // develop diverge do master com um arquivo próprio.
        await repo.GitAsync("checkout -b develop");
        await repo.CommitFileAsync("dev-only.txt", "dev", "marca develop");

        await repo.GitAsync("checkout master");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("feature.txt", "x", "feature");
        await repo.GitAsync("checkout master");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "feature");

        // Sufixo 'dev' → base develop.
        var result = await git.ReplicateCommitAsync(repo.Path, commit, "feature/x-dev", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.Contains("develop", result.Message);

        // O novo branch herdou o conteúdo de develop (dev-only.txt presente).
        Assert.True(File.Exists(Path.Combine(repo.Path, "dev-only.txt")));
    }

    [Fact]
    public async Task GetRemoteUrl_returns_origin_url()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");
        await repo.GitAsync("remote add origin https://example.com/meu/repo.git");

        var git = NewService();
        var url = await git.GetRemoteUrlAsync(repo.Path);

        Assert.Equal("https://example.com/meu/repo.git", url);
    }

    [Fact]
    public async Task GetRemoteUrl_returns_empty_when_no_remote()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");

        var git = NewService();
        var url = await git.GetRemoteUrlAsync(repo.Path);

        Assert.Equal(string.Empty, url);
    }

    [Fact]
    public async Task Clone_from_local_path_into_temp_keeps_original_untouched_and_replicates()
    {
        // Repositório "original" do usuário, posicionado num branch de trabalho.
        using var origin = await TestRepository.CreateAsync();
        await origin.CommitFileAsync("base.txt", "base", "base");
        await origin.GitAsync("branch destino");
        await origin.GitAsync("checkout -b origem");
        var hash = await origin.CommitFileAsync("feature.txt", "nova", "adiciona feature");
        // Usuário fica no branch 'origem' — não deve ser trocado.
        var originalHead = (await origin.RunRawAsync("rev-parse --abbrev-ref HEAD")).StandardOutput.Trim();
        Assert.Equal("origem", originalHead);

        // Clona o caminho local numa pasta temporária e opera nela.
        var temp = Path.Combine(Path.GetTempPath(), "git.kit-localclone-" + Guid.NewGuid().ToString("N"));
        var git = NewService();
        try
        {
            var clone = await git.CloneAsync(origin.Path, temp);
            Assert.True(clone.Success, clone.CombinedOutput);

            var branches = await git.GetBranchesAsync(temp);
            Assert.Contains(branches, b => b.Name is "origin/origem");
            Assert.Contains(branches, b => b.Name is "origin/destino");

            var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "adiciona feature");
            var result = await git.ReplicateCommitAsync(temp, commit, "origin/destino", ReplicationMode.CherryPick);
            Assert.Equal(ReplicationStatus.Success, result.Status);

            // O repositório original permaneceu no mesmo branch, intocado.
            var headAfter = (await origin.RunRawAsync("rev-parse --abbrev-ref HEAD")).StandardOutput.Trim();
            Assert.Equal("origem", headAfter);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CherryPick_resolve_conflict_then_push_updates_bare_remote()
    {
        // Reproduz o fluxo do cherry-pick com conflito: resolve, conclui e envia (push)
        // para o remote — o commit resolvido deve chegar ao repositório bare (remoto).
        var bareDir = Path.Combine(Path.GetTempPath(), "git.kit-cp-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bareDir);
        var runner = new ProcessRunner();
        await runner.RunAsync("git", "init --bare", bareDir);

        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "base\n", "base");
        // develop diverge alterando o arquivo.
        await repo.GitAsync("checkout -b develop");
        await repo.CommitFileAsync("shared.txt", "develop\n", "muda no develop");
        // feature altera a MESMA linha de outra forma (conflito com develop).
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b feature");
        var conflictHash = await repo.CommitFileAsync("shared.txt", "feature\n", "muda na feature");

        // Publica develop no remote bare (destino do cherry-pick).
        await repo.GitAsync("checkout main");
        await repo.GitAsync($"remote add origin \"{bareDir}\"");
        await repo.GitAsync("push origin develop");

        var git = NewService();
        var commit = new GitCommit(conflictHash, "autor", DateTimeOffset.Now, "muda na feature");

        // Cherry-pick do commit da feature sobre develop → conflito.
        var result = await git.ReplicateCommitAsync(repo.Path, commit, "develop", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, result.Status);

        // Usuário resolve e conclui.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shared.txt"), "resolvido\n");
        var concluded = await git.ContinueReplicationAsync(repo.Path, commit, ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.Success, concluded.Status);
        Assert.Equal("develop", concluded.BranchName);

        // Push do branch resolvido para o remote.
        var push = await git.PushAsync(repo.Path, concluded.BranchName);
        Assert.True(push.Success, push.CombinedOutput);

        try
        {
            // O remote bare deve conter o commit resolvido em develop.
            var top = await runner.RunAsync("git", "log develop -1 --format=%s", bareDir);
            Assert.Contains("muda na feature", top.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(bareDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessRunner_writes_standard_input_to_process()
    {
        // 'git hash-object --stdin' lê o stdin e imprime o hash do blob:
        // para "hello\n" o hash é conhecido e determinístico.
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("git", "hash-object --stdin", null, null, default, "hello\n");

        Assert.True(result.Success, result.CombinedOutput);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task CheckoutNewBranch_and_CommitAll_create_branch_and_commit()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base", "base");

        var git = NewService();

        var checkout = await git.CheckoutNewBranchAsync(repo.Path, "us/1234");
        Assert.True(checkout.Success, checkout.CombinedOutput);
        var head = (await repo.RunRawAsync("rev-parse --abbrev-ref HEAD")).StandardOutput.Trim();
        Assert.Equal("us/1234", head);

        // Altera arquivos e commita tudo com mensagem multi-linha padrão do agente.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "novo.txt"), "conteudo\n");
        var message = "Ab#1234 implementa a validação de login\n\ndetalhes adicionais";
        var commit = await git.CommitAllAsync(repo.Path, message);
        Assert.True(commit.Success, commit.CombinedOutput);

        var subject = (await repo.RunRawAsync("log -1 --format=%s")).StandardOutput.Trim();
        Assert.Equal("Ab#1234 implementa a validação de login", subject);

        var status = await repo.RunRawAsync("status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    [Fact]
    public async Task ConfigureGhCredentialHelper_sets_local_helper_for_host()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");

        var git = NewService();
        var result = await git.ConfigureGhCredentialHelperAsync(repo.Path, "github.com");
        Assert.True(result.Success, result.CombinedOutput);

        // O helper do gh deve ficar configurado localmente para o host.
        var helpers = await repo.RunRawAsync("config --local --get-all credential.https://github.com.helper");
        Assert.True(helpers.Success, helpers.CombinedOutput);
        Assert.Contains("gh auth git-credential", helpers.StandardOutput);
    }

    [Fact]
    public async Task Push_sends_branch_to_origin()
    {
        // "Remote" como repositório bare.
        var bareDir = Path.Combine(Path.GetTempPath(), "git.kit-bare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bareDir);
        var runner = new ProcessRunner();
        await runner.RunAsync("git", "init --bare", bareDir);

        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");
        await repo.GitAsync($"remote add origin \"{bareDir}\"");
        await repo.GitAsync("checkout -b feature/enviar");
        await repo.CommitFileAsync("b.txt", "y", "novo no branch");

        var git = NewService();
        try
        {
            var result = await git.PushAsync(repo.Path, "feature/enviar");
            Assert.True(result.Success, result.CombinedOutput);

            // O branch deve existir no remote bare.
            var refs = await runner.RunAsync("git", "branch --list feature/enviar", bareDir);
            Assert.Contains("feature/enviar", refs.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(bareDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Local_clone_push_targets_real_remote_of_source_repo()
    {
        // "Remote real" (servidor) como repositório bare.
        var bareDir = Path.Combine(Path.GetTempPath(), "git.kit-realremote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bareDir);
        var runner = new ProcessRunner();
        await runner.RunAsync("git", "init --bare", bareDir);

        // Repositório de origem do usuário, com remote apontando para o bare
        // e um branch LOCAL que ainda não existe no remote.
        using var source = await TestRepository.CreateAsync();
        await source.CommitFileAsync("base.txt", "base", "base");
        await source.GitAsync($"remote add origin \"{bareDir}\"");
        await source.GitAsync("checkout -b feature/x");
        await source.CommitFileAsync("f.txt", "feat", "commit de feature");

        var git = NewService();
        var realUrl = await git.GetRemoteUrlAsync(source.Path);
        Assert.Equal(bareDir, realUrl);

        // App: clona o caminho local em uma pasta temporária (origin = caminho local)...
        var temp = Path.Combine(Path.GetTempPath(), "git.kit-temp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await git.CloneAsync(source.Path, temp);

            // ...e reaponta o origin para o remote REAL do repositório originário.
            await git.SetRemoteUrlAsync(temp, realUrl);
            Assert.Equal(realUrl, await git.GetRemoteUrlAsync(temp));

            // Cria o branch local a partir do que veio do clone e faz push.
            await runner.RunAsync("git", "checkout -b feature/x origin/feature/x", temp);
            var push = await git.PushAsync(temp, "feature/x");
            Assert.True(push.Success, push.CombinedOutput);

            // O branch chegou no remote REAL (bare), não no caminho local.
            var refs = await runner.RunAsync("git", "branch --list feature/x", bareDir);
            Assert.Contains("feature/x", refs.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            try { Directory.Delete(bareDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TortoiseArguments_quote_only_the_value_so_spaces_survive()
    {
        const string merged = @"C:\Users\Paulo Rogerio\AppData\Local\Temp\git.kit\README.md";

        var proc = TortoiseGitArguments.BuildProc("conflicteditor", merged);
        // O caminho deve aparecer entre aspas como VALOR (não o argumento inteiro).
        Assert.Equal($"/command:conflicteditor /path:\"{merged}\"", proc);
        Assert.DoesNotContain("\"/path", proc);

        var args = TortoiseGitArguments.BuildMerge(
            @"C:\a b\base.txt", @"C:\a b\mine.txt", @"C:\a b\theirs.txt", merged,
            mergedName: "README.md");

        Assert.Contains("/base:\"C:\\a b\\base.txt\"", args);
        Assert.Contains("/mine:\"C:\\a b\\mine.txt\"", args);
        Assert.Contains("/theirs:\"C:\\a b\\theirs.txt\"", args);
        Assert.Contains($"/merged:\"{merged}\"", args);
        Assert.Contains("/mergedname:\"README.md\"", args);
        // Nenhuma aspa deve preceder a barra do argumento.
        Assert.DoesNotContain("\"/", args);
    }

    [Fact]
    public async Task ExtractConflictStage_writes_correct_content_with_spaces_in_repo_path()
    {
        // Repositório em caminho COM ESPAÇO, reproduzindo o cenário real do %TEMP%.
        using var repo = await TestRepository.CreateAsync(withSpaceInPath: true);
        Assert.Contains(" ", repo.Path);

        await repo.CommitFileAsync("README.md", "linha original do readme\n", "base");
        await repo.GitAsync("branch destino");

        // destino altera a linha.
        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("README.md", "conteudo do DESTINO\n", "muda destino");

        // origem altera a mesma linha de outra forma.
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("README.md", "conteudo da ORIGEM\n", "muda origem");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");
        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, result.Status);

        var dir = Path.Combine(repo.Path, "stages");
        var basePath = await git.ExtractConflictStageAsync(repo.Path, "README.md", 1, Path.Combine(dir, "base.txt"));
        var minePath = await git.ExtractConflictStageAsync(repo.Path, "README.md", 2, Path.Combine(dir, "mine.txt"));
        var theirsPath = await git.ExtractConflictStageAsync(repo.Path, "README.md", 3, Path.Combine(dir, "theirs.txt"));

        Assert.NotNull(basePath);
        Assert.NotNull(minePath);
        Assert.NotNull(theirsPath);

        // O CONTEÚDO de cada versão deve ser o do README — não lixo nem outro arquivo.
        static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        Assert.Equal("linha original do readme", Norm(await File.ReadAllTextAsync(basePath!)));
        Assert.Equal("conteudo do DESTINO", Norm(await File.ReadAllTextAsync(minePath!)));
        Assert.Equal("conteudo da ORIGEM", Norm(await File.ReadAllTextAsync(theirsPath!)));
    }

    [Fact]
    public async Task GetConflicts_returns_entry_with_metadata()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");
        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "destino\n", "muda destino");
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("shared.txt", "origem\n", "muda origem");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");
        await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);

        var conflicts = await git.GetConflictsAsync(repo.Path);

        var entry = Assert.Single(conflicts);
        Assert.Equal("shared.txt", entry.Path);
        Assert.Equal("UU", entry.Code);
        Assert.Equal("Ambos modificaram", entry.Description);
    }

    [Fact]
    public async Task Continue_after_resolving_conflict_creates_the_commit()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");

        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "alterado no destino\n", "muda destino");

        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("shared.txt", "alterado na origem\n", "muda origem");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");

        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, result.Status);

        var commitsBefore = (await git.GetCommitsAsync(repo.Path, "destino")).Count;

        // Usuário resolve o conflito (simulado escrevendo o arquivo final).
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shared.txt"), "linha resolvida manualmente\n");

        // Conclui a replicação: deve estagiar, continuar o cherry-pick e commitar.
        var concluded = await git.ContinueReplicationAsync(repo.Path, commit, ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, concluded.Status);
        Assert.Equal("destino", concluded.BranchName);

        // Há um novo commit em destino e a árvore de trabalho está limpa.
        var commitsAfter = (await git.GetCommitsAsync(repo.Path, "destino")).Count;
        Assert.Equal(commitsBefore + 1, commitsAfter);

        var status = await repo.RunRawAsync("status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));

        var content = await File.ReadAllTextAsync(Path.Combine(repo.Path, "shared.txt"));
        Assert.Contains("resolvida manualmente", content);
    }

    [Fact]
    public async Task Continue_after_conflict_preserves_original_message_and_author()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");

        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "alterado no destino\n", "muda destino");

        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        // Assunto começando com '#' (número de issue) e autor distinto: o cleanup=strip
        // do 'cherry-pick --continue' apagaria a linha, deixando só o trailer.
        var subject = "#1234 corrige bug importante";
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shared.txt"), "alterado na origem\n");
        await repo.GitAsync("add shared.txt");
        var msgFile = Path.Combine(repo.Path, ".git", "MSG_ORIG.txt");
        await File.WriteAllTextAsync(msgFile, subject + "\n\ncorpo detalhado\n");
        await repo.RunAsync($"commit --author=\"Autor Original <autor@orig.com>\" -F \"{msgFile}\"");
        var hash = (await repo.RunAsync("rev-parse HEAD")).StandardOutput.Trim();

        var git = NewService();
        var commit = new GitCommit(hash, "Autor Original", DateTimeOffset.Now, subject);

        var conflict = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, conflict.Status);

        // Usuário resolve o conflito.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shared.txt"), "resolvido manualmente\n");

        var result = await git.ContinueReplicationAsync(repo.Path, commit, ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.Success, result.Status);

        // A mensagem original (com '#') deve ser preservada e o trailer -x referenciado.
        var subjectAfter = (await repo.RunAsync($"log -1 --format=%s destino")).StandardOutput.Trim();
        Assert.Equal(subject, subjectAfter);
        var body = (await repo.RunAsync("log -1 --format=%B destino")).StandardOutput;
        Assert.Contains("corpo detalhado", body);
        Assert.Contains($"(cherry picked from commit {hash})", body);

        // A autoria original deve ser mantida.
        var author = (await repo.RunAsync("log -1 --format=%an destino")).StandardOutput.Trim();
        Assert.Equal("Autor Original", author);
    }

    [Fact]
    public async Task Continue_when_resolution_matches_destination_reports_already_applied()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");
        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "conteudo do destino\n", "muda destino");
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("shared.txt", "conteudo da origem\n", "muda origem");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");
        var conflict = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, conflict.Status);

        var commitsBefore = (await git.GetCommitsAsync(repo.Path, "destino")).Count;

        // Usuário resolve mantendo EXATAMENTE o conteúdo do destino (descarta a origem).
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shared.txt"), "conteudo do destino\n");

        var result = await git.ContinueReplicationAsync(repo.Path, commit, ReplicationMode.CherryPick);

        // Resultado idêntico ao destino => nada a commitar, mas não é erro.
        Assert.Equal(ReplicationStatus.AlreadyApplied, result.Status);
        Assert.Equal("destino", result.BranchName);

        // Nenhum commit novo e árvore limpa (cherry-pick foi encerrado).
        var commitsAfter = (await git.GetCommitsAsync(repo.Path, "destino")).Count;
        Assert.Equal(commitsBefore, commitsAfter);
        var status = await repo.RunRawAsync("status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    [Fact]
    public async Task Continue_with_unresolved_conflict_does_not_commit()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("shared.txt", "linha original\n", "base");
        await repo.GitAsync("branch destino");
        await repo.GitAsync("checkout destino");
        await repo.CommitFileAsync("shared.txt", "destino\n", "muda destino");
        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        var hash = await repo.CommitFileAsync("shared.txt", "origem\n", "muda origem");

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");
        await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);

        // Sem resolver (arquivo ainda com marcadores de conflito), tentar concluir falha.
        var concluded = await git.ContinueReplicationAsync(repo.Path, commit, ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, concluded.Status);
    }

    [Fact]
    public async Task GetCommits_skip_paginates_history()
    {
        using var repo = await TestRepository.CreateAsync();
        for (var i = 1; i <= 5; i++)
            await repo.CommitFileAsync("a.txt", $"v{i}", $"commit {i}");

        var git = NewService();
        var page1 = await git.GetCommitsAsync(repo.Path, "main", max: 2);
        var page2 = await git.GetCommitsAsync(repo.Path, "main", max: 2, skip: 2);
        var page3 = await git.GetCommitsAsync(repo.Path, "main", max: 2, skip: 4);

        Assert.Equal(new[] { "commit 5", "commit 4" }, page1.Select(c => c.Subject));
        Assert.Equal(new[] { "commit 3", "commit 2" }, page2.Select(c => c.Subject));
        Assert.Equal(new[] { "commit 1" }, page3.Select(c => c.Subject));
    }

    [Fact]
    public async Task SearchCommits_matches_message_body_and_author_case_insensitively()
    {
        using var repo = await TestRepository.CreateAsync();
        // Termo apenas no CORPO da mensagem (o filtro local da UI só vê o assunto).
        await repo.CommitFileAsync("a.txt", "v1", "primeiro commit\n\ncorpo com TICKET-123 referenciado");
        await repo.CommitFileAsync("a.txt", "v2", "segundo commit");
        // Commit com autor distinto.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "v3");
        await repo.GitAsync("add a.txt");
        await repo.GitAsync("commit --author=\"Maria Souza <maria@exemplo.com>\" -m \"terceiro commit\"");

        var git = NewService();

        var byBody = await git.SearchCommitsAsync(repo.Path, "main", "ticket-123");
        var found = Assert.Single(byBody);
        Assert.Equal("primeiro commit", found.Subject);

        var byAuthor = await git.SearchCommitsAsync(repo.Path, "main", "maria");
        var author = Assert.Single(byAuthor);
        Assert.Equal("terceiro commit", author.Subject);

        Assert.Empty(await git.SearchCommitsAsync(repo.Path, "main", "termo-inexistente-xyz"));
    }

    [Fact]
    public async Task ExtractConflictStage_preserves_exact_bytes_of_non_utf8_content()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.GitAsync("config core.autocrlf false");

        // Conteúdo UTF-16 LE com BOM: capturar via stdout (implementação antiga)
        // corromperia os bytes; a extração deve devolver o blob intacto.
        static byte[] Utf16(string text)
            => new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var file = Path.Combine(repo.Path, "dados.txt");
        var mineBytes = Utf16("conteúdo do DESTINO\r\n");
        var theirsBytes = Utf16("conteúdo da ORIGEM\r\n");

        await File.WriteAllBytesAsync(file, Utf16("linha original\r\n"));
        await repo.GitAsync("add dados.txt");
        await repo.GitAsync("commit -m base");
        await repo.GitAsync("branch destino");

        await repo.GitAsync("checkout destino");
        await File.WriteAllBytesAsync(file, mineBytes);
        await repo.GitAsync("add dados.txt");
        await repo.GitAsync("commit -m \"muda destino\"");

        await repo.GitAsync("checkout main");
        await repo.GitAsync("checkout -b origem");
        await File.WriteAllBytesAsync(file, theirsBytes);
        await repo.GitAsync("add dados.txt");
        await repo.GitAsync("commit -m \"muda origem\"");
        var hash = (await repo.RunAsync("rev-parse HEAD")).StandardOutput.Trim();

        var git = NewService();
        var commit = new GitCommit(hash, "autor", DateTimeOffset.Now, "muda origem");
        var result = await git.ReplicateCommitAsync(repo.Path, commit, "destino", ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, result.Status);

        var dir = Path.Combine(Path.GetTempPath(), "git.kit-bytes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var minePath = await git.ExtractConflictStageAsync(repo.Path, "dados.txt", 2, Path.Combine(dir, "mine.txt"));
            var theirsPath = await git.ExtractConflictStageAsync(repo.Path, "dados.txt", 3, Path.Combine(dir, "theirs.txt"));

            Assert.NotNull(minePath);
            Assert.NotNull(theirsPath);
            Assert.Equal(mineBytes, await File.ReadAllBytesAsync(minePath!));
            Assert.Equal(theirsBytes, await File.ReadAllBytesAsync(theirsPath!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessRunner_cancellation_kills_git_process()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("a.txt", "x", "base");

        // Contrato de cancelamento: o token cancelado gera OperationCanceledException
        // (o processo é morto, sem resultado parcial). Token já cancelado é a forma
        // determinística de exercitar esse caminho.
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("git", "log", repo.Path, null, cts.Token));
    }

    [Fact]
    public async Task IsRepository_detects_non_repo()
    {
        var temp = Path.Combine(Path.GetTempPath(), "gitkit-notrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var git = NewService();
            Assert.False(await git.IsRepositoryAsync(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // ----- Replicação de branch (todos os commits + regra de target) -----

    /// <summary>
    /// Monta um repo com master/develop e dois branches: um filho de master e outro
    /// filho de develop. Retorna o repo pronto para os testes de branch.
    /// </summary>
    private static async Task<TestRepository> CreateGitflowRepoAsync()
    {
        var repo = await TestRepository.CreateAsync();
        // c1 na base, que vira master.
        await repo.CommitFileAsync("conflict.txt", "base\n", "c1 base");
        await repo.GitAsync("branch master");

        // develop = master + um commit próprio.
        await repo.GitAsync("checkout -b develop master");
        await repo.CommitFileAsync("conflict.txt", "develop version\n", "develop muda conflict");

        // feature-master = filho de master (não contém o commit de develop).
        await repo.GitAsync("checkout -b feature-master master");
        await repo.CommitFileAsync("a.txt", "AAA\n", "feat a");
        await repo.CommitFileAsync("conflict.txt", "feature version\n", "feat muda conflict");
        await repo.CommitFileAsync("c.txt", "CCC\n", "feat c");

        // feature-develop = filho de develop.
        await repo.GitAsync("checkout -b feature-develop develop");
        await repo.CommitFileAsync("d.txt", "DDD\n", "feat d");

        await repo.GitAsync("checkout master");
        return repo;
    }

    [Fact]
    public async Task ListCommitsBetween_returns_source_only_commits_oldest_first()
    {
        using var repo = await CreateGitflowRepoAsync();
        var git = NewService();

        var commits = await git.ListCommitsBetweenAsync(repo.Path, "develop", "feature-master");

        // develop..feature-master = os 3 commits da feature (nenhum deles em develop).
        Assert.Equal(3, commits.Count);
        Assert.Equal("feat a", commits[0].Subject);   // mais antigo primeiro
        Assert.Equal("feat muda conflict", commits[1].Subject);
        Assert.Equal("feat c", commits[2].Subject);
    }

    [Fact]
    public async Task ReplicateBranch_happy_path_applies_all_commits_onto_new_branch()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.CommitFileAsync("base.txt", "base\n", "base");
        await repo.GitAsync("branch develop");

        // Branch de origem com dois commits limpos (arquivos distintos).
        await repo.GitAsync("checkout -b feature");
        await repo.CommitFileAsync("f1.txt", "um\n", "feat 1");
        await repo.CommitFileAsync("f2.txt", "dois\n", "feat 2");
        await repo.GitAsync("checkout develop");

        var git = NewService();
        var commits = await git.ListCommitsBetweenAsync(repo.Path, "develop", "feature");
        Assert.Equal(2, commits.Count);

        var result = await git.ReplicateBranchAsync(
            repo.Path, commits, 0, "feature-replicado", "develop", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.Equal(2, result.Replicated);
        Assert.Equal("feature-replicado", result.BranchName);

        var head = (await repo.RunRawAsync("rev-parse --abbrev-ref HEAD")).StandardOutput.Trim();
        Assert.Equal("feature-replicado", head);
        Assert.True(File.Exists(Path.Combine(repo.Path, "f1.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "f2.txt")));
    }

    [Fact]
    public async Task ReplicateBranch_conflict_in_middle_pauses_then_resumes_to_completion()
    {
        using var repo = await CreateGitflowRepoAsync();
        var git = NewService();

        // Replica todos os commits de feature-master (a, conflito, c) sobre develop.
        var commits = await git.ListCommitsBetweenAsync(repo.Path, "develop", "feature-master");
        Assert.Equal(3, commits.Count);

        var first = await git.ReplicateBranchAsync(
            repo.Path, commits, 0, "repl", "develop", ReplicationMode.CherryPick);

        // Para no 2º commit (índice 1), que altera conflict.txt já mexido em develop.
        Assert.Equal(ReplicationStatus.ConflictsNeedManualResolution, first.Status);
        Assert.NotNull(first.PendingCommit);
        Assert.Equal(1, first.NextIndex);
        Assert.Equal(1, first.Replicated);
        Assert.Equal("feat muda conflict", first.PendingCommit!.Subject);

        // O 1º commit (a.txt) já foi aplicado antes do conflito.
        Assert.True(File.Exists(Path.Combine(repo.Path, "a.txt")));

        // Usuário resolve o conflito (simulado) e conclui esse commit.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "conflict.txt"), "resolvido\n");
        var continued = await git.ContinueReplicationAsync(repo.Path, first.PendingCommit!, ReplicationMode.CherryPick);
        Assert.Equal(ReplicationStatus.Success, continued.Status);

        // Retoma a partir do próximo commit (c.txt).
        var resumed = await git.ReplicateBranchAsync(
            repo.Path, commits, first.NextIndex + 1, "repl", "develop", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, resumed.Status);
        Assert.Equal(1, resumed.Replicated);

        // Estado final: todos os arquivos presentes e conflito resolvido.
        Assert.True(File.Exists(Path.Combine(repo.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "c.txt")));
        Assert.Contains("resolvido", await File.ReadAllTextAsync(Path.Combine(repo.Path, "conflict.txt")));

        var status = await repo.RunRawAsync("status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    [Fact]
    public async Task ReplicateBranch_works_with_space_in_repo_path()
    {
        using var repo = await TestRepository.CreateAsync(withSpaceInPath: true);
        Assert.Contains(" ", repo.Path);

        await repo.CommitFileAsync("base.txt", "base\n", "base");
        await repo.GitAsync("branch develop");
        await repo.GitAsync("checkout -b feature");
        await repo.CommitFileAsync("nova.txt", "conteudo\n", "feat nova");
        await repo.GitAsync("checkout develop");

        var git = NewService();
        var commits = await git.ListCommitsBetweenAsync(repo.Path, "develop", "feature");
        var result = await git.ReplicateBranchAsync(
            repo.Path, commits, 0, "feature-replicado", "develop", ReplicationMode.CherryPick);

        Assert.Equal(ReplicationStatus.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Path, "nova.txt")));
    }
}

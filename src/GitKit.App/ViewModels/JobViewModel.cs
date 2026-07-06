using System.Windows;
using GitKit.App.MVVM;
using GitKit.Core.Models;

namespace GitKit.App.ViewModels;

/// <summary>Tipo de processo em background.</summary>
public enum JobKind
{
    /// <summary>Replicação de TODOS os commits de um branch + criação de PR.</summary>
    BranchReplication,

    /// <summary>Cherry-pick dos commits selecionados para um branch alvo.</summary>
    CherryPick,
}

/// <summary>Situação de um processo em background.</summary>
public enum JobStatus
{
    Queued,
    Running,
    NeedsConflictResolution,
    ReadyToPush,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Um processo em background (clone + replicação) exibido na aba "Processos".
/// Carrega tanto o estado observável (para a UI) quanto o contexto necessário
/// para retomar após uma resolução de conflitos.
/// </summary>
public sealed class JobViewModel : ObservableObject
{
    public JobViewModel(JobKind kind, string title)
    {
        Kind = kind;
        _title = title;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public JobKind Kind { get; }

    /// <summary>Cancelamento próprio deste processo (padrão do RunBusyAsync original).</summary>
    public CancellationTokenSource Cts { get; } = new();

    private string _title;
    public string Title
    {
        get => _title;
        set => SetOnUi(ref _title, value);
    }

    public string KindLabel => Kind == JobKind.BranchReplication ? "Replicar branch" : "Cherry-pick";

    private JobStatus _status = JobStatus.Queued;
    public JobStatus Status
    {
        get => _status;
        private set
        {
            if (SetOnUi(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(NeedsResolution));
                OnPropertyChanged(nameof(CanRecover));
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public string StatusLabel => Status switch
    {
        JobStatus.Queued => "Na fila",
        JobStatus.Running => "Em execução",
        JobStatus.NeedsConflictResolution => "Conflito — resolver",
        JobStatus.ReadyToPush => "Pronto para enviar (push)",
        JobStatus.Completed => "Concluído",
        JobStatus.Failed => "Falhou",
        JobStatus.Canceled => "Cancelado",
        _ => Status.ToString(),
    };

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetOnUi(ref _statusText, value);
    }

    private string _pullRequestUrl = string.Empty;
    public string PullRequestUrl
    {
        get => _pullRequestUrl;
        private set
        {
            if (SetOnUi(ref _pullRequestUrl, value))
                OnPropertyChanged(nameof(HasPullRequest));
        }
    }

    public bool HasPullRequest => !string.IsNullOrWhiteSpace(PullRequestUrl);
    public bool CanCancel => Status is JobStatus.Queued or JobStatus.Running;
    public bool NeedsResolution => Status == JobStatus.NeedsConflictResolution;
    /// <summary>Pode ser trazido de volta à tela: para resolver conflitos ou para enviar (push).</summary>
    public bool CanRecover => Status is JobStatus.NeedsConflictResolution or JobStatus.ReadyToPush;
    public bool IsFinished => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    // ----- Contexto de execução/retomada (não vinculado diretamente à UI) -----

    /// <summary>Origem do clone: URL GitHub ou caminho local do repositório.</summary>
    public string RepositoryUrl { get; init; } = string.Empty;
    /// <summary>Remote real para push/PR (pode diferir de <see cref="RepositoryUrl"/> em repos locais).</summary>
    public string RemoteUrl { get; init; } = string.Empty;
    /// <summary>True quando a origem informada é um caminho local.</summary>
    public bool IsLocalSource { get; init; }
    public GitHubRepo? GhRepo { get; init; }
    public string SourceBranch { get; init; } = string.Empty;
    public ReplicationMode Mode { get; init; } = ReplicationMode.CherryPick;

    /// <summary>Pasta de trabalho temporária do clone (definida ao clonar).</summary>
    public string WorkingDir { get; set; } = string.Empty;

    // Replicação de branch:
    public string NewBranch { get; set; } = string.Empty;
    /// <summary>Alvo escolhido na tela (develop/master), quando já resolvido via gh.</summary>
    public string? RequestedTarget { get; init; }
    public string? TargetBaseRef { get; set; }
    public string? TargetBranchName { get; set; }
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public string PrTitle { get; set; } = string.Empty;
    public string PrBody { get; set; } = string.Empty;

    // Cherry-pick:
    public string TargetBranch { get; init; } = string.Empty;

    // Estado do laço de commits (compartilhado):
    public IReadOnlyList<GitCommit> Commits { get; set; } = Array.Empty<GitCommit>();
    public int NextIndex { get; set; }
    public GitCommit? PendingCommit { get; set; }

    // ----- Destino do push (upstream) -----

    /// <summary>Branch local que será enviado: novo branch (replicação) ou alvo (cherry-pick).</summary>
    public string PushBranch => Kind == JobKind.BranchReplication
        ? NewBranch
        : (string.IsNullOrWhiteSpace(TargetBranchName) ? TargetBranch : TargetBranchName!);

    /// <summary>Descrição legível do upstream para onde o push será enviado.</summary>
    public string PushUpstream
    {
        get
        {
            var branch = PushBranch;
            if (string.IsNullOrWhiteSpace(branch))
                return "—";
            var remote = string.IsNullOrWhiteSpace(RemoteUrl) ? RepositoryUrl : RemoteUrl;
            return string.IsNullOrWhiteSpace(remote)
                ? $"origin/{branch}"
                : $"origin/{branch}  ({remote})";
        }
    }

    /// <summary>Notifica a UI de que o destino do push pode ter mudado (thread-safe).</summary>
    public void NotifyPushTargetChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(PushBranch));
            OnPropertyChanged(nameof(PushUpstream));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Raise();
        else
            dispatcher.Invoke(Raise);
    }

    // ----- Transições de estado (sempre marshalladas para a UI thread) -----

    public void MarkRunning(string text) => Apply(JobStatus.Running, text);
    public void Report(string text) => StatusText = text;
    public void MarkConflict(string text) => Apply(JobStatus.NeedsConflictResolution, text);
    public void MarkReadyToPush(string text) => Apply(JobStatus.ReadyToPush, text);
    public void MarkFailed(string text) => Apply(JobStatus.Failed, text);
    public void MarkCanceled(string text) => Apply(JobStatus.Canceled, text);

    public void MarkCompleted(string text, string prUrl = "")
    {
        PullRequestUrl = prUrl;
        Apply(JobStatus.Completed, text);
    }

    private void Apply(JobStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    // Atualiza a propriedade na thread da UI (o job roda em background).
    private bool SetOnUi<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            OnPropertyChanged(name);
        else
            dispatcher.Invoke(() => OnPropertyChanged(name));
        return true;
    }
}

using System.ComponentModel;
using GitKit.App.MVVM;
using GitKit.App.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// ViewModel do popup de um processo do agente: exibe o transcript (console),
/// permite responder ao agente por texto, solicitar/aprovar o commit padrão
/// (<c>Ab#{id} ...</c>) e enviar o branch (push). A UI é uma casca para o CLI.
/// </summary>
public sealed class AgentSessionViewModel : ObservableObject
{
    private readonly BackgroundJobService _jobs;

    public AgentSessionViewModel(BackgroundJobService jobs, JobViewModel job)
    {
        _jobs = jobs;
        Job = job;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        RequestCommitCommand = new AsyncRelayCommand(RequestCommitAsync,
            () => !IsBusy && Job.Status == JobStatus.WaitingForInput);
        ApproveCommitCommand = new AsyncRelayCommand(ApproveCommitAsync,
            () => !IsBusy && Job.HasCommitProposal && !string.IsNullOrWhiteSpace(CommitMessage));
        PushCommand = new AsyncRelayCommand(PushAsync, () => !IsBusy && Job.Status == JobStatus.ReadyToPush);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));

        _commitMessage = job.ProposedCommitMessage;
        Job.PropertyChanged += OnJobPropertyChanged;
    }

    /// <summary>O processo em background (status/transcript ao vivo).</summary>
    public JobViewModel Job { get; }

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand RequestCommitCommand { get; }
    public AsyncRelayCommand ApproveCommitCommand { get; }
    public AsyncRelayCommand PushCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>Disparado quando o popup deve ser fechado.</summary>
    public event EventHandler? RequestClose;

    // ----- Estado -----

    private string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    // Mensagem de commit em edição (inicia com a proposta do agente).
    private string _commitMessage;
    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>O agente está aguardando texto do dev.</summary>
    public bool CanInteract => Job.Status == JobStatus.WaitingForInput;

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(JobViewModel.Status):
                OnPropertyChanged(nameof(CanInteract));
                break;
            case nameof(JobViewModel.ProposedCommitMessage):
                // Puxa a proposta do agente para o campo editável.
                CommitMessage = Job.ProposedCommitMessage;
                break;
        }
    }

    // ----- Ações -----

    private bool CanSend()
        => !IsBusy && Job.Status == JobStatus.WaitingForInput && !string.IsNullOrWhiteSpace(InputText);

    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
            return;

        try
        {
            IsBusy = true;
            InputText = string.Empty;
            await _jobs.SendToAgentAsync(Job, text);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RequestCommitAsync()
    {
        try
        {
            IsBusy = true;
            await _jobs.RequestCommitAsync(Job);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApproveCommitAsync()
    {
        try
        {
            IsBusy = true;
            await _jobs.ApproveCommitAsync(Job, CommitMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PushAsync()
    {
        try
        {
            IsBusy = true;
            if (await _jobs.FinishAsync(Job))
                RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

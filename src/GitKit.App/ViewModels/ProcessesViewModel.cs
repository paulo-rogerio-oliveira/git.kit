using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using GitKit.App.MVVM;
using GitKit.App.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// Aba "Processos": lista os jobs em background. Cada linha traz o indicativo de
/// status e os botões de recuperar (abre a tela de resolução/envio) e cancelar.
/// </summary>
public sealed class ProcessesViewModel : ObservableObject
{
    private readonly BackgroundJobService _service;
    private readonly Func<JobViewModel, Task> _recover;

    public ProcessesViewModel(BackgroundJobService service, Func<JobViewModel, Task> recover)
    {
        _service = service;
        _recover = recover;

        // "Abrir" está disponível para qualquer processo: sempre dá para ver os dados.
        RecoverCommand = new AsyncRelayCommand<JobViewModel>(
            job => job is null ? Task.CompletedTask : _recover(job),
            job => job is not null);
        CancelCommand = new RelayCommand<JobViewModel>(
            job => { if (job is not null) _service.Cancel(job); },
            job => job is { CanCancel: true });

        Jobs.CollectionChanged += OnJobsChanged;
        foreach (var job in Jobs)
            job.PropertyChanged += OnJobPropertyChanged;
        UpdateSummary();
    }

    public ObservableCollection<JobViewModel> Jobs => _service.Jobs;

    public AsyncRelayCommand<JobViewModel> RecoverCommand { get; }
    public RelayCommand<JobViewModel> CancelCommand { get; }

    private string _summary = "Nenhum processo em background.";
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (JobViewModel job in e.NewItems)
                job.PropertyChanged += OnJobPropertyChanged;
        if (e.OldItems is not null)
            foreach (JobViewModel job in e.OldItems)
                job.PropertyChanged -= OnJobPropertyChanged;

        UpdateSummary();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobViewModel.Status))
        {
            UpdateSummary();
            // Reavalia os botões de recuperar/cancelar das linhas.
            RecoverCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateSummary()
    {
        if (Jobs.Count == 0)
        {
            Summary = "Nenhum processo em background.";
            return;
        }

        var running = Jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Queued);
        var attention = Jobs.Count(j => j.Status is JobStatus.NeedsConflictResolution or JobStatus.ReadyToPush);
        Summary = $"{Jobs.Count} processo(s) — {running} em execução, {attention} aguardando ação.";
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;

namespace Cue.ViewModels;

/// <summary>Drives the live project and label lists in the left navigation pane.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IReorderService _reorder;

    public ObservableCollection<ProjectListItem> Projects { get; } = new();
    public ObservableCollection<LabelListItem> Labels { get; } = new();

    /// <summary>Open-task counts per project / label, refreshed on each <see cref="LoadAsync"/>.
    /// Read by the shell after a load to stamp navigation count badges.</summary>
    public IReadOnlyDictionary<Guid, int> ProjectTaskCounts { get; private set; } = new Dictionary<Guid, int>();
    public IReadOnlyDictionary<Guid, int> LabelTaskCounts { get; private set; } = new Dictionary<Guid, int>();

    public ShellViewModel(ITaskStore store, ITaskIndex index, IReorderService reorder)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var projects = await _index.GetProjectsAsync();
        var labels = await _index.GetLabelsAsync();
        ProjectTaskCounts = await _index.GetOpenTaskCountsByProjectAsync();
        LabelTaskCounts = await _index.GetOpenTaskCountsByLabelAsync();
        Replace(Projects, projects);
        Replace(Labels, labels);
    }

    [RelayCommand]
    private async Task CreateProjectAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _store.SaveAsync(new Project
        {
            Name = name.Trim(),
            SortOrder = _reorder.AppendRank(Projects.Select(project => project.SortOrder)),
        });
        await LoadAsync();
    }

    /// <summary>
    /// Reorders a project in the pane: moves it optimistically, persists the moved record's new rank
    /// through the rank service (only that record save for a rare rebalance), then reloads from the
    /// index so the persisted order — the source of truth — drives the pane.
    /// </summary>
    [RelayCommand]
    public async Task ReorderProjectAsync(ReorderRequest request)
    {
        if (request.OldIndex == request.NewIndex || (uint)request.NewIndex >= Projects.Count) return;
        Projects.Move(request.OldIndex, request.NewIndex);
        var moved = Projects[request.NewIndex];
        var ordered = Projects.Select(project => new RankedItem(project.Id, project.SortOrder)).ToList();
        try { await _reorder.MoveAsync<Project>(moved.Id, ordered); }
        finally { await LoadAsync(); }
    }

    /// <summary>Reorders a label in the pane. Mirrors <see cref="ReorderProjectAsync"/>.</summary>
    [RelayCommand]
    public async Task ReorderLabelAsync(ReorderRequest request)
    {
        if (request.OldIndex == request.NewIndex || (uint)request.NewIndex >= Labels.Count) return;
        Labels.Move(request.OldIndex, request.NewIndex);
        var moved = Labels[request.NewIndex];
        var ordered = Labels.Select(label => new RankedItem(label.Id, label.SortOrder)).ToList();
        try { await _reorder.MoveAsync<Label>(moved.Id, ordered); }
        finally { await LoadAsync(); }
    }

    [RelayCommand]
    private async Task RenameProjectAsync(RenameRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return;
        var project = await _store.GetAsync<Project>(request.Id);
        if (project is null || project.IsDeleted) return;
        project.Name = request.Name.Trim();
        await _store.SaveAsync(project);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteProjectAsync(Guid id)
    {
        await _store.DeleteAsync<Project>(id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CreateLabelAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _store.SaveAsync(new Label
        {
            Name = name.Trim(),
            Color = LabelColors.ForNewLabel(Labels.Count),
            SortOrder = _reorder.AppendRank(Labels.Select(label => label.SortOrder)),
        });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameLabelAsync(RenameRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return;
        var label = await _store.GetAsync<Label>(request.Id);
        if (label is null || label.IsDeleted) return;
        label.Name = request.Name.Trim();
        await _store.SaveAsync(label);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteLabelAsync(Guid id)
    {
        await _store.DeleteAsync<Label>(id);
        await LoadAsync();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

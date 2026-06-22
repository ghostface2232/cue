using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>Drives the live project and label lists in the left navigation pane.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;

    public ObservableCollection<ProjectListItem> Projects { get; } = new();
    public ObservableCollection<LabelListItem> Labels { get; } = new();

    public ShellViewModel(ITaskStore store, ITaskIndex index)
    {
        _store = store;
        _index = index;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var projects = await _index.GetProjectsAsync();
        var labels = await _index.GetLabelsAsync();
        Replace(Projects, projects);
        Replace(Labels, labels);
    }

    [RelayCommand]
    private async Task CreateProjectAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _store.SaveAsync(new Project { Name = name.Trim() });
        await LoadAsync();
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
        await _store.SaveAsync(new Label { Name = name.Trim() });
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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;

namespace Cue.ViewModels;

/// <summary>Drives the live task-group and tag lists in the left navigation pane.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IReorderService _reorder;
    private readonly IContainerDeletionStore _containers;

    public ObservableCollection<TaskGroupListItem> TaskGroups { get; } = new();
    public ObservableCollection<TagListItem> Tags { get; } = new();

    /// <summary>Open-task counts per group / tag, refreshed on each <see cref="LoadAsync"/>.
    /// Read by the shell after a load to stamp navigation count badges.</summary>
    public IReadOnlyDictionary<Guid, int> TaskGroupTaskCounts { get; private set; } = new Dictionary<Guid, int>();
    public IReadOnlyDictionary<Guid, int> TagTaskCounts { get; private set; } = new Dictionary<Guid, int>();

    /// <summary>Open-task counts for the 그룹 없음 / 태그 없음 collection points, refreshed on each
    /// <see cref="LoadAsync"/>. Read by the shell to stamp their navigation badges.</summary>
    public int NoTaskGroupTaskCount { get; private set; }
    public int NoTagTaskCount { get; private set; }

    public ShellViewModel(ITaskStore store, ITaskIndex index, IReorderService reorder, IContainerDeletionStore containers)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _containers = containers;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var taskGroups = await _index.GetTaskGroupsAsync();
        var tags = await _index.GetTagsAsync();
        TaskGroupTaskCounts = await _index.GetOpenTaskCountsByTaskGroupAsync();
        TagTaskCounts = await _index.GetOpenTaskCountsByTagAsync();
        NoTaskGroupTaskCount = await _index.GetOpenTaskCountWithoutTaskGroupAsync();
        NoTagTaskCount = await _index.GetOpenTaskCountWithoutTagAsync();
        Replace(TaskGroups, taskGroups);
        Replace(Tags, tags);
    }

    [RelayCommand]
    private async Task CreateTaskGroupAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _store.SaveAsync(new TaskGroup
        {
            Name = name.Trim(),
            SortOrder = _reorder.AppendRank(TaskGroups.Select(group => group.SortOrder)),
        });
        await LoadAsync();
    }

    /// <summary>
    /// Reorders a group in the pane: moves it optimistically, persists the moved record's new rank
    /// through the rank service (only that record save for a rare rebalance), then reloads from the
    /// index so the persisted order — the source of truth — drives the pane.
    /// </summary>
    [RelayCommand]
    public async Task ReorderTaskGroupAsync(ReorderRequest request)
    {
        if (request.OldIndex == request.NewIndex || (uint)request.NewIndex >= TaskGroups.Count) return;
        TaskGroups.Move(request.OldIndex, request.NewIndex);
        var moved = TaskGroups[request.NewIndex];
        var ordered = TaskGroups.Select(group => new RankedItem(group.Id, group.SortOrder)).ToList();
        try { await _reorder.MoveAsync<TaskGroup>(moved.Id, ordered); }
        finally { await LoadAsync(); }
    }

    /// <summary>Reorders a tag in the pane. Mirrors <see cref="ReorderTaskGroupAsync"/>.</summary>
    [RelayCommand]
    public async Task ReorderTagAsync(ReorderRequest request)
    {
        if (request.OldIndex == request.NewIndex || (uint)request.NewIndex >= Tags.Count) return;
        Tags.Move(request.OldIndex, request.NewIndex);
        var moved = Tags[request.NewIndex];
        var ordered = Tags.Select(tag => new RankedItem(tag.Id, tag.SortOrder)).ToList();
        try { await _reorder.MoveAsync<Tag>(moved.Id, ordered); }
        finally { await LoadAsync(); }
    }

    [RelayCommand]
    private async Task RenameTaskGroupAsync(RenameRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return;
        var taskGroup = await _store.GetAsync<TaskGroup>(request.Id);
        if (taskGroup is null || taskGroup.IsDeleted) return;
        taskGroup.Name = request.Name.Trim();
        await _store.SaveAsync(taskGroup);
        await LoadAsync();
    }

    /// <summary>Deletes a group, disposing of its tasks per <paramref name="mode"/> (reparent to the
    /// Cue home, or soft-delete alongside the group), then reloads the navigation.</summary>
    public async Task DeleteTaskGroupAsync(Guid id, TaskGroupDeletionMode mode)
    {
        await _containers.DeleteTaskGroupAsync(id, mode);
        await LoadAsync();
    }

    /// <summary>Sets a group's sidebar icon (a Fluent glyph) and reloads so it shows at once.</summary>
    public async Task SetTaskGroupIconAsync(Guid id, string glyph)
    {
        var taskGroup = await _store.GetAsync<TaskGroup>(id);
        if (taskGroup is null || taskGroup.IsDeleted) return;
        taskGroup.Icon = glyph;
        await _store.SaveAsync(taskGroup);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CreateTagAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _store.SaveAsync(new Tag
        {
            Name = name.Trim(),
            Color = TagColors.ForNewTag(Tags.Count),
            SortOrder = _reorder.AppendRank(Tags.Select(tag => tag.SortOrder)),
        });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameTagAsync(RenameRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return;
        var tag = await _store.GetAsync<Tag>(request.Id);
        if (tag is null || tag.IsDeleted) return;
        tag.Name = request.Name.Trim();
        await _store.SaveAsync(tag);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteTagAsync(Guid id)
    {
        await _store.DeleteAsync<Tag>(id);
        await LoadAsync();
    }

    /// <summary>Recolors a tag from the navigation pane and reloads so the change shows at once.</summary>
    public async Task SetTagColorAsync(Guid id, string color)
    {
        var tag = await _store.GetAsync<Tag>(id);
        if (tag is null || tag.IsDeleted) return;
        tag.Color = color;
        await _store.SaveAsync(tag);
        await LoadAsync();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

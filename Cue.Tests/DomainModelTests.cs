using Cue.Domain;

namespace Cue.Tests;

public class DomainModelTests
{
    [Fact]
    public void NewTask_HasSaneDefaults()
    {
        var task = new TaskItem();

        Assert.NotEqual(Guid.Empty, task.Id);
        Assert.Equal(string.Empty, task.Title);
        Assert.Null(task.Notes);
        Assert.Null(task.CompletedAt);
        Assert.False(task.IsCompleted);
        Assert.Equal(WhenKind.Unscheduled, task.When.Kind);
        Assert.False(task.When.HasDate);
        Assert.Equal(Priority.None, task.Priority);
        Assert.Null(task.TaskGroupId);
        Assert.NotNull(task.Checklist);
        Assert.Empty(task.Checklist);
        Assert.Null(task.Recurrence);
        Assert.NotNull(task.TagIds);
        Assert.Empty(task.TagIds);
        Assert.Equal(string.Empty, task.SortOrder);
        Assert.Equal(RecordBase.CurrentSchemaVersion, task.SchemaVersion);
    }

    [Fact]
    public void Completion_IsDerivedFromCompletedAt()
    {
        var task = new TaskItem();
        Assert.False(task.IsCompleted);

        task.CompletedAt = new DateTimeOffset(2026, 6, 22, 3, 0, 0, TimeSpan.Zero);
        Assert.True(task.IsCompleted);

        task.CompletedAt = null; // re-opened
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void EveryRecordType_CarriesTheCommonAuditFields()
    {
        RecordBase[] records = { new TaskItem(), new TaskGroup(), new Tag() };

        foreach (var record in records)
        {
            Assert.NotEqual(Guid.Empty, record.Id);
            Assert.Null(record.DeletedAt);
            Assert.False(record.IsDeleted);
            Assert.Equal(RecordBase.CurrentSchemaVersion, record.SchemaVersion);
        }
    }

    [Fact]
    public void SoftDelete_IsReflectedByIsDeleted()
    {
        var task = new TaskItem();
        Assert.False(task.IsDeleted);

        // Soft delete = stamp the tombstone; the record itself is never removed.
        task.DeletedAt = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

        Assert.True(task.IsDeleted);
    }

    [Fact]
    public void Records_HaveIdentityEqualityByIdAndType()
    {
        var id = Guid.NewGuid();
        var a = new TaskItem { Id = id, Title = "first copy" };
        var b = new TaskItem { Id = id, Title = "second copy, different fields" };
        var other = new TaskItem { Id = Guid.NewGuid() };

        // Same type + same Id => equal, even though Title differs.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, other);

        // De-duplication keys on Id, not whole-object value.
        var distinct = new[] { a, b, other }.Distinct().ToList();
        Assert.Equal(2, distinct.Count);

        // A different record type with the same Guid is not equal.
        var taskGroup = new TaskGroup { Id = id };
        Assert.False(a.Equals(taskGroup));
    }

    [Fact]
    public void EqualityOperators_MatchIdentityAndHandleNull()
    {
        var id = Guid.NewGuid();
        var a = new TaskItem { Id = id, Title = "one" };
        var b = new TaskItem { Id = id, Title = "another" };
        var other = new TaskItem { Id = Guid.NewGuid() };

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != other);

        TaskItem? nullRef = null;
        Assert.False(a == nullRef);
        Assert.True(a != nullRef);
        Assert.True(nullRef == null);
    }

    [Fact]
    public void Checklist_HoldsLightweightItemsInOrder()
    {
        var task = new TaskItem { Title = "Plan trip" };
        task.Checklist.Add(new ChecklistItem { Title = "Book flights", IsChecked = true, Note = "aisle seat" });
        task.Checklist.Add(new ChecklistItem { Title = "Pack bags" });

        Assert.Equal(2, task.Checklist.Count);
        Assert.Equal("Book flights", task.Checklist[0].Title);
        Assert.True(task.Checklist[0].IsChecked);
        Assert.Equal("aisle seat", task.Checklist[0].Note);
        Assert.Equal("Pack bags", task.Checklist[1].Title);
        Assert.False(task.Checklist[1].IsChecked);
        Assert.Null(task.Checklist[1].Note);
        Assert.NotEqual(Guid.Empty, task.Checklist[0].Id);
        Assert.NotEqual(task.Checklist[0].Id, task.Checklist[1].Id);
    }

    [Fact]
    public void TaskGroup_Defaults_AreListView()
    {
        var taskGroup = new TaskGroup();

        Assert.Equal(TaskGroupView.List, taskGroup.View);
    }
}

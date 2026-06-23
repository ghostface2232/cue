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
        Assert.Null(task.ProjectId);
        Assert.Null(task.ParentTaskId);
        Assert.Null(task.Recurrence);
        Assert.NotNull(task.LabelIds);
        Assert.Empty(task.LabelIds);
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
        RecordBase[] records = { new TaskItem(), new Project(), new Label() };

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
        var project = new Project { Id = id };
        Assert.False(a.Equals(project));
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
    public void Subtask_PointsAtParentAndKeepsItsOwnProperties()
    {
        var parent = new TaskItem { Title = "Plan trip" };
        var child = new TaskItem
        {
            Title = "Book flights",
            ParentTaskId = parent.Id,
            Priority = Priority.P1,
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 18, 0, 0), "Asia/Seoul")),
        };

        Assert.Equal(parent.Id, child.ParentTaskId);
        Assert.Equal(Priority.P1, child.Priority);
        Assert.True(child.When.HasDate);
    }

    [Fact]
    public void Project_Defaults_AreListView()
    {
        var project = new Project();

        Assert.Equal(ProjectView.List, project.View);
    }
}

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
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.Deadline);
        Assert.Null(task.When);
        Assert.Equal(Priority.None, task.Priority);
        Assert.Null(task.ProjectId);
        Assert.Null(task.SectionId);
        Assert.Null(task.AreaId);
        Assert.Null(task.ParentTaskId);
        Assert.Null(task.Recurrence);
        Assert.NotNull(task.LabelIds);
        Assert.Empty(task.LabelIds);
        Assert.Equal(RecordBase.CurrentSchemaVersion, task.SchemaVersion);
    }

    [Fact]
    public void EveryRecordType_CarriesTheCommonAuditFields()
    {
        // Compile-time guarantee that each record inherits the shared shape, plus a
        // runtime check that the tombstone field exists and starts alive.
        RecordBase[] records = { new TaskItem(), new Project(), new Area(), new Label(), new Section() };

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
    public void UniqueIds_AreGeneratedPerInstance()
    {
        var a = new TaskItem();
        var b = new TaskItem();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Subtask_PointsAtParentAndKeepsItsOwnProperties()
    {
        var parent = new TaskItem { Title = "Plan trip" };
        var child = new TaskItem
        {
            Title = "Book flights",
            ParentTaskId = parent.Id,
            Priority = Priority.High,
            Deadline = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 18, 0, 0), "Asia/Seoul"),
        };

        Assert.Equal(parent.Id, child.ParentTaskId);
        Assert.Equal(Priority.High, child.Priority);
        Assert.NotNull(child.Deadline);
    }

    [Fact]
    public void Project_Defaults_AreActiveListView()
    {
        var project = new Project();

        Assert.False(project.IsArchived);
        Assert.Null(project.CompletedAt);
        Assert.Equal(ProjectView.List, project.View);
    }
}

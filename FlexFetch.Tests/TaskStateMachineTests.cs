using FlexFetch.Entities;
using FlexFetch.Services.Tasks;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TaskStateMachineTests
{
    [TestMethod]
    public void CanTransition_ValidTransitions()
    {
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Queued, TaskStatus.Running));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Queued, TaskStatus.Cancelled));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Completed));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Failed));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Cancelled));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Failed, TaskStatus.Queued));
    }

    [TestMethod]
    public void CanTransition_RejectsIllegalTransitions()
    {
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Completed, TaskStatus.Running));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Cancelled, TaskStatus.Running));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Queued, TaskStatus.Completed));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Queued));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Completed, TaskStatus.Failed));
    }

    [TestMethod]
    public void EnsureTransition_ThrowsOnIllegal()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => TaskStateMachine.EnsureTransition(TaskStatus.Completed, TaskStatus.Running));
    }
}

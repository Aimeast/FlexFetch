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
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Completed));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Failed));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Failed, TaskStatus.Queued));
    }

    [TestMethod]
    public void CanTransition_Waiting_Transitions()
    {
        // Startup recovery holds interrupted tasks in Waiting until the
        // component installs finish, then releases them back into the queue;
        // a second recovery re-holds an already Waiting task.
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Running, TaskStatus.Waiting));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Queued, TaskStatus.Waiting));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Waiting, TaskStatus.Waiting));
        Assert.IsTrue(TaskStateMachine.CanTransition(TaskStatus.Waiting, TaskStatus.Queued));
    }

    [TestMethod]
    public void CanTransition_Waiting_RejectsIllegalTransitions()
    {
        // A held task reaches the queue only via ReleaseWaitingTasks; it
        // never runs or completes while waiting.
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Waiting, TaskStatus.Running));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Waiting, TaskStatus.Completed));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Waiting, TaskStatus.Failed));
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Completed, TaskStatus.Waiting));
    }

    [TestMethod]
    public void CanTransition_RejectsIllegalTransitions()
    {
        Assert.IsFalse(TaskStateMachine.CanTransition(TaskStatus.Completed, TaskStatus.Running));
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

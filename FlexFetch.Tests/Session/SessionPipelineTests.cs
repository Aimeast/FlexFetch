using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionPipelineTests
{
    [TestMethod]
    public async Task RunAsync_SecondCallWhileRunning_IsRejectedWithBusyMessage()
    {
        var pipeline = new SessionPipeline("already running");
        var blocker = new TaskCompletionSource<int>();
        var first = pipeline.RunAsync(_ => blocker.Task, CancellationToken.None);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.RunAsync(_ => Task.FromResult(2), CancellationToken.None));
        Assert.AreEqual("already running", ex.Message);
        Assert.IsTrue(pipeline.IsRunning);

        blocker.SetResult(1);
        Assert.AreEqual(1, await first);
        Assert.IsFalse(pipeline.IsRunning);
    }

    [TestMethod]
    public async Task RunAsync_ResetsStageAndRecordsTheRun()
    {
        var pipeline = new SessionPipeline("busy");
        pipeline.SetStage("left-over");

        var result = await pipeline.RunAsync(ct =>
        {
            pipeline.SetStage("probe");
            pipeline.SetDetail("live line");
            return Task.FromResult(7);
        }, CancellationToken.None);

        Assert.AreEqual(7, result);
        Assert.AreEqual("idle", pipeline.CurrentStage, "the stage must reset after the run");
        Assert.IsNull(pipeline.CurrentDetail);
        Assert.IsNotNull(pipeline.LastRunAt);
        Assert.IsNull(pipeline.LastError);
    }

    [TestMethod]
    public async Task RunAsync_Error_RecordsLastErrorAndRethrows()
    {
        var pipeline = new SessionPipeline("busy");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.RunAsync<int>(_ => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.AreEqual("boom", pipeline.LastError);
        Assert.IsNotNull(pipeline.LastRunAt);
        Assert.AreEqual("idle", pipeline.CurrentStage);
    }
}

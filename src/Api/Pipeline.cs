using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Overens.Ingestion.Pipeline;

namespace Overens.Api;

/// <summary>How the API reaches the pipeline: it can start a run and pass on a reviewer's decision, nothing more.</summary>
public interface IPipeline
{
    Task StartAsync(RunRef run, CancellationToken cancellationToken);

    Task DecideAsync(RunRef run, bool publish, CancellationToken cancellationToken);
}

/// <summary>The pipeline as a Durable Task orchestration, named and shaped by the contract the worker shares.</summary>
public sealed class DurablePipeline(DurableTaskClient client) : IPipeline
{
    public Task StartAsync(RunRef run, CancellationToken cancellationToken) =>
        client.ScheduleNewOrchestrationInstanceAsync(PipelineContract.Orchestration, run, new StartOrchestrationOptions(InstanceId: run.RunId), cancellationToken);

    public Task DecideAsync(RunRef run, bool publish, CancellationToken cancellationToken) =>
        client.RaiseEventAsync(run.RunId, PipelineContract.DecisionEvent, new ReviewDecision(publish), cancellationToken);
}

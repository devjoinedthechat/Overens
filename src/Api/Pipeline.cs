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

/// <summary>The pipeline as a Durable Task orchestration, named and shaped as the worker defines it.</summary>
public sealed class DurablePipeline(DurableTaskClient client) : IPipeline
{
    public Task StartAsync(RunRef run, CancellationToken cancellationToken) =>
        client.ScheduleNewOrchestrationInstanceAsync("IngestAgreement", run, new StartOrchestrationOptions(InstanceId: run.RunId), cancellationToken);

    public Task DecideAsync(RunRef run, bool publish, CancellationToken cancellationToken) =>
        client.RaiseEventAsync(run.RunId, "decision", new { Publish = publish }, cancellationToken);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Interfaces;

public interface ITelemetryProvider
{
    event EventHandler<TelemetryModel>? TelemetryUpdated;

    TelemetryModel Current { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

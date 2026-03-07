using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using AvoPerformanceSetupAI.Interfaces;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public class FakeTelemetryProvider : ITelemetryProvider
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _random = new();
    private double _t;

    public FakeTelemetryProvider()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50); // 20 Hz
        _timer.Tick += (_, _) => Tick();
    }

    public event EventHandler<TelemetryModel>? TelemetryUpdated;

    public TelemetryModel Current { get; private set; } = new();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _timer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    private void Tick()
    {
        _t += 0.05;
        var lapPct = (_t % 60) / 60.0; // 60 s lap
        Current = new TelemetryModel
        {
            Timestamp = DateTime.UtcNow,
            LapDistPct = lapPct,
            SpeedKph = 120 + 80 * Math.Sin(_t * 0.8),
            Throttle = 0.6 + 0.4 * Math.Sin(_t),
            Brake = Math.Max(0, 0.5 * Math.Sin(_t * 1.7)),
            Gear = 3 + (int)(2 * Math.Sin(_t * 0.5)),
            Rpm = 4500 + (int)(2000 * Math.Sin(_t * 0.9)),
            SteeringDeg = 5 * Math.Sin(_t * 1.3),
            TyreTemps = new[]
            {
                75 + 5 * Math.Sin(_t),
                74 + 4 * Math.Sin(_t + 0.2),
                73 + 5 * Math.Sin(_t + 0.4),
                74 + 4 * Math.Sin(_t + 0.6)
            },
            DeltaToRef = 0.3 * Math.Sin(_t * 0.3)
        };

        TelemetryUpdated?.Invoke(this, Current);
    }
}

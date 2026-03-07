using System;
using System.Collections.Generic;
using System.Linq;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public class TrackOverlayBuffer
{
    private readonly TrackSegmentStat[] _segments;

    public TrackOverlayBuffer(int segmentCount = 100)
    {
        _segments = Enumerable.Range(0, segmentCount).Select(i => new TrackSegmentStat { Index = i }).ToArray();
    }

    public IReadOnlyList<TrackSegmentStat> Segments => _segments;

    public void AddSample(double lapDistPct, double throttle, double brake, double speed)
    {
        var index = Math.Clamp((int)(lapDistPct * _segments.Length), 0, _segments.Length - 1);
        var s = _segments[index];
        s.AvgThrottle = (s.AvgThrottle + throttle) / 2.0;
        s.AvgBrake = (s.AvgBrake + brake) / 2.0;
        s.AvgSpeed = (s.AvgSpeed + speed) / 2.0;
    }

    public void Clear()
    {
        foreach (var s in _segments)
        {
            s.AvgThrottle = 0;
            s.AvgBrake = 0;
            s.AvgSpeed = 0;
        }
    }
}

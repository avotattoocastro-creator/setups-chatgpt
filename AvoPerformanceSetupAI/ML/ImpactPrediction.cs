using Microsoft.ML.Data;

namespace AvoPerformanceSetupAI.ML;

/// <summary>
/// Output schema returned by the ML.NET FastTree regression model
/// loaded in <see cref="ImpactPredictor"/>.
/// </summary>
public sealed class ImpactPrediction
{
    /// <summary>
    /// Predicted change in overall driving score (0..∞ represents improvement;
    /// negative values represent predicted degradation).
    /// Corresponds to <see cref="ImpactTrainingSample.DeltaOverallScore"/>.
    /// </summary>
    [ColumnName("Score")]
    public float DeltaOverallScore { get; set; }
}

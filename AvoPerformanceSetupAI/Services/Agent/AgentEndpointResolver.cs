namespace AvoPerformanceSetupAI.Services.Agent;

/// <summary>
/// Centralises the logic for resolving the correct HTTP base URL for the remote Agent,
/// guarding against the common mistake of using port 8182 (UDP discovery) instead of
/// the actual HTTP/WS port (8181).
/// </summary>
public static class AgentEndpointResolver
{
    /// <summary>UDP discovery port — must NOT be used for HTTP/WS calls.</summary>
    public const int DiscoveryPort = 8182;

    /// <summary>Default HTTP/WS port for the Agent.</summary>
    public const int DefaultHttpPort = 8181;

    /// <summary>
    /// Returns the base HTTP URL (<c>http://host:port</c>) for the Agent.
    /// If <paramref name="port"/> equals <see cref="DiscoveryPort"/> (8182),
    /// logs a warning and automatically corrects to <see cref="DefaultHttpPort"/> (8181).
    /// </summary>
    public static string GetBaseHttpUrl(string host, int port)
    {
        if (port == DiscoveryPort)
        {
            AppLogger.Instance.Warn(
                $"Puerto {DiscoveryPort} es Discovery (UDP), NO HTTP/WS. " +
                $"Corrigiendo a {DefaultHttpPort} automáticamente. " +
                $"Configura el puerto HTTP/WS ({DefaultHttpPort}) para evitar este aviso.");
            port = DefaultHttpPort;
        }
        return $"http://{host}:{port}";
    }
}

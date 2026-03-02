using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.Services.Agent;

/// <summary>
/// Typed HTTP client for the AVO Performance remote Agent.
/// All methods throw <see cref="AgentException"/> on network / HTTP errors so
/// callers only need to catch one exception type.
/// </summary>
public sealed class AgentApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Per-request timeout budgets ───────────────────────────────────────────
    private static readonly TimeSpan PingTimeout   = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SaveTimeout   = TimeSpan.FromSeconds(15);

    // Retry delays for reference-browsing endpoints (between attempts 1→2, 2→3, 3→4)
    private static readonly TimeSpan[] BrowseRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
    ];

    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    // In-flight deduplication — avoids duplicate parallel requests from rapid UI events
    private readonly Dictionary<string, Task<List<string>>> _inFlight = new();
    private readonly object _inFlightLock = new();

    public AgentApiClient(string host, int port, string token)
    {
        // AgentEndpointResolver guards against the 8182-UDP mistake and returns the corrected URL.
        _baseUrl = AgentEndpointResolver.GetBaseHttpUrl(host, port);
        // Use InfiniteTimeSpan so individual CancellationTokenSource instances control each request.
        _http    = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Add("X-API-TOKEN", token);
    }

    // ── Connectivity ──────────────────────────────────────────────────────────

    /// <summary>Returns true if the agent responds to GET /api/ping within 3 s.</summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/ping");
            using var res = await SendWithTimeoutAsync(req, PingTimeout);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// GET /api/admin/state — returns the current simulator state.
    /// Returns <see langword="null"/> when the endpoint is not reachable or
    /// not yet implemented by the Agent (404).
    /// </summary>
    public async Task<AgentAdminState?> GetAdminStateAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/admin/state");
            using var res = await SendWithTimeoutAsync(req, PingTimeout);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<AgentAdminState>(JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ── Reference library ─────────────────────────────────────────────────────

    /// <summary>GET /api/reference/root — returns the configured root folder path.</summary>
    public Task<ReferenceRootResponse> GetReferenceRootAsync()
        => GetAsync<ReferenceRootResponse>("/api/reference/root");

    /// <summary>POST /api/admin/referenceRoot/browse — agent opens a folder picker on the sim PC.</summary>
    public Task<BrowseRootResponse> BrowseReferenceRootAsync()
        => PostAsync<BrowseRootResponse>("/api/admin/referenceRoot/browse", null);

    /// <summary>GET /api/reference/cars — list of car folder names. Deduplicates parallel calls.</summary>
    public Task<List<string>> GetCarsAsync(CancellationToken ct = default)
        => GetDeduplicatedStringListAsync("/api/reference/cars", ct);

    /// <summary>GET /api/reference/tracks?car=... — list of track folder names. Deduplicates parallel calls.</summary>
    public Task<List<string>> GetTracksAsync(string car, CancellationToken ct = default)
        => GetDeduplicatedStringListAsync($"/api/reference/tracks?car={Uri.EscapeDataString(car)}", ct);

    /// <summary>GET /api/reference/setups?car=...&amp;track=... — list of setup files. Deduplicates parallel calls.</summary>
    public async Task<List<SetupItem>> GetSetupsAsync(string car, string track, CancellationToken ct = default)
    {
        var files = await GetDeduplicatedStringListAsync(
            $"/api/reference/setups?car={Uri.EscapeDataString(car)}&track={Uri.EscapeDataString(track)}", ct);
        return files
            .Select(f => new SetupItem { FileName = f, Car = car, Track = track })
            .ToList();
    }

    /// <summary>GET /api/reference/setup/read — returns raw INI text of the setup.</summary>
    public Task<string> ReadSetupAsync(string car, string track, string fileName)
        => GetStringAsync(
            $"/api/reference/setup/read?car={Uri.EscapeDataString(car)}" +
            $"&track={Uri.EscapeDataString(track)}" +
            $"&file={Uri.EscapeDataString(fileName)}");

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>POST /api/setup/save — persists a generated setup on the simulator PC.</summary>
    public async Task<SaveResult> SaveSetupAsync(
        string car, string track, string fileName, string setupText, bool overwrite = true)
    {
        if (string.IsNullOrWhiteSpace(setupText))
            throw new AgentException("REMOTE SAVE aborted: content is empty.");

        var url = $"{_baseUrl}/api/setup/save";
        var dto = new SaveSetupRequest
        {
            CarId     = car,
            TrackId   = track,
            FileName  = fileName,
            SetupText = setupText,
            Overwrite = overwrite,
        };
        AppLogger.Instance.Info(
            $"SAVE JSON -> car={dto.CarId}, track={dto.TrackId}, file={dto.FileName}, length={dto.SetupText?.Length}");

        SaveResult result;
        try
        {
            result = await PostAsync<SaveResult>("/api/setup/save", dto);
        }
        catch (AgentException ex)
        {
            var statusPart = ex.HttpStatus.HasValue
                ? $" HTTP {(int)ex.HttpStatus.Value}"
                : string.Empty;
            AppLogger.Instance.Error(
                $"REMOTE SAVE FAILED{statusPart}  url={url}  {ex.Message}");
            throw;
        }

        if (!result.Success)
        {
            var errMsg = string.IsNullOrEmpty(result.Error)
                ? "El Agent rechazó el guardado (success=false sin mensaje)."
                : result.Error;
            AppLogger.Instance.Error($"REMOTE SAVE FAILED  url={url}  {errMsg}");
            throw new AgentException(errMsg);
        }

        AppLogger.Instance.Info("REMOTE SAVE OK");
        return result;
    }

    /// <summary>
    /// GET /api/setups/versions — returns the list of existing versioned files for a given
    /// car/track/base combination, allowing callers to compute the next version number.
    /// Returns an empty <see cref="VersionsResponse"/> when the endpoint is not reachable.
    /// </summary>
    public async Task<VersionsResponse> GetVersionsAsync(string car, string track, string baseFile)
    {
        try
        {
            return await GetAsync<VersionsResponse>(
                $"/api/setups/versions" +
                $"?car={Uri.EscapeDataString(car)}" +
                $"&track={Uri.EscapeDataString(track)}" +
                $"&base={Uri.EscapeDataString(baseFile)}");
        }
        catch
        {
            // If the endpoint is unavailable, return an empty list so versioning starts at v001.
            return new VersionsResponse();
        }
    }

    // ── Core HTTP helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="req"/> with a per-call <paramref name="timeout"/>,
    /// avoiding dependence on the global <see cref="HttpClient.Timeout"/>.
    /// Throws <see cref="TaskCanceledException"/> (with a descriptive message) on timeout.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage req, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"Request to {req.RequestUri} timed out after {timeout.TotalSeconds:F0}s.");
        }
    }

    private async Task<T> GetAsync<T>(string path)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            var res = await SendWithTimeoutAsync(req, BrowseTimeout);
            await EnsureSuccessAsync(res);
            var result = await res.Content.ReadFromJsonAsync<T>(JsonOpts);
            return result ?? throw new AgentException("Empty response from agent.");
        }
        catch (AgentException) { throw; }
        catch (Exception ex)   { throw new AgentException($"Agent no accesible: {ex.Message}", ex); }
    }

    private async Task<string> GetStringAsync(string path)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            var res = await SendWithTimeoutAsync(req, BrowseTimeout);
            await EnsureSuccessAsync(res);
            return await res.Content.ReadAsStringAsync();
        }
        catch (AgentException) { throw; }
        catch (Exception ex)   { throw new AgentException($"Agent no accesible: {ex.Message}", ex); }
    }

    // ── Deduplication + retry for list endpoints ──────────────────────────────

    /// <summary>
    /// Returns the in-flight Task for <paramref name="path"/> if one is already running,
    /// otherwise starts a new retriable fetch and caches it until it settles.
    /// This prevents duplicate parallel requests from rapid UI events.
    /// </summary>
    private Task<List<string>> GetDeduplicatedStringListAsync(string path, CancellationToken ct)
    {
        lock (_inFlightLock)
        {
            if (_inFlight.TryGetValue(path, out var existing)) return existing;
            var task = FetchStringListWithRetryAsync(path, ct);
            _inFlight[path] = task;
            // Remove from the dict once settled (success or failure) so later calls can start fresh.
            _ = task.ContinueWith(t => { lock (_inFlightLock) { _inFlight.Remove(path); } },
                                  TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// Fetches <paramref name="path"/> with up to 4 attempts (initial + 3 retries),
    /// backing off 250 ms → 750 ms → 1 500 ms between attempts.
    /// Retries on <see cref="TaskCanceledException"/> (timeout) and
    /// <see cref="HttpRequestException"/> (connection errors).
    /// Does NOT retry on HTTP 4xx/5xx (those surface as <see cref="AgentException"/>).
    /// Logs endpoint, timeout, attempt number, and next retry delay on each transient failure.
    /// </summary>
    private async Task<List<string>> FetchStringListWithRetryAsync(string path, CancellationToken ct)
    {
        int totalAttempts = BrowseRetryDelays.Length + 1;
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
                var res = await SendWithTimeoutAsync(req, BrowseTimeout, ct);
                await EnsureSuccessAsync(res);
                return await ReadStringListAsync(res);
            }
            catch (AgentException)
            {
                // HTTP-level error (401, 404, 5xx…) — do NOT retry.
                throw;
            }
            catch (TaskCanceledException ex)
            {
                lastEx = ex;
                if (attempt < totalAttempts)
                {
                    var delay = BrowseRetryDelays[attempt - 1];
                    AppLogger.Instance.Warn(
                        $"Timeout en {_baseUrl}{path} " +
                        $"(intento {attempt}/{totalAttempts}, timeout={BrowseTimeout.TotalSeconds:F0}s). " +
                        $"Reintentando en {delay.TotalMilliseconds:F0}ms…");
                    await Task.Delay(delay, ct);
                }
            }
            catch (HttpRequestException ex)
            {
                lastEx = ex;
                if (attempt < totalAttempts)
                {
                    var delay = BrowseRetryDelays[attempt - 1];
                    AppLogger.Instance.Warn(
                        $"Error de red en {_baseUrl}{path} " +
                        $"(intento {attempt}/{totalAttempts}): {ex.Message}. " +
                        $"Reintentando en {delay.TotalMilliseconds:F0}ms…");
                    await Task.Delay(delay, ct);
                }
            }
        }

        // All attempts exhausted — log and throw a detailed exception.
        var hint = "Sugerencia: Puerto HTTP/WS por defecto 8181; discovery UDP 8182.";
        AppLogger.Instance.Error(
            $"Todos los intentos fallaron para {_baseUrl}{path}: {lastEx?.Message}\n  {hint}");
        throw new AgentException(
            $"Agent no accesible en {_baseUrl}{path} tras {totalAttempts} intentos: {lastEx?.Message}\n" +
            $"  {hint}",
            lastEx!);
    }

    // ── JSON deserialization ───────────────────────────────────────────────────

    /// <summary>
    /// Deserializes an HTTP response into a <c>List&lt;string&gt;</c>.
    /// Accepts both a plain JSON array (<c>["a","b"]</c>) and a wrapped object
    /// (<c>{{ "cars":[...] }}</c>, <c>{{ "tracks":[...] }}</c>,
    ///  <c>{{ "setups":[...] }}</c>, <c>{{ "data":[...] }}</c>).
    /// Logs and rethrows a detailed <see cref="AgentException"/> when the format
    /// is not recognised.
    /// </summary>
    private static async Task<List<string>> ReadStringListAsync(HttpResponseMessage response)
    {
        var rawJson = await response.Content.ReadAsStringAsync();

        // 1) Try direct array deserialization
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(rawJson, JsonOpts);
            if (list is not null) return list;
        }
        catch (JsonException) { /* fall through */ }

        // 2) Try wrapped object: { "cars":[...] }, { "tracks":[...] }, { "setups":[...] }, { "data":[...] }
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "cars", "tracks", "setups", "data", "items", "results" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var prop) &&
                        prop.ValueKind == JsonValueKind.Array)
                    {
                        var result = prop.EnumerateArray()
                                        .Where(e => e.ValueKind == JsonValueKind.String)
                                        .Select(e => e.GetString()!)
                                        .ToList();
                        AppLogger.Instance.Warn(
                            $"Agent devolvió lista envuelta en propiedad '{key}'. " +
                            $"Considera actualizar el Agent para devolver un array plano.");
                        return result;
                    }
                }
            }
        }
        catch (JsonException) { /* fall through to detailed error */ }

        // 3) Could not parse — log raw content and throw
        var preview = rawJson.Length > 300 ? rawJson[..300] + "…" : rawJson;
        AppLogger.Instance.Error(
            $"Error de deserialización JSON del Agent. JSON recibido: {preview}");
        throw new AgentException(
            $"Respuesta JSON no reconocida del Agent. " +
            $"Se esperaba array de strings o objeto con propiedad 'cars'/'tracks'/'setups'/'data'. " +
            $"JSON: {preview}");
    }

    private async Task<T> PostAsync<T>(string path, object? body)
    {
        return await PostWithTimeoutAsync<T>(path, body, SaveTimeout);
    }

    private async Task<T> PostWithTimeoutAsync<T>(string path, object? body, TimeSpan timeout)
    {
        try
        {
            using var req  = body is null
                ? new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
                  { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }
                : new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
                  { Content = JsonContent.Create(body, options: JsonOpts) };

            var res = await SendWithTimeoutAsync(req, timeout);
            await EnsureSuccessAsync(res);
            var result = await res.Content.ReadFromJsonAsync<T>(JsonOpts);
            return result ?? throw new AgentException("Empty response from agent.");
        }
        catch (AgentException) { throw; }
        catch (Exception ex)   { throw new AgentException($"Agent no accesible: {ex.Message}", ex); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var body      = await res.Content.ReadAsStringAsync();
        var errorText = TryExtractJsonError(body) ?? body;
        throw res.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new AgentException("Token inválido (401).", System.Net.HttpStatusCode.Unauthorized),
            System.Net.HttpStatusCode.NotFound     =>
                new AgentException("Endpoint no encontrado (404).", System.Net.HttpStatusCode.NotFound),
            _ => new AgentException($"Error HTTP {(int)res.StatusCode}: {errorText}", res.StatusCode)
        };
    }

    /// <summary>
    /// Tries to extract a plain error string from a JSON body of the form
    /// <c>{ "error": "message" }</c>. Returns <see langword="null"/> when the body is
    /// not that shape (so callers can fall back to the raw body).
    /// </summary>
    private static string? TryExtractJsonError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String)
                return err.GetString();
        }
        catch { /* not JSON or unexpected shape */ }
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Typed exception thrown by <see cref="AgentApiClient"/> on every error path.</summary>
public sealed class AgentException : Exception
{
    /// <summary>HTTP status code, or <see langword="null"/> when the error is not HTTP-level.</summary>
    public System.Net.HttpStatusCode? HttpStatus { get; }

    public AgentException(string message) : base(message) { }
    public AgentException(string message, Exception inner) : base(message, inner) { }
    public AgentException(string message, System.Net.HttpStatusCode status) : base(message)
        => HttpStatus = status;
}

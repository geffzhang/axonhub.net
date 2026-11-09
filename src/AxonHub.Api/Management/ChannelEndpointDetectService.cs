using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Argument object for <see cref="ChannelEndpointDetectService.DetectAsync"/>.</summary>
public sealed record DetectChannelEndpointsCommand(long ChannelId, string? Model);

/// <summary>Detection outcome for one protocol (biz.DetectedChannelEndpoint).</summary>
public sealed record DetectedChannelEndpointResult(
    string ApiFormat,
    bool Supported,
    int StatusCode,
    string Reason);

/// <summary>Detection payload (biz.DetectChannelEndpointsPayload).</summary>
public sealed record DetectChannelEndpointsResult(IReadOnlyList<DetectedChannelEndpointResult> Endpoints);

/// <summary>
/// Port of biz.ChannelService.DetectChannelEndpoints
/// (internal/server/biz/channel_endpoint_detect.go): probe the channel upstream
/// for chat completions, responses and messages and report which surfaces it
/// exposes. It never mutates the channel.
/// </summary>
/// <remarks>
/// The reference probes with the channel's resolved outbound map; the port has
/// no stored endpoint records yet (C082), so it reuses the primary outbound for
/// the channel type's primary format and builds the reference's generic
/// non-default outbound for the other two. The per-channel proxy client is not
/// modelled (C104/C150); the shared pipeline executor issues the probes.
/// </remarks>
public sealed class ChannelEndpointDetectService
{
    // Detection outcome reasons (channel_endpoint_detect.go:35-45). Stable
    // values consumed by the frontend.
    public const string ReasonSupported = "supported";
    public const string ReasonNotFound = "not_found";
    public const string ReasonAuthError = "auth_error";
    public const string ReasonRateLimited = "rate_limited";
    public const string ReasonServerError = "server_error";
    public const string ReasonUnreachable = "unreachable";
    public const string ReasonInvalid = "invalid_request";

    /// <summary>
    /// The ordered protocol surfaces the detector probes
    /// (detectableEndpointAPIFormats, channel_endpoint_detect.go:25-29).
    /// </summary>
    public static IReadOnlyList<string> DetectableApiFormats { get; } =
    [
        ApiFormats.OpenAIChatCompletion,
        ApiFormats.OpenAIResponse,
        ApiFormats.AnthropicMessage,
    ];

    /// <summary>Per-probe bound (endpointDetectTimeout, channel_endpoint_detect.go:33).</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(20);

    private readonly IChannelWriteStore channels;
    private readonly IExecutor executor;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// The shared key-selection state. Detection reads the current cursors (the
    /// reference builds its probe channels through buildChannelWithTransformer,
    /// which attaches the same state) but must never publish a served snapshot,
    /// so the success report never walks a generation built for detection.
    /// </summary>
    private readonly ChannelApiKeySelectionRegistry? apiKeySelection;

    public ChannelEndpointDetectService(
        IChannelWriteStore channels,
        IExecutor executor,
        TimeProvider timeProvider,
        ChannelApiKeySelectionRegistry? apiKeySelection = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.channels = channels;
        this.executor = executor;
        this.timeProvider = timeProvider;
        this.apiKeySelection = apiKeySelection;
    }

    public async Task<DetectChannelEndpointsResult> DetectAsync(
        DetectChannelEndpointsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await LoadChannelAsync(command.ChannelId, cancellationToken).ConfigureAwait(false);

        var candidate = new ChannelCandidate
        {
            ChannelId = entity.Id,
            ChannelName = entity.Name,
            UpdatedAt = entity.UpdatedAt,
            Type = entity.Type,
            BaseUrl = entity.BaseUrl,
            Credentials = entity.Credentials,
            DisabledApiKeys = entity.DisabledApiKeys,
            DefaultTestModel = entity.DefaultTestModel,
            Settings = entity.Settings,
            Endpoints = entity.Endpoints,
        };

        var now = timeProvider.GetUtcNow();
        var primaryFormat = ChannelOutboundFactory.PrimaryApiFormat(entity.Type);

        IOutboundTransformer primary;
        try
        {
            // buildChannelWithOutbounds always builds the primary first and
            // aborts detection when it fails; the reference's fallback rebuild
            // fails with the same error, which the caller wraps with the
            // channel name (channel_endpoint_detect.go:81-95).
            primary = ChannelOutboundFactory.Create(
                candidate,
                new LlmRequest { ApiFormat = primaryFormat ?? ApiFormats.OpenAIChatCompletion },
                now,
                apiKeySelection: apiKeySelection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to build channel " + entity.Name + ": " + ex.Message,
                ex);
        }

        var model = ResolveModel(entity, command.Model);
        var results = new DetectedChannelEndpointResult[DetectableApiFormats.Count];

        // The reference probes the three formats in parallel goroutines and
        // writes each result by index (channel_endpoint_detect.go:99-112), so
        // the payload order is fixed regardless of completion order.
        await Task.WhenAll(DetectableApiFormats.Select((format, index) => Task.Run(
                () => results[index] = Probe(
                    candidate,
                    primary,
                    primaryFormat,
                    format,
                    model,
                    now,
                    cancellationToken),
                CancellationToken.None)))
            .ConfigureAwait(false);

        return new DetectChannelEndpointsResult(results);
    }

    private async Task<ChannelRecord> LoadChannelAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await channels.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "channel not found: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            // ent's Channel.Get error wrapped by the reference
            // (channel_endpoint_detect.go:76-79).
            throw new InvalidOperationException("channel not found: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Port of detectChannelEndpoint (channel_endpoint_detect.go:120-205): a
    /// route is supported unless the upstream clearly reports the path does not
    /// exist or cannot be reached.
    /// </summary>
    private DetectedChannelEndpointResult Probe(
        ChannelCandidate candidate,
        IOutboundTransformer primary,
        string? primaryFormat,
        string apiFormat,
        string model,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = new DetectedChannelEndpointResult(apiFormat, false, 0, ReasonUnreachable);

        IOutboundTransformer outbound;
        if (primaryFormat is not null
            && string.Equals(apiFormat, primaryFormat, StringComparison.Ordinal))
        {
            outbound = primary;
        }
        else
        {
            try
            {
                outbound = ChannelOutboundFactory.CreateNonDefaultEndpointOutbound(
                    candidate,
                    apiFormat,
                    now,
                    apiKeySelection);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reference logs and reports invalid_request when a
                // non-default outbound cannot be built.
                return result with { Reason = ReasonInvalid };
            }
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        HttpRequestData httpRequest;
        try
        {
            httpRequest = outbound.TransformRequest(BuildProbeRequest(apiFormat, model));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result with { Reason = ReasonInvalid };
        }

        try
        {
            var response = executor.Do(probeCts.Token, httpRequest);
            return result with
            {
                Supported = true,
                StatusCode = response.StatusCode,
                Reason = ReasonSupported,
            };
        }
        catch (HttpClientError error)
        {
            return Classify(apiFormat, error.StatusCode);
        }
        catch (Exception)
        {
            // Transport failure: not an httpclient.Error in the reference, so
            // the probe keeps unreachable and no status code.
            return result;
        }
    }

    private static DetectedChannelEndpointResult Classify(string apiFormat, int statusCode)
    {
        var (supported, reason) = statusCode switch
        {
            404 or 405 => (false, ReasonNotFound),
            401 or 403 => (false, ReasonAuthError),
            429 => (true, ReasonRateLimited),
            >= 500 => (false, ReasonServerError),
            // Any other client error (400/422/...) means the route exists and
            // rejected the probe payload, so the protocol is available.
            _ => (true, ReasonSupported),
        };

        return new DetectedChannelEndpointResult(apiFormat, supported, statusCode, reason);
    }

    /// <summary>
    /// Port of buildEndpointDetectRequest (channel_endpoint_detect.go:207-217):
    /// a minimal, cheap probe. The model is always sent, even when empty.
    /// </summary>
    private static LlmRequest BuildProbeRequest(string apiFormat, string model)
        => new()
        {
            ApiFormat = apiFormat,
            Model = model,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromText("ping"),
                },
            ],
            MaxCompletionTokens = 1,
            Stream = false,
        };

    /// <summary>
    /// Port of resolveEndpointDetectModel (channel_endpoint_detect.go:219-240):
    /// explicit request, then the channel default test model, then the first
    /// supported model.
    /// </summary>
    private static string ResolveModel(ChannelRecord entity, string? requested)
    {
        if (requested is not null)
        {
            var trimmed = requested.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        var defaultModel = entity.DefaultTestModel.Trim();
        if (defaultModel.Length > 0)
        {
            return defaultModel;
        }

        foreach (var model in ParseSupportedModels(entity.SupportedModels))
        {
            var trimmed = model.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static string[] ParseSupportedModels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string?[]>(raw)?
                .Select(static value => value ?? string.Empty)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            // ent would have rejected the row at scan time; the port stays
            // lenient like its other JSON-column readers (C141).
            return [];
        }
    }
}

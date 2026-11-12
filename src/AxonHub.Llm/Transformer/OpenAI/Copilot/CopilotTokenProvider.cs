using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.OpenAI.Copilot;

/// <summary>
/// Port of the local <c>TokenProvider</c> interface the Copilot outbound
/// transformer consumes (outbound.go:51-53).
/// </summary>
public interface ICopilotTokenProvider
{
    /// <summary>Returns a valid Copilot token, exchanging the OAuth token when needed.</summary>
    string GetToken(CancellationToken cancellationToken);
}

/// <summary>Port of copilot.TokenProviderParams (token_provider.go:17-22).</summary>
public sealed class CopilotTokenProviderParams
{
    public OAuthCredentials? Credentials { get; set; }

    /// <summary>The channel's proxy-aware client; also used by the token exchanger.</summary>
    public IExecutor? HttpClient { get; set; }

    /// <summary>Called when the OAuth credentials are refreshed (for persistence).</summary>
    public Func<CancellationToken, OAuthCredentials, Exception?>? OnRefreshed { get; set; }

    /// <summary>
    /// Port-only seam (§194): the shared per-channel Copilot token exchanger.
    /// The reference owns one exchanger per enabled-channel cache entry; the
    /// port builds the outbound per attempt, so the API layer hands the same
    /// exchanger back on every attempt. When null the provider creates its own
    /// like <c>copilot.NewTokenProvider</c> (token_provider.go:26-50).
    /// </summary>
    public ITokenExchanger? TokenExchanger { get; set; }
}

/// <summary>
/// Port of copilot.CopilotTokenProvider (token_provider.go:10-89): a device-flow
/// provider pinned to GitHub's endpoints and wired to the Copilot token
/// exchanger.
/// </summary>
public sealed class CopilotTokenProvider : ICopilotTokenProvider
{
    private readonly DeviceFlowProvider _deviceFlowProvider;

    /// <summary>Port of <c>NewTokenProvider</c> (token_provider.go:26-50).</summary>
    public CopilotTokenProvider(CopilotTokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var exchanger = parameters.TokenExchanger ?? new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = parameters.HttpClient,
        });

        _deviceFlowProvider = new DeviceFlowProvider(new DeviceFlowProviderParams
        {
            Config = new DeviceFlowConfig
            {
                DeviceAuthUrl = CopilotConstants.DeviceAuthUrl,
                TokenUrl = CopilotConstants.OAuthTokenUrl,
                ClientId = CopilotConstants.OAuthClientId,
                Scopes = [.. CopilotConstants.DeviceFlowScopes],
                UserAgent = string.Empty,
            },
            HttpClient = parameters.HttpClient,
            Credentials = parameters.Credentials,
            TokenExchanger = exchanger,
            OnRefreshed = parameters.OnRefreshed,
        });
    }

    /// <summary>Port of <c>(*CopilotTokenProvider).GetToken</c>.</summary>
    public string GetToken(CancellationToken cancellationToken)
        => _deviceFlowProvider.GetToken(cancellationToken);

    /// <summary>Port of <c>(*CopilotTokenProvider).UpdateCredentials</c>.</summary>
    public void UpdateCredentials(OAuthCredentials? credentials)
        => _deviceFlowProvider.UpdateCredentials(credentials);

    /// <summary>Port of <c>(*CopilotTokenProvider).GetCredentials</c>.</summary>
    public OAuthCredentials? GetCredentials()
        => _deviceFlowProvider.GetCredentials();

    /// <summary>Port of <c>(*CopilotTokenProvider).StartAutoRefresh</c>.</summary>
    public void StartAutoRefresh(CancellationToken cancellationToken, AutoRefreshOptions options)
        => _deviceFlowProvider.StartAutoRefresh(cancellationToken, options);

    /// <summary>Port of <c>(*CopilotTokenProvider).StopAutoRefresh</c>.</summary>
    public void StopAutoRefresh()
        => _deviceFlowProvider.StopAutoRefresh();
}

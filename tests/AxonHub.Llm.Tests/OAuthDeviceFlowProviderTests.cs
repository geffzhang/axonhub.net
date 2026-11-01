using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>Port of llm/oauth/device_flow_provider_test.go.</summary>
public sealed class OAuthDeviceFlowProviderTests
{
    private static DeviceFlowProvider NewProvider(DeviceFlowProviderParams parameters)
        => new(parameters);

    private static DeviceFlowProviderParams ValidStartParams(
        OAuthTestExecutor executor,
        Action<DeviceFlowConfig>? configure = null)
    {
        var config = new DeviceFlowConfig
        {
            DeviceAuthUrl = "https://example.com/device",
            TokenUrl = "https://example.com/token",
            ClientId = "test-client",
        };
        configure?.Invoke(config);

        return new DeviceFlowProviderParams { HttpClient = executor, Config = config };
    }

    private static void AssertConfig(DeviceFlowConfig expected, DeviceFlowConfig actual)
    {
        Assert.Equal(expected.DeviceAuthUrl, actual.DeviceAuthUrl);
        Assert.Equal(expected.TokenUrl, actual.TokenUrl);
        Assert.Equal(expected.ClientId, actual.ClientId);
        Assert.Equal(expected.Scopes, actual.Scopes);
        Assert.Equal(expected.UserAgent, actual.UserAgent);
    }

    [Fact]
    public void ConstructionKeepsTheConfiguration()
    {
        var cases = new (string Name, DeviceFlowProviderParams Parameters)[]
        {
            ("empty params", new DeviceFlowProviderParams()),
            (
                "with config",
                new DeviceFlowProviderParams
                {
                    Config = new DeviceFlowConfig
                    {
                        DeviceAuthUrl = "https://example.com/device",
                        TokenUrl = "https://example.com/token",
                        ClientId = "test-client",
                    },
                }),
            (
                "with credentials",
                new DeviceFlowProviderParams
                {
                    Credentials = new OAuthCredentials
                    {
                        AccessToken = "test-token",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    },
                }),
            (
                "with token exchanger",
                new DeviceFlowProviderParams
                {
                    TokenExchanger = new MockTokenExchanger
                    {
                        Token = "exchanged-token",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    },
                }),
            (
                "with onRefreshed callback",
                new DeviceFlowProviderParams { OnRefreshed = (_, _) => null }),
            (
                "with all fields",
                new DeviceFlowProviderParams
                {
                    Config = new DeviceFlowConfig
                    {
                        DeviceAuthUrl = "https://example.com/device",
                        TokenUrl = "https://example.com/token",
                        ClientId = "test-client",
                        Scopes = ["read", "write"],
                        UserAgent = "test-agent",
                    },
                    HttpClient = new OAuthTestExecutor(),
                    Credentials = new OAuthCredentials
                    {
                        AccessToken = "access-token",
                        RefreshToken = "refresh-token",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    },
                    TokenExchanger = new MockTokenExchanger { Token = "exchanged" },
                    OnRefreshed = (_, _) => null,
                }),
        };

        foreach (var (_, parameters) in cases)
        {
            var provider = NewProvider(parameters);
            Assert.NotNull(provider);
            AssertConfig(parameters.Config, provider.Config);
        }
    }

    [Fact]
    public void StartValidation()
    {
        var cases = new (string Name, DeviceFlowProviderParams Parameters, string WantError)[]
        {
            ("nil http client", new DeviceFlowProviderParams(), "http client is nil"),
            (
                "empty device auth URL",
                new DeviceFlowProviderParams { HttpClient = new OAuthTestExecutor() },
                "device authorization URL is empty"),
            (
                "empty client ID",
                new DeviceFlowProviderParams
                {
                    HttpClient = new OAuthTestExecutor(),
                    Config = new DeviceFlowConfig { DeviceAuthUrl = "https://example.com/device" },
                },
                "client_id is empty"),
        };

        foreach (var (_, parameters, wantError) in cases)
        {
            var provider = NewProvider(parameters);
            var error = Assert.Throws<InvalidOperationException>(
                () => provider.Start(CancellationToken.None));
            Assert.Equal(wantError, error.Message);
        }
    }

    [Fact]
    public void StartSuccess()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("https://example.com/device", request.Url);
                Assert.Equal(
                    "application/x-www-form-urlencoded",
                    OAuthTestSupport.Header(request, "Content-Type"));
                Assert.Equal("application/json", OAuthTestSupport.Header(request, "Accept"));
                Assert.Equal("axonhub-test", OAuthTestSupport.Header(request, "User-Agent"));

                return OAuthTestSupport.Json(
                    """
                    {"device_code":"device-code-123","user_code":"USER-CODE-456","verification_uri":"https://example.com/verify","verification_uri_complete":"https://example.com/verify?code=USER-CODE-456","expires_in":900,"interval":5}
                    """);
            },
        };

        var provider = NewProvider(ValidStartParams(
            executor,
            config =>
            {
                config.Scopes = ["read:user", "repo"];
                config.UserAgent = "axonhub-test";
            }));

        var response = provider.Start(CancellationToken.None);

        Assert.Equal("device-code-123", response.DeviceCode);
        Assert.Equal("USER-CODE-456", response.UserCode);
        Assert.Equal("https://example.com/verify", response.VerificationUri);
        Assert.Equal("https://example.com/verify?code=USER-CODE-456", response.CompleteUri);
        Assert.Equal(900, response.ExpiresIn);
        Assert.Equal(5, response.Interval);

        var form = OAuthTestSupport.ParseForm(executor.SingleRequest());
        Assert.Equal("test-client", form["client_id"]);
        Assert.Equal("read:user repo", form["scope"]);
    }

    [Fact]
    public void StartWithoutScopes()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"device_code":"device-code","user_code":"USER-CODE","verification_uri":"https://example.com/verify","expires_in":900,"interval":5}
                """),
        };

        var provider = NewProvider(ValidStartParams(executor));

        _ = provider.Start(CancellationToken.None);

        var form = OAuthTestSupport.ParseForm(executor.SingleRequest());
        Assert.False(form.ContainsKey("scope"));
    }

    [Fact]
    public void StartErrorResponse()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """{"error":"invalid_client","error_description":"client not found"}""",
                statusCode: 400),
        };

        var provider = NewProvider(ValidStartParams(executor));

        var error = Assert.Throws<WrappedException>(() => provider.Start(CancellationToken.None));
        Assert.Contains("device authorization request failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartMissingDeviceCode()
    {
        var executor = new OAuthTestExecutor { Handler = _ => OAuthTestSupport.Json("{}") };
        var provider = NewProvider(ValidStartParams(executor));

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Start(CancellationToken.None));
        Assert.Equal("device authorization response missing device_code", error.Message);
    }

    [Fact]
    public void PollValidation()
    {
        var cases = new (string Name, DeviceFlowProviderParams Parameters, string DeviceCode, string WantError)[]
        {
            ("nil http client", new DeviceFlowProviderParams(), "test-code", "http client is nil"),
            (
                "empty token URL",
                new DeviceFlowProviderParams { HttpClient = new OAuthTestExecutor() },
                "test-code",
                "token URL is empty"),
            (
                "empty device code",
                new DeviceFlowProviderParams
                {
                    HttpClient = new OAuthTestExecutor(),
                    Config = new DeviceFlowConfig { TokenUrl = "https://example.com/token" },
                },
                string.Empty,
                "device_code is empty"),
        };

        foreach (var (_, parameters, deviceCode, wantError) in cases)
        {
            var provider = NewProvider(parameters);
            var error = Assert.Throws<InvalidOperationException>(
                () => provider.Poll(CancellationToken.None, deviceCode));
            Assert.Equal(wantError, error.Message);
        }
    }

    [Fact]
    public void PollSuccess()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("https://example.com/token", request.Url);

                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-token-123","refresh_token":"refresh-token-456","token_type":"Bearer","expires_in":3600,"scope":"read:user repo"}
                    """);
            },
        };

        var provider = NewProvider(ValidStartParams(executor));

        var creds = provider.Poll(CancellationToken.None, "device-code-123");

        var form = OAuthTestSupport.ParseForm(executor.SingleRequest());
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
        Assert.Equal("test-client", form["client_id"]);
        Assert.Equal("device-code-123", form["device_code"]);

        Assert.Equal("test-client", creds.ClientId);
        Assert.Equal("access-token-123", creds.AccessToken);
        Assert.Equal("refresh-token-456", creds.RefreshToken);
        Assert.Equal("Bearer", creds.TokenType);
        Assert.Equal(["read:user", "repo"], creds.Scopes);
        Assert.NotEqual(DateTimeOffset.MinValue, creds.ExpiresAt);

        var stored = provider.GetCredentials();
        Assert.NotNull(stored);
        Assert.Equal(creds.AccessToken, stored!.AccessToken);
    }

    [Fact]
    public void PollErrorResponses()
    {
        var cases = new (string ErrorCode, string WantError)[]
        {
            ("authorization_pending", "authorization_pending"),
            ("slow_down", "slow_down"),
            ("expired_token", "expired_token"),
            ("access_denied", "access_denied"),
            ("custom_error", "token request failed: custom_error"),
        };

        foreach (var (errorCode, wantError) in cases)
        {
            var executor = new OAuthTestExecutor
            {
                Handler = _ => OAuthTestSupport.Json(
                    $$"""
                    {"error":"{{errorCode}}","error_description":"detailed description"}
                    """),
            };

            var provider = NewProvider(ValidStartParams(executor));

            var error = Assert.Throws<InvalidOperationException>(
                () => provider.Poll(CancellationToken.None, "device-code"));
            Assert.Contains(wantError, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetTokenWithoutCredentials()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetToken(CancellationToken.None));
        Assert.Equal("credentials is nil", error.Message);
    }

    [Fact]
    public void GetTokenWithEmptyAccessToken()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            Credentials = new OAuthCredentials
            {
                AccessToken = string.Empty,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetToken(CancellationToken.None));
        Assert.Equal("access token is empty", error.Message);
    }

    [Fact]
    public void GetTokenReturnsValidToken()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            Credentials = new OAuthCredentials
            {
                AccessToken = "valid-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        Assert.Equal("valid-token", provider.GetToken(CancellationToken.None));
    }

    [Fact]
    public void GetTokenUsesTokenExchanger()
    {
        var exchanger = new MockTokenExchanger
        {
            Token = "exchanged-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            TokenExchanger = exchanger,
            Credentials = new OAuthCredentials
            {
                AccessToken = "oauth-access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        Assert.Equal("exchanged-token", provider.GetToken(CancellationToken.None));
        Assert.Equal(1, exchanger.ExchangeWithClientCalls);
    }

    [Fact]
    public void GetTokenReportsTokenExchangerError()
    {
        var exchanger = new MockTokenExchanger { Error = new InvalidOperationException("exchange failed") };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            TokenExchanger = exchanger,
            Credentials = new OAuthCredentials
            {
                AccessToken = "oauth-access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        var error = Assert.Throws<WrappedException>(
            () => provider.GetToken(CancellationToken.None));
        Assert.Contains("exchange failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTokenRefreshesExpiredToken()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"access_token":"new-access-token","refresh_token":"new-refresh-token","token_type":"Bearer","expires_in":3600}
                """),
        };

        var refreshedCalls = 0;
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                DeviceAuthUrl = "https://example.com/device",
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
            Credentials = new OAuthCredentials
            {
                AccessToken = "expired-token",
                RefreshToken = "old-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            OnRefreshed = (_, _) =>
            {
                Interlocked.Increment(ref refreshedCalls);
                return null;
            },
        });

        Assert.Equal("new-access-token", provider.GetToken(CancellationToken.None));

        var form = OAuthTestSupport.ParseForm(executor.SingleRequest());
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("test-client", form["client_id"]);
        Assert.Equal("old-refresh-token", form["refresh_token"]);
        Assert.Equal(1, Volatile.Read(ref refreshedCalls));

        var stored = provider.GetCredentials();
        Assert.NotNull(stored);
        Assert.Equal("new-access-token", stored!.AccessToken);
        Assert.Equal("new-refresh-token", stored.RefreshToken);
    }

    [Fact]
    public void GetTokenRefreshWithoutRefreshToken()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            Config = new DeviceFlowConfig { TokenUrl = "https://example.com/token" },
            Credentials = new OAuthCredentials
            {
                AccessToken = "expired-token",
                RefreshToken = string.Empty,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetToken(CancellationToken.None));
        Assert.Equal("refresh_token is empty", error.Message);
    }

    [Fact]
    public async Task GetTokenDeduplicatesConcurrentRefreshes()
    {
        var calls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = _ =>
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(50);

                return OAuthTestSupport.Json(
                    """
                    {"access_token":"refreshed-token","refresh_token":"refresh-token","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
            Credentials = new OAuthCredentials
            {
                AccessToken = "expired-token",
                RefreshToken = "old-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
        });

        var tokens = new string[10];
        var errors = new Exception?[10];
        var tasks = new Task[10];

        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    tokens[index] = provider.GetToken(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    errors[index] = ex;
                }
            });
        }

        await Task.WhenAll(tasks);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(errors[i] is null, "goroutine " + i + " failed: " + errors[i]);
            Assert.Equal(tokens[0], tokens[i]);
        }

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void CredentialsManagementCopiesOnBothSides()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
        });

        Assert.Null(provider.GetCredentials());

        var original = new OAuthCredentials
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        provider.UpdateCredentials(original);

        var stored = provider.GetCredentials();
        Assert.NotNull(stored);
        Assert.Equal("access-1", stored.AccessToken);
        Assert.Equal("refresh-1", stored.RefreshToken);
        Assert.NotEqual(DateTimeOffset.MinValue, stored.ExpiresAt);

        stored.AccessToken = "modified";
        Assert.Equal("access-1", provider.GetCredentials()!.AccessToken);

        original.AccessToken = "original-modified";
        Assert.Equal("access-1", provider.GetCredentials()!.AccessToken);

        provider.UpdateCredentials(null);
        Assert.Null(provider.GetCredentials());
    }

    [Fact]
    public void OnRefreshedReceivesFreshCredentials()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"access_token":"new-token","refresh_token":"new-refresh","token_type":"Bearer","expires_in":3600}
                """),
        };

        OAuthCredentials? received = null;
        var callbackCalls = 0;

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
            Credentials = new OAuthCredentials
            {
                AccessToken = "expired",
                RefreshToken = "old-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            OnRefreshed = (_, creds) =>
            {
                Interlocked.Increment(ref callbackCalls);
                received = creds;
                return null;
            },
        });

        _ = provider.GetToken(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref callbackCalls));
        Assert.NotNull(received);
        Assert.Equal("new-token", received!.AccessToken);
        Assert.Equal("new-refresh", received.RefreshToken);
    }

    [Fact]
    public void OnRefreshedFailureDoesNotFailGetToken()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"access_token":"new-token","refresh_token":"new-refresh","token_type":"Bearer","expires_in":3600}
                """),
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
            Credentials = new OAuthCredentials
            {
                AccessToken = "expired",
                RefreshToken = "old-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            OnRefreshed = (_, _) => new InvalidOperationException("persistence failed"),
        });

        Assert.Equal("new-token", provider.GetToken(CancellationToken.None));
    }

    [Fact]
    public void ConcurrentCredentialUpdatesAndReadsAreSafe()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
        });

        Parallel.For(
            0,
            100,
            _ => provider.UpdateCredentials(new OAuthCredentials
            {
                AccessToken = "token",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));

        Parallel.For(0, 100, index => _ = provider.GetCredentials());

        Assert.NotNull(provider.GetCredentials());
    }

    [Fact]
    public void RefreshValidation()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(CancellationToken.None, null));
        Assert.Equal("nil credentials", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(CancellationToken.None, new OAuthCredentials()));
        Assert.Equal("refresh_token is empty", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(
                CancellationToken.None,
                new OAuthCredentials { RefreshToken = "refresh" }));
        Assert.Equal("token URL is empty", error.Message);
    }

    [Fact]
    public void RefreshSuccess()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"access_token":"new-access","refresh_token":"new-refresh","token_type":"Bearer","expires_in":3600}
                """),
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
        });

        var oldCredentials = new OAuthCredentials
        {
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
        };

        var newCredentials = provider.Refresh(CancellationToken.None, oldCredentials);

        var form = OAuthTestSupport.ParseForm(executor.SingleRequest());
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("test-client", form["client_id"]);
        Assert.Equal("old-refresh", form["refresh_token"]);
        Assert.Equal("new-access", newCredentials.AccessToken);
        Assert.Equal("new-refresh", newCredentials.RefreshToken);
    }

    [Fact]
    public void RefreshPreservesRefreshTokenWhenNotReturned()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """{"access_token":"new-access","token_type":"Bearer","expires_in":3600}"""),
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
            },
        });

        var newCredentials = provider.Refresh(
            CancellationToken.None,
            new OAuthCredentials
            {
                AccessToken = "old-access",
                RefreshToken = "old-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            });

        Assert.Equal("new-access", newCredentials.AccessToken);
        Assert.Equal("old-refresh", newCredentials.RefreshToken);
    }

    [Fact]
    public void RefreshSendsConfiguredUserAgent()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                Assert.Equal("custom-agent/1.0", OAuthTestSupport.Header(request, "User-Agent"));
                return OAuthTestSupport.Json(
                    """{"access_token":"new-token","token_type":"Bearer","expires_in":3600}""");
            },
        };

        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = executor,
            Config = new DeviceFlowConfig
            {
                TokenUrl = "https://example.com/token",
                ClientId = "test-client",
                UserAgent = "custom-agent/1.0",
            },
        });

        _ = provider.Refresh(
            CancellationToken.None,
            new OAuthCredentials { RefreshToken = "old-refresh" });
    }

    [Fact]
    public void AutoRefreshStartStopIsIdempotent()
    {
        var provider = NewProvider(new DeviceFlowProviderParams
        {
            HttpClient = new OAuthTestExecutor(),
            Credentials = new OAuthCredentials
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        var options = new AutoRefreshOptions
        {
            Interval = TimeSpan.FromMilliseconds(10),
            RefreshBefore = TimeSpan.FromMinutes(1),
        };

        provider.StartAutoRefresh(CancellationToken.None, options);
        provider.StartAutoRefresh(CancellationToken.None, options);

        Thread.Sleep(30);

        provider.StopAutoRefresh();
        provider.StopAutoRefresh();
    }

    [Fact]
    public void NetworkErrorsAreWrapped()
    {
        var startExecutor = new OAuthTestExecutor
        {
            Handler = _ => throw new HttpRequestException("connection refused"),
        };
        var startProvider = NewProvider(ValidStartParams(startExecutor));

        var startError = Assert.Throws<WrappedException>(
            () => startProvider.Start(CancellationToken.None));
        Assert.Contains("device authorization request failed", startError.Message, StringComparison.Ordinal);

        var pollExecutor = new OAuthTestExecutor
        {
            Handler = _ => throw new HttpRequestException("connection refused"),
        };
        var pollProvider = NewProvider(ValidStartParams(pollExecutor));

        var pollError = Assert.Throws<WrappedException>(
            () => pollProvider.Poll(CancellationToken.None, "device-code"));
        Assert.Contains("token request failed", pollError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonIsReported()
    {
        var startExecutor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json("{invalid json"),
        };
        var startProvider = NewProvider(ValidStartParams(startExecutor));

        var startError = Assert.Throws<WrappedException>(
            () => startProvider.Start(CancellationToken.None));
        Assert.Contains(
            "decode device authorization response",
            startError.Message,
            StringComparison.Ordinal);

        var pollExecutor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json("{invalid json"),
        };
        var pollProvider = NewProvider(ValidStartParams(pollExecutor));

        var pollError = Assert.Throws<InvalidOperationException>(
            () => pollProvider.Poll(CancellationToken.None, "device-code"));
        Assert.Contains("decode response", pollError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeIsSplitOnWhitespace()
    {
        var cases = new (string Scope, string[]? Expected)[]
        {
            ("read:user", ["read:user"]),
            ("read:user repo write:repo", ["read:user", "repo", "write:repo"]),
            (string.Empty, null),
        };

        foreach (var (scope, expected) in cases)
        {
            var executor = new OAuthTestExecutor
            {
                Handler = _ => OAuthTestSupport.Json(
                    $$"""
                    {"access_token":"token","token_type":"Bearer","expires_in":3600,"scope":"{{scope}}"}
                    """),
            };

            var provider = NewProvider(ValidStartParams(executor));
            var creds = provider.Poll(CancellationToken.None, "device-code");

            if (expected is null)
            {
                Assert.True(creds.Scopes is null || creds.Scopes.Count == 0);
            }
            else
            {
                Assert.Equal(expected, creds.Scopes);
            }
        }
    }

    [Fact]
    public void PollKeepsIdToken()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """
                {"access_token":"access-token","refresh_token":"refresh-token","token_type":"Bearer","expires_in":3600,"id_token":"id-token-value"}
                """),
        };

        var provider = NewProvider(ValidStartParams(executor));

        var creds = provider.Poll(CancellationToken.None, "device-code");

        Assert.Equal("id-token-value", creds.IdToken);
    }

    [Fact]
    public void IntegrationFullFlow()
    {
        var tokenRequestCount = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                if (request.Url.EndsWith("/device", StringComparison.Ordinal))
                {
                    return OAuthTestSupport.Json(
                        """
                        {"device_code":"device-code-123","user_code":"USER-123","verification_uri":"https://example.com/verify","expires_in":900,"interval":5}
                        """);
                }

                var count = Interlocked.Increment(ref tokenRequestCount);
                if (count <= 2)
                {
                    return OAuthTestSupport.Json("""{"error":"authorization_pending"}""");
                }

                return OAuthTestSupport.Json(
                    """
                    {"access_token":"final-access-token","refresh_token":"final-refresh-token","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var provider = NewProvider(ValidStartParams(
            executor,
            config => config.Scopes = ["read:user"]));

        var startResponse = provider.Start(CancellationToken.None);
        Assert.NotNull(startResponse);

        OAuthCredentials? creds = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                creds = provider.Poll(CancellationToken.None, startResponse.DeviceCode);
                break;
            }
            catch (InvalidOperationException ex) when (ex.Message == "authorization_pending")
            {
                // Keep polling, like the reference loop.
            }
        }

        Assert.NotNull(creds);
        Assert.Equal("final-access-token", creds!.AccessToken);
        Assert.Equal("final-refresh-token", creds.RefreshToken);
    }
}

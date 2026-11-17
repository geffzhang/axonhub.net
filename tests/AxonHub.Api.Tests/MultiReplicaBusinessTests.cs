using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using AxonHub.Api.Management;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

/// <summary>Real-process acceptance: no TestServer, mocked stores, or test-only API routes.</summary>
public sealed class MultiReplicaBusinessTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "MultiReplica")]
    public async Task SharedBusinessStateAdmissionOidcAndFailureRecovery()
    {
        await using var environment = new MultiReplicaEnvironment();
        try
        {
            await environment.StartAsync();
            var a = environment.A.Client;
            var b = environment.B.Client;

            output.WriteLine("STARTED two independent API processes: {0}, {1}",
                environment.A.Process.Id, environment.B.Process.Id);
            await SuccessfulChatAsync(a, "warm-a");
            await SuccessfulChatAsync(b, "warm-b");
            await environment.SqlAsync("UPDATE api_keys SET status='disabled' WHERE id=1");
            using (var revoked = await ChatAsync(b, "revoked"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            }

            await environment.SqlAsync("UPDATE api_keys SET status='enabled', key='sk-replica-rotated' WHERE id=1");
            using (var rotated = await ChatAsync(b, "old-key"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
            }

            a.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-replica-rotated");
            b.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-replica-rotated");
            await SuccessfulChatAsync(a, "rotated-a");
            await SuccessfulChatAsync(b, "rotated-b");
            using (var before = await b.GetFromJsonAsync<JsonDocument>("/admin/system/status"))
            {
                Assert.True(before!.RootElement.GetProperty("isInitialized").GetBoolean());
            }

            await environment.SqlAsync("UPDATE systems SET value='false' WHERE key='system_initialized'");
            using (var after = await b.GetFromJsonAsync<JsonDocument>("/admin/system/status"))
            {
                Assert.False(after!.RootElement.GetProperty("isInitialized").GetBoolean());
            }

            await environment.SqlAsync("UPDATE systems SET value='true' WHERE key='system_initialized'");
            output.WriteLine("PASS key revoke/rotate and warmed system-setting read-through, immediate next HTTP requests");

            await VerifyOidcAsync(a, b, environment);
            output.WriteLine("PASS OIDC authorize A, callback B, concurrent exchange A/B, replay and PKCE");

            environment.Upstream.Hold = true;
            var first = ChatAsync(a, "fifo-1");
            var firstArrival = await environment.Upstream.NextAsync();
            var second = ChatAsync(b, "fifo-2");
            await environment.WaitRowsAsync(1, 1);
            var third = ChatAsync(a, "fifo-3");
            await environment.WaitRowsAsync(1, 2);
            var beforeOverflow = environment.Upstream.Arrivals;
            using (var overflow = await ChatAsync(b, "fifo-overflow"))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            }

            Assert.Equal(beforeOverflow, environment.Upstream.Arrivals);
            firstArrival.TrySetResult();
            using (var response = await first.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            var secondArrival = await environment.Upstream.NextAsync();
            Assert.Equal("fifo-2", environment.Upstream.LastMessage);
            secondArrival.TrySetResult();
            using (var response = await second.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            var thirdArrival = await environment.Upstream.NextAsync();
            Assert.Equal("fifo-3", environment.Upstream.LastMessage);
            thirdArrival.TrySetResult();
            using (var response = await third.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            Assert.Equal(1, environment.Upstream.MaximumActive);
            await environment.WaitRowsAsync(0, 0);
            output.WriteLine("PASS global maxConcurrent=1, queueSize=2, FIFO upstream arrivals and overflow rejection");

            environment.Upstream.Hold = false;
            var beforeOutage = environment.Upstream.Arrivals;
            await environment.Database.StopAsync();
            try
            {
                using var failed = await ChatAsync(b, "database-down");
                Assert.False(failed.IsSuccessStatusCode);
                Assert.Equal(beforeOutage, environment.Upstream.Arrivals);
            }
            finally
            {
                await environment.Database.StartAsync();
            }

            await MultiReplicaEnvironment.EventuallyAsync(async () =>
            {
                using var response = await ChatAsync(b, "database-restored");
                return response.IsSuccessStatusCode;
            }, TimeSpan.FromSeconds(90), "business recovery after private database restart");
            output.WriteLine("PASS database outage fails closed with zero upstream arrivals; restart recovers");

            environment.Upstream.Hold = true;
            var abandoned = ChatAsync(a, "crash-holder");
            var abandonedArrival = await environment.Upstream.NextAsync();
            await environment.WaitRowsAsync(1, 0);
            await environment.A.KillAsync();
            try
            {
                using var ignored = await abandoned;
            }
            catch (HttpRequestException)
            {
                // A hard-killed HTTP server may reset the caller connection.
            }

            await environment.WaitRowsAsync(1, 0);
            await MultiReplicaEnvironment.EventuallyAsync(async () =>
                await environment.ScalarAsync("SELECT count(*) FROM orleansmembershiptable WHERE status=6") > 0,
                TimeSpan.FromSeconds(180), "Orleans marks killed silo dead");
            var arrivalsBeforeSurvivor = environment.Upstream.Arrivals;
            using (var denied = await ChatAsync(b, "must-not-over-admit"))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            }

            Assert.Equal(arrivalsBeforeSurvivor, environment.Upstream.Arrivals);
            Assert.Equal(1L, await environment.ScalarAsync("SELECT count(*) FROM axonhub_channel_admissions WHERE granted"));
            abandonedArrival.TrySetResult();
            environment.Upstream.Hold = false;
            await environment.SqlAsync("""
                INSERT INTO channels(id,type,base_url,name,status,credentials,supported_models,default_test_model,settings)
                SELECT 2,type,base_url,'recovery-channel','enabled',credentials,'["recovery-model"]',default_test_model,settings
                FROM channels WHERE id=1
                """);
            await MultiReplicaEnvironment.EventuallyAsync(async () =>
            {
                using var response = await ChatAsync(b, "survivor-business", "recovery-model");
                return response.IsSuccessStatusCode;
            }, TimeSpan.FromSeconds(60), "surviving API business on independent channel after silo death");
            output.WriteLine("PASS killed holder retains durable permit, no over-admission; dead silo detected and surviving API serves new channel");
        }
        finally
        {
            output.WriteLine(environment.Diagnostics);
        }
    }

    private static Task<HttpResponseMessage> ChatAsync(HttpClient client, string text, string model = "replica-model")
        => client.PostAsJsonAsync("/v1/chat/completions",
            new { model, messages = new[] { new { role = "user", content = text } } });

    private static async Task SuccessfulChatAsync(HttpClient client, string text)
    {
        using var response = await ChatAsync(client, text);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task VerifyOidcAsync(HttpClient a, HttpClient b, MultiReplicaEnvironment environment)
    {
        using var authorize = await a.GetFromJsonAsync<JsonDocument>("/oauth/oidc/authorize/local");
        var data = authorize!.RootElement.GetProperty("data");
        var state = data.GetProperty("state").GetString();
        var query = QueryHelpers.ParseQuery(new Uri(data.GetProperty("url").GetString()!).Query);
        environment.Upstream.ExpectedChallenge = query["code_challenge"].ToString();
        Assert.NotEmpty(environment.Upstream.ExpectedChallenge);
        using var callback = await b.GetAsync("/oauth/oidc/callback/local?code=private-code&state=" + state);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var exchangeCode = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();
        Assert.NotEmpty(exchangeCode);
        using var replayCallback = await a.GetAsync("/oauth/oidc/callback/local?code=private-code&state=" + state);
        Assert.Equal("auth_failed",
            QueryHelpers.ParseQuery(replayCallback.Headers.Location!.Query)["error"].ToString());
        using (var exchangeAtA = await a.PostAsJsonAsync("/oauth/oidc/exchange", new { code = exchangeCode }))
        {
            Assert.True(exchangeAtA.IsSuccessStatusCode, await exchangeAtA.Content.ReadAsStringAsync());
        }

        using var firstReplay = await b.PostAsJsonAsync("/oauth/oidc/exchange", new { code = exchangeCode });
        Assert.False(firstReplay.IsSuccessStatusCode);
        using var nextAuthorize = await a.GetFromJsonAsync<JsonDocument>("/oauth/oidc/authorize/local");
        var nextData = nextAuthorize!.RootElement.GetProperty("data");
        var nextQuery = QueryHelpers.ParseQuery(new Uri(nextData.GetProperty("url").GetString()!).Query);
        environment.Upstream.ExpectedChallenge = nextQuery["code_challenge"].ToString();
        using var nextCallback = await b.GetAsync("/oauth/oidc/callback/local?code=private-code-2&state="
            + nextData.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Redirect, nextCallback.StatusCode);
        exchangeCode = QueryHelpers.ParseQuery(nextCallback.Headers.Location!.Query)["code"].ToString();
        Assert.NotEmpty(exchangeCode);
        var exchanges = await Task.WhenAll(
            a.PostAsJsonAsync("/oauth/oidc/exchange", new { code = exchangeCode }),
            b.PostAsJsonAsync("/oauth/oidc/exchange", new { code = exchangeCode }));
        try
        {
            Assert.Single(exchanges, response => response.IsSuccessStatusCode);
        }
        finally
        {
            foreach (var response in exchanges)
            {
                response.Dispose();
            }
        }

        using var replay = await a.PostAsJsonAsync("/oauth/oidc/exchange", new { code = exchangeCode });
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Equal(2, environment.Upstream.TokenExchanges);
    }
}

public sealed class MultiReplicaProcessTests
{
    [Fact]
    public async Task UnstartedProcessCanBeDisposed()
    {
        var process = new MultiReplicaProcess(
            "unstarted", "cleanup", "Host=127.0.0.1", "http://127.0.0.1");

        await process.DisposeAsync();
    }

    [Fact]
    public async Task FailedStartCanBeDisposedWithoutMaskingTheStartupError()
    {
        await using var process = new MultiReplicaProcess(
            "failed-start", "cleanup", "Host=127.0.0.1", "http://127.0.0.1");
        process.Process.StartInfo.WorkingDirectory = Path.Combine(
            Path.GetTempPath(), "axonhub-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(process.StartAsync);
    }
}

internal sealed class MultiReplicaEnvironment : IAsyncDisposable
{
    public PostgreSqlContainer Database { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .WithPortBinding(FreePort(), 5432).Build();
    public MultiReplicaUpstream Upstream { get; } = new();
    public MultiReplicaProcess A { get; private set; } = null!;
    public MultiReplicaProcess B { get; private set; } = null!;
    public string Diagnostics => (A?.Diagnostics ?? "") + "\n" + (B?.Diagnostics ?? "");

    public async Task StartAsync()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Database.StartAsync(startup.Token);
        await Upstream.StartAsync();
        var cluster = "business-" + Guid.NewGuid().ToString("N");
        A = new MultiReplicaProcess("a", cluster, Database.GetConnectionString(), Upstream.Url);
        await A.StartAsync();
        using (var initialized = await A.Client.PostAsJsonAsync("/admin/system/initialize", new
        {
            ownerEmail = "owner@replica.invalid",
            ownerPassword = "private-test-password",
            ownerFirstName = "Replica",
            ownerLastName = "Owner",
            brandName = "Replica acceptance",
        }))
        {
            Assert.True(initialized.IsSuccessStatusCode, await initialized.Content.ReadAsStringAsync());
        }

        await SqlAsync("""
            INSERT INTO api_keys(id,key,name,type,status,scopes,project_id,user_id)
            VALUES(1,'sk-replica','acceptance','user','enabled','["write_requests","read_channels","read_models"]',1,1);
            INSERT INTO channels(id,type,base_url,name,status,credentials,supported_models,default_test_model,settings)
            VALUES(1,'openai',@url,'controlled','enabled','{"apiKey":"private-upstream-key"}',
            '["replica-model"]','replica-model','{"rateLimit":{"maxConcurrent":1,"queueSize":2,"queueTimeoutMs":4000}}');
            """, new { url = Upstream.Url + "/v1" });
        using (var warm = await A.Client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "replica-model", messages = new[] { new { role = "user", content = "activate-before-b" } } }))
        {
            Assert.True(warm.IsSuccessStatusCode, await warm.Content.ReadAsStringAsync());
        }

        B = new MultiReplicaProcess("b", cluster, Database.GetConnectionString(), Upstream.Url);
        await B.StartAsync();
    }

    public async Task SqlAsync(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(Database.GetConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandTimeout: 10));
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.GetConnectionString());
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, commandTimeout: 10));
    }

    public Task WaitRowsAsync(int granted, int waiting)
        => EventuallyAsync(async () =>
            await ScalarAsync("SELECT count(*) FROM axonhub_channel_admissions WHERE granted") == granted
            && await ScalarAsync("SELECT count(*) FROM axonhub_channel_admissions WHERE NOT granted") == waiting,
            TimeSpan.FromSeconds(10), $"admission rows granted={granted}, waiting={waiting}");

    public static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var started = Stopwatch.StartNew();
        Exception? last = null;
        while (started.Elapsed < timeout)
        {
            try
            {
                if (await condition().WaitAsync(TimeSpan.FromSeconds(20)))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or NpgsqlException or TaskCanceledException or TimeoutException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(description, last);
    }

    public async ValueTask DisposeAsync()
    {
        if (A is not null)
        {
            await A.DisposeAsync();
        }

        if (B is not null)
        {
            await B.DisposeAsync();
        }

        await Upstream.DisposeAsync();
        await Database.DisposeAsync();
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class MultiReplicaProcess : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _lines = new();
    private bool _started;
    public Process Process { get; }
    public HttpClient Client { get; }
    public string Diagnostics => string.Join("\n", _lines);

    public MultiReplicaProcess(string name, string cluster, string connectionString, string upstream)
    {
        var dll = typeof(RequestContentHandlers).Assembly.Location;
        var url = "http://127.0.0.1:" + MultiReplicaEnvironment.FreePort();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        foreach (var pair in new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = url,
            ["ConnectionStrings__Default"] = connectionString + ";Timeout=3;Command Timeout=5",
            ["OrleansCluster__Enabled"] = "true",
            ["OrleansCluster__ClusterId"] = cluster,
            ["OrleansCluster__ServiceId"] = cluster,
            ["OrleansCluster__SiloName"] = name,
            ["OrleansCluster__AdvertisedIP"] = "127.0.0.1",
            ["OrleansCluster__SiloPort"] = MultiReplicaEnvironment.FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OrleansCluster__GatewayPort"] = "0",
            ["Server__PublicUrl"] = url,
            ["Logging__LogLevel__Default"] = "Warning",
            ["Oidc__Providers__0__id"] = "local",
            ["Oidc__Providers__0__client_id"] = "private-client",
            ["Oidc__Providers__0__client_secret"] = "private-secret",
            ["Oidc__Providers__0__auth_url"] = upstream + "/authorize",
            ["Oidc__Providers__0__token_url"] = upstream + "/token",
            ["Oidc__Providers__0__user_info_url"] = upstream + "/userinfo",
            ["Oidc__Providers__0__jit_enabled"] = "true",
            ["Oidc__Providers__0__enable_pkce"] = "true",
        })
        {
            start.Environment[pair.Key] = pair.Value;
        }

        Process = new Process { StartInfo = start };
        Process.OutputDataReceived += (_, args) => Capture(args.Data);
        Process.ErrorDataReceived += (_, args) => Capture(args.Data);
        Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(20),
        };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-replica");
    }

    private void Capture(string? line)
    {
        if (line is null)
        {
            return;
        }

        _lines.Enqueue(line);
        while (_lines.Count > 160)
        {
            _lines.TryDequeue(out _);
        }
    }

    public async Task StartAsync()
    {
        _started = Process.Start();
        Assert.True(_started);
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
        await MultiReplicaEnvironment.EventuallyAsync(async () =>
        {
            if (Process.HasExited)
            {
                throw new InvalidOperationException("API exited: " + Diagnostics);
            }

            using var response = await Client.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }, TimeSpan.FromSeconds(90), "API readiness: " + Process.Id);
    }

    public async Task KillAsync()
    {
        if (_started && !Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        Client.Dispose();
        Process.Dispose();
    }
}

internal sealed class MultiReplicaUpstream : IAsyncDisposable
{
    private readonly Channel<TaskCompletionSource> _arrivals = Channel.CreateUnbounded<TaskCompletionSource>();
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _app;
    private int _active;
    private int _arrivalsCount;
    private int _maximumActive;
    private int _tokenExchanges;
    public bool Hold { get; set; }
    public string LastMessage { get; private set; } = "";
    public string ExpectedChallenge { get; set; } = "";
    public string Url { get; } = "http://127.0.0.1:" + MultiReplicaEnvironment.FreePort();
    public int Arrivals => Volatile.Read(ref _arrivalsCount);
    public int MaximumActive => Volatile.Read(ref _maximumActive);
    public int TokenExchanges => Volatile.Read(ref _tokenExchanges);

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);
        _app = builder.Build();
        _app.MapPost("/token", async context =>
        {
            var form = await context.Request.ReadFormAsync();
            var verifier = form["code_verifier"].ToString();
            var challenge = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
            if (challenge != ExpectedChallenge || verifier.Length == 0)
            {
                context.Response.StatusCode = 400;
                return;
            }

            Interlocked.Increment(ref _tokenExchanges);
            await context.Response.WriteAsJsonAsync(new { access_token = "local-access", token_type = "Bearer" });
        });
        _app.MapGet("/userinfo", () => Results.Json(new
        {
            sub = "replica-subject",
            email = "oidc@replica.invalid",
            email_verified = true,
            name = "Replica OIDC",
        }));
        _app.MapPost("/v1/chat/completions", async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            LastMessage = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var model = document.RootElement.GetProperty("model").GetString();
            Interlocked.Increment(ref _arrivalsCount);
            var active = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maximumActive, Math.Max(active, MaximumActive));
            try
            {
                if (Hold)
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _arrivals.Writer.WriteAsync(release);
                    await release.Task.WaitAsync(_stop.Token);
                }

                await context.Response.WriteAsJsonAsync(new
                {
                    id = "chatcmpl-private",
                    @object = "chat.completion",
                    created = 1700000000,
                    model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "controlled" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                });
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        });
        await _app.StartAsync();
    }

    public async Task<TaskCompletionSource> NextAsync()
        => await _arrivals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _stop.Dispose();
    }
}

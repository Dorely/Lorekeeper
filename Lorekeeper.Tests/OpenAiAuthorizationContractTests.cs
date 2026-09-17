using System.Net;
using Lorekeeper.Authorization;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class OpenAiAuthorizationContractTests
{
    [Fact]
    public void NewFlowCancelsPreviousStateAndCallbacksAreSingleUse()
    {
        var registry = new OpenAiAuthorizationFlowRegistry(TimeProvider.System);
        var first = registry.Start(7, AuthorizationTarget);
        var firstState = ReadQueryValue(first.AuthorizationTarget, "state");
        var second = registry.Start(7, AuthorizationTarget);
        var secondState = ReadQueryValue(second.AuthorizationTarget, "state");

        Assert.Null(registry.Claim(firstState));
        var claim = registry.Claim(secondState);
        Assert.NotNull(claim);
        Assert.Equal(43, firstState.Length);
        Assert.Equal(43, claim!.CodeVerifier.Length);
        Assert.NotEqual(firstState, claim.CodeVerifier);
        Assert.Null(registry.Claim(secondState));
        Assert.True(registry.TryBeginCredentialCommit(claim));
        Assert.NotNull(registry.Complete(claim, OpenAiAuthorizationFlowStatus.Succeeded, "Connected."));
        Assert.Equal(OpenAiAuthorizationFlowStatus.Succeeded, registry.Get(7)!.Status);
    }

    [Fact]
    public void DenialAndExpiryAreTerminalWithoutProducingAReusableClaim()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var registry = new OpenAiAuthorizationFlowRegistry(clock);
        var deniedFlow = registry.Start(3, AuthorizationTarget);
        var deniedState = ReadQueryValue(deniedFlow.AuthorizationTarget, "state");

        var deniedClaim = registry.Claim(deniedState, "Authorization was denied.");

        Assert.NotNull(deniedClaim);
        Assert.Equal(OpenAiAuthorizationFlowStatus.Denied, registry.Get(3)!.Status);
        Assert.Null(registry.Claim(deniedState));

        var expiringFlow = registry.Start(3, AuthorizationTarget);
        var expiringState = ReadQueryValue(expiringFlow.AuthorizationTarget, "state");
        clock.Advance(OpenAiAuthorizationFlowRegistry.FlowLifetime + TimeSpan.FromSeconds(1));

        Assert.Null(registry.Claim(expiringState));
        Assert.Equal(OpenAiAuthorizationFlowStatus.Expired, registry.Get(3)!.Status);

        var exchangeFlow = registry.Start(3, AuthorizationTarget);
        var exchangeClaim = registry.Claim(ReadQueryValue(exchangeFlow.AuthorizationTarget, "state"));
        clock.Advance(OpenAiAuthorizationFlowRegistry.FlowLifetime + TimeSpan.FromSeconds(1));

        Assert.False(registry.TryBeginCredentialCommit(exchangeClaim!));
        Assert.Equal(OpenAiAuthorizationFlowStatus.Expired, registry.Get(3)!.Status);
    }

    [Fact]
    public void CancellationInvalidatesThePendingState()
    {
        var registry = new OpenAiAuthorizationFlowRegistry(TimeProvider.System);
        var flow = registry.Start(5, AuthorizationTarget);
        var state = ReadQueryValue(flow.AuthorizationTarget, "state");

        var canceled = registry.Cancel(5);

        Assert.Equal(OpenAiAuthorizationFlowStatus.Canceled, canceled!.Status);
        Assert.Null(registry.Claim(state));
    }

    [Fact]
    public async Task SimultaneousCallbacksYieldExactlyOneClaim()
    {
        var registry = new OpenAiAuthorizationFlowRegistry(TimeProvider.System);
        var flow = registry.Start(11, AuthorizationTarget);
        var state = ReadQueryValue(flow.AuthorizationTarget, "state");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await gate.Task;
                return registry.Claim(state);
            })
            .ToArray();
        gate.SetResult();
        var claims = await Task.WhenAll(attempts);

        Assert.Single(claims, claim => claim is not null);
    }

    [Fact]
    public async Task AccountOperationsSerializeRefreshAndCredentialCommitWork()
    {
        var coordinator = new OpenAiAccountOperationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunAsync(
            19,
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;
        var second = coordinator.RunAsync(
            19,
            _ =>
            {
                secondEntered.SetResult();
                return Task.CompletedTask;
            });

        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StartingAndReplacingReconnectPreservesExistingCredentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var contextFactory = new TestDbContextFactory(connection);
        await using (var context = contextFactory.CreateDbContext())
            await context.Database.MigrateAsync();
        var database = new AppDatabaseOperationFactory(
            contextFactory,
            new AppDatabaseWriteCoordinator(),
            new ProjectMutationCoordinator());
        var account = new OpenAiAccount
        {
            DisplayName = "Existing OpenAI account",
            ExternalAccountId = "external-account",
        };
        await using (var operation = await database.OpenWriteAsync())
        {
            await operation.Repositories.OpenAiAccounts.AddAsync(account);
            await operation.SaveChangesAsync();
            await operation.Repositories.OAuthTokens.ReplaceForAccountAsync(
                account.Id,
                new OAuthToken
                {
                    OpenAiAccountId = account.Id,
                    AccessToken = "existing-access-token",
                    RefreshToken = "existing-refresh-token",
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                });
            await operation.SaveChangesAsync();
        }

        var coordinator = new OpenAiAccountOperationCoordinator();
        var httpClients = new TestHttpClientFactory();
        var tokenService = new OpenAiAccountTokenService(
            database,
            httpClients,
            coordinator,
            TimeProvider.System,
            NullLogger<OpenAiAccountTokenService>.Instance);
        var authorization = new OpenAiAccountAuthorizationService(
            database,
            httpClients,
            new ValidCallbackOriginValidator(),
            new BrowserExternalAuthorizationLauncher(),
            new OpenAiAuthorizationFlowRegistry(TimeProvider.System),
            coordinator,
            tokenService,
            TimeProvider.System,
            NullLogger<OpenAiAccountAuthorizationService>.Instance);

        await authorization.StartAsync(account.Id);
        await authorization.StartAsync(account.Id);

        await using var read = await database.OpenReadAsync();
        var persisted = await read.Repositories.OAuthTokens.GetLatestForAccountAsync(account.Id);
        var persistedAccount = await read.Repositories.OpenAiAccounts.GetByIdAsync(account.Id);
        Assert.Equal("existing-access-token", persisted!.AccessToken);
        Assert.Equal("existing-refresh-token", persisted.RefreshToken);
        Assert.Equal("external-account", persistedAccount!.ExternalAccountId);
    }

    [Fact]
    public async Task ExternalAuthorizationTargetsAreAllowlistedAndBrowserRequiresUserActivation()
    {
        var launcher = new BrowserExternalAuthorizationLauncher();
        var openAi = new Uri("https://auth.openai.com/oauth/authorize?state=test");
        var github = new Uri("https://github.com/login/device?user_code=ABCD");

        var result = await launcher.LaunchAsync(openAi);

        Assert.Equal(ExternalAuthorizationLaunchDisposition.UserActivationRequired, result.Disposition);
        Assert.Equal(openAi, result.Target);
        Assert.Equal(github, ExternalAuthorizationTargetValidator.Validate(github));
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAuthorizationTargetValidator.Validate(new Uri("http://auth.openai.com/oauth/authorize")));
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAuthorizationTargetValidator.Validate(new Uri("https://auth.openai.com.evil.example/oauth/authorize")));
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAuthorizationTargetValidator.Validate(new Uri("https://auth.openai.com:444/oauth/authorize")));
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAuthorizationTargetValidator.Validate(new Uri("https://github.com/login/oauth/authorize")));
    }

    [Fact]
    public void CallbackOriginMustMatchAnActiveLorekeeperLoopbackOrigin()
    {
        var valid = OpenAiCallbackOriginValidator.Validate(
            "http://localhost:1455/auth/callback",
            ["http://localhost:1455"]);
        var wrongPort = OpenAiCallbackOriginValidator.Validate(
            "http://localhost:1455/auth/callback",
            ["http://localhost:1555"]);
        var remote = OpenAiCallbackOriginValidator.Validate(
            "https://example.com/auth/callback",
            ["https://example.com"]);
        var wrongPath = OpenAiCallbackOriginValidator.Validate(
            "http://localhost:1455/settings/providers",
            ["http://localhost:1455"]);

        Assert.True(valid.IsValid);
        Assert.False(wrongPort.IsValid);
        Assert.Contains("callback port", wrongPort.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(remote.IsValid);
        Assert.False(wrongPath.IsValid);
    }

    [Fact]
    public void CompletionPageIsStandaloneAndEncodesCallbackMessages()
    {
        var html = OpenAiAuthorizationCompletionPage.Render(
            new OpenAiAuthorizationCallbackResult(
                OpenAiAuthorizationFlowStatus.Failed,
                "Failed <script>alert('no')</script>"));

        Assert.Contains("return to Lorekeeper", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("window.location", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, true)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_grant", true)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_token", true)]
    [InlineData(HttpStatusCode.BadRequest, "temporarily_unavailable", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, false)]
    public void RefreshRejectionClassificationSeparatesTerminalFromTransientFailures(
        HttpStatusCode statusCode,
        string? errorCode,
        bool expected) =>
        Assert.Equal(
            expected,
            OpenAiAccountTokenService.IsTerminalRefreshRejection(statusCode, errorCode));

    private static Uri AuthorizationTarget(string state, string verifier)
    {
        Assert.Equal(43, verifier.Length);
        return new Uri($"https://auth.openai.com/oauth/authorize?state={Uri.EscapeDataString(state)}");
    }

    private static string ReadQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(parts[1]);
        }

        throw new InvalidOperationException($"Query key '{key}' was not found.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        public AppDbContext CreateDbContext() =>
            new(_options, NullLogger<AppDbContext>.Instance);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ValidCallbackOriginValidator : IOpenAiCallbackOriginValidator
    {
        public OpenAiCallbackOriginValidation Validate() =>
            new(true, new Uri("http://localhost:1455/auth/callback"), string.Empty);
    }
}

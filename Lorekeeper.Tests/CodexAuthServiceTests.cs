using System.Net;
using System.Text;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class CodexAuthServiceTests
{
    [Fact]
    public async Task GetValidTokenAsync_UnauthorizedRefreshRequiresReconnectWithoutDeletingToken()
    {
        var token = ExpiredToken(101);
        var repository = new FakeOAuthTokenRepository(token);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"invalid_grant","error_description":"refresh token expired"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var service = CreateService(repository, handler);

        var first = await service.GetValidTokenAsync(token.ProviderId);
        var second = await service.GetValidTokenAsync(token.ProviderId);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Same(token, repository.Token);
        Assert.Equal(0, repository.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_InvalidGrantRequiresReconnect()
    {
        var token = ExpiredToken(102);
        var repository = new FakeOAuthTokenRepository(token);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
        });
        var service = CreateService(repository, handler);

        var result = await service.GetValidTokenAsync(token.ProviderId);

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Same(token, repository.Token);
    }

    [Fact]
    public async Task GetValidTokenAsync_ServerFailureStillFailsLoudly()
    {
        var token = ExpiredToken(103);
        var repository = new FakeOAuthTokenRepository(token);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("service unavailable"),
        });
        var service = CreateService(repository, handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetValidTokenAsync(token.ProviderId));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_SeparateScopesShareOneSuccessfulRefresh()
    {
        var token = ExpiredToken(104);
        var store = new FakeOAuthTokenStore(token);
        var firstRepository = new FakeOAuthTokenRepository(store);
        var secondRepository = new FakeOAuthTokenRepository(store);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"fresh-access-token","expires_in":3600,"refresh_token":"rotated-refresh-token"}""",
                Encoding.UTF8,
                "application/json"),
        }, TimeSpan.FromMilliseconds(50));
        var firstService = CreateService(firstRepository, handler);
        var secondService = CreateService(secondRepository, handler);

        var results = await Task.WhenAll(
            firstService.GetValidTokenAsync(token.ProviderId),
            secondService.GetValidTokenAsync(token.ProviderId));

        Assert.All(results, result => Assert.Equal("fresh-access-token", result));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("rotated-refresh-token", store.Token?.RefreshToken);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_ReplacementTokenBypassesCrossScopeRejectionCache()
    {
        var token = ExpiredToken(105, "first-rejected-refresh-token");
        var store = new FakeOAuthTokenStore(token);
        var rejectedHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
        });
        var firstService = CreateService(new FakeOAuthTokenRepository(store), rejectedHandler);
        var secondService = CreateService(new FakeOAuthTokenRepository(store), rejectedHandler);

        Assert.Null(await firstService.GetValidTokenAsync(token.ProviderId));
        Assert.Null(await secondService.GetValidTokenAsync(token.ProviderId));
        Assert.Equal(1, rejectedHandler.RequestCount);

        store.Token = new OAuthToken
        {
            Id = token.Id,
            ProviderId = token.ProviderId,
            AccessToken = token.AccessToken,
            RefreshToken = "replacement-refresh-token",
            ExpiresAt = token.ExpiresAt,
            CreatedAt = token.CreatedAt,
        };
        var replacementHandler = SuccessfulRefreshHandler();
        var replacementService = CreateService(new FakeOAuthTokenRepository(store), replacementHandler);

        var result = await replacementService.GetValidTokenAsync(token.ProviderId);

        Assert.Equal("fresh-access-token", result);
        Assert.Equal(1, replacementHandler.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task GetValidTokenAsync_MissingRefreshRotationPreservesExistingCredential(
        string? refreshTokenJson)
    {
        var token = ExpiredToken(106 + (refreshTokenJson?.Length ?? 0));
        var repository = new FakeOAuthTokenRepository(token);
        var refreshProperty = refreshTokenJson is null
            ? string.Empty
            : $",\"refresh_token\":{refreshTokenJson}";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"fresh-access-token\",\"expires_in\":3600{refreshProperty}}}",
                Encoding.UTF8,
                "application/json"),
        });
        var service = CreateService(repository, handler);

        var result = await service.GetValidTokenAsync(token.ProviderId);

        Assert.Equal("fresh-access-token", result);
        Assert.Equal("rejected-refresh-token", repository.Token?.RefreshToken);
        Assert.Equal(1, repository.SaveCount);
    }

    private static CodexAuthService CreateService(
        IOAuthTokenRepository repository,
        HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Codex:RedirectUri"] = "http://localhost:1455/auth/callback",
            })
            .Build();
        return new CodexAuthService(
            repository,
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<CodexAuthService>.Instance);
    }

    private static StubHttpMessageHandler SuccessfulRefreshHandler() =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"fresh-access-token","expires_in":3600,"refresh_token":"rotated-refresh-token"}""",
                Encoding.UTF8,
                "application/json"),
        });

    private static OAuthToken ExpiredToken(int providerId, string refreshToken = "rejected-refresh-token") => new()
    {
        Id = 7,
        ProviderId = providerId,
        AccessToken = "expired-access-token",
        RefreshToken = refreshToken,
        ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
        CreatedAt = DateTime.UtcNow.AddDays(-1),
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private readonly TimeSpan _delay;
        private int _requestCount;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
            TimeSpan delay = default)
        {
            _responseFactory = responseFactory;
            _delay = delay;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken);
            return _responseFactory(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeOAuthTokenStore(OAuthToken? token)
    {
        public OAuthToken? Token { get; set; } = token;
        public int DeleteCount { get; set; }
        public int SaveCount { get; set; }
    }

    private sealed class FakeOAuthTokenRepository : IOAuthTokenRepository
    {
        private readonly FakeOAuthTokenStore _store;

        public FakeOAuthTokenRepository(OAuthToken? token)
            : this(new FakeOAuthTokenStore(token))
        {
        }

        public FakeOAuthTokenRepository(FakeOAuthTokenStore store)
        {
            _store = store;
        }

        public OAuthToken? Token => _store.Token;
        public int DeleteCount => _store.DeleteCount;
        public int SaveCount => _store.SaveCount;

        public Task<OAuthToken?> GetLatestForProviderAsync(
            int providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.Token?.ProviderId == providerId ? _store.Token : null);

        public Task<OAuthToken?> GetLatestValidForProviderAsync(
            int providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.Token?.ProviderId == providerId && _store.Token.ExpiresAt > DateTime.UtcNow
                ? _store.Token
                : null);

        public Task ReplaceForProviderAsync(
            int providerId,
            OAuthToken newToken,
            CancellationToken cancellationToken = default)
        {
            newToken.ProviderId = providerId;
            _store.Token = newToken;
            return Task.CompletedTask;
        }

        public Task DeleteForProviderAsync(int providerId, CancellationToken cancellationToken = default)
        {
            _store.DeleteCount++;
            if (_store.Token?.ProviderId == providerId)
                _store.Token = null;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _store.SaveCount++;
            return Task.CompletedTask;
        }
    }
}

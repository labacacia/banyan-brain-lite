// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banyan.Node.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Alive_ReturnsImmediately()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new HashingEmbedder());

        var resp = await fixture.Client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_ReturnsImmediately()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new HashingEmbedder());

        var resp = await fixture.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ReturnsOk_WhenSqliteAndEmbedderAreReady()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new HashingEmbedder());

        var resp = await fixture.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("checks").GetProperty("sqlite").GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("checks").GetProperty("embedder").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_ReturnsOk_WhenSqliteAndEmbedderAreReady()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new HashingEmbedder());

        var resp = await fixture.Client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ReturnsServiceUnavailable_WhenAReadinessCheckFails()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new FailingEmbedder());

        var resp = await fixture.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("checks").GetProperty("sqlite").GetProperty("status").GetString());
        Assert.Equal("degraded", body.GetProperty("checks").GetProperty("embedder").GetProperty("status").GetString());
    }

    [Fact]
    public async Task MissingRoute_ReturnsProblemDetails()
    {
        await using var fixture = await HealthAppFixture.StartAsync(new HashingEmbedder());

        var resp = await fixture.Client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }

    private sealed class HealthAppFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly SqliteMemoryStore _store;

        private HealthAppFixture(WebApplication app, SqliteMemoryStore store, HttpClient client)
        {
            _app = app;
            _store = store;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HealthAppFixture> StartAsync(IEmbedder embedder)
        {
            var store = await SqliteMemoryStore.OpenInMemoryAsync(embedder);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddProblemDetails();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(embedder);
            var app = builder.Build();
            app.UseExceptionHandler();
            app.UseStatusCodePages();
            HealthEndpoints.Map(app);
            await app.StartAsync();

            return new HealthAppFixture(app, store, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _store.DisposeAsync();
        }
    }

    private sealed class FailingEmbedder : IEmbedder
    {
        public int Dimensions => 3;
        public string ModelId => "failing-test";

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => throw new InvalidOperationException("embedder unavailable");

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => throw new InvalidOperationException("embedder unavailable");
    }
}

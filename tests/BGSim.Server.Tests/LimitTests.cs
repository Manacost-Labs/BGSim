using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace BGSim.Server.Tests;

/// <summary>Limits and switches set by BGSIM_* settings, each on a server of its own.</summary>
public sealed class LimitTests
{
    private const string Minion = """{ "cardId": "BGS_004", "attack": 1, "health": 1 }""";
    private const string Small = $$"""{ "player": { "board": [ {{Minion}} ] }, "opponent": { "board": [ {{Minion}} ] } }""";

    [Fact]
    public async Task A_body_over_the_limit_is_a_413()
    {
        await using var server = await RunningServer.StartAsync(("BGSIM_MAX_BODY_BYTES", "1024"));
        var big = Small.Replace("\"board\": [ ", "\"board\": [ " + new string(' ', 2048), StringComparison.Ordinal);

        var (response, body) = await server.PostJsonAsync("/v1/simulate", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("body_too_large", (string?)body["error"]);

        // Without a Content-Length (chunked), Kestrel stops reading at the limit.
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/simulate") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        try
        {
            var streamed = await server.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Kestrel may close the connection before the client has sent the rest of the body: refused all the same.
        }

        var (fine, _) = await server.PostJsonAsync("/v1/simulate", Small);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    [Fact]
    public async Task Runs_above_the_configured_maximum_are_a_400()
    {
        await using var server = await RunningServer.StartAsync(("BGSIM_MAX_RUNS", "100"), ("BGSIM_DEFAULT_RUNS", "100"));
        var (response, body) = await server.PostJsonAsync("/v1/simulate", Small.TrimEnd()[..^1] + """, "options": { "runs": 101 } }""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("options.runs", (string?)body["errors"]![0]!["path"]);

        var (ok, odds) = await server.PostJsonAsync("/v1/simulate", Small);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(100, (int)odds["runs"]!);
    }

    [Fact]
    public async Task The_api_key_guards_v1_but_not_health()
    {
        await using var server = await RunningServer.StartAsync(("BGSIM_API_KEY", "s3cret-key"));

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/health")).StatusCode);
        var (missing, body) = await server.GetJsonAsync("/v1/info");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("unauthorized", (string?)body["error"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetJsonAsync("/v1/info", "s3cret-kez")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetJsonAsync("/v1/info", "s3cret-key-and-more")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.PostJsonAsync("/v1/simulate", Small)).Response.StatusCode);

        var (allowed, info) = await server.GetJsonAsync("/v1/info", "s3cret-key");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((bool)info["limits"]!["apiKeyRequired"]!);

        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/v1/info");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret-key");
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(bearer)).StatusCode);
    }

    [Fact]
    public async Task A_full_server_answers_503_with_retry_after()
    {
        await using var server = await RunningServer.StartAsync(("BGSIM_MAX_CONCURRENT", "1"), ("BGSIM_QUEUE_LIMIT", "0"));
        var gate = server.App.Services.GetRequiredService<SimulationGate>();

        // Hold the only slot as a running simulation would.
        using (var held = await gate.EnterAsync(CancellationToken.None))
        {
            Assert.NotNull(held);
            var (busy, body) = await server.PostJsonAsync("/v1/simulate", Small);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("busy", (string?)body["error"]);
            Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.PostJsonAsync("/v1/fight", Small)).Response.StatusCode);
        }

        var (ok, _) = await server.PostJsonAsync("/v1/simulate", Small);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(0, gate.Admitted);
    }

    [Fact]
    public async Task The_fight_log_is_cut_at_the_limit()
    {
        await using var server = await RunningServer.StartAsync(("BGSIM_FIGHT_LOG_LINES", "2"));
        var (response, body) = await server.PostJsonAsync("/v1/fight", ApiTests.MidBoard.Replace("\"options\": { \"runs\": 500 }", "\"seed\": 7", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body["log"]!.AsArray().Count);
        Assert.True((bool)body["logTruncated"]!);
        Assert.True((int)body["logLines"]! > 2);
    }

    [Fact]
    public async Task Cors_is_off_unless_origins_are_set()
    {
        await using (var closed = await RunningServer.StartAsync())
        {
            var answer = await closed.Client.SendAsync(Preflight());
            Assert.False(answer.Headers.Contains("Access-Control-Allow-Origin"));
        }

        await using var open = await RunningServer.StartAsync(("BGSIM_CORS_ORIGINS", "https://example.org, https://other.example"), ("BGSIM_API_KEY", "k"));
        var preflight = await open.Client.SendAsync(Preflight());
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("https://example.org", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());

        static HttpRequestMessage Preflight()
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/v1/simulate");
            request.Headers.Add("Origin", "https://example.org");
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");
            return request;
        }
    }
}

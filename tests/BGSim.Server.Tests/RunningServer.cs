using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace BGSim.Server.Tests;

/// <summary>
/// The real server (Kestrel, the same pipeline as the binary) on a free loopback port, with extra <c>BGSIM_*</c> settings.
/// Kestrel rather than a test host: the body size limit is Kestrel's.
/// </summary>
internal sealed class RunningServer : IAsyncDisposable
{
    private RunningServer(WebApplication app, HttpClient client)
    {
        App = app;
        Client = client;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<RunningServer> StartAsync(params (string Name, string Value)[] settings)
    {
        var app = ServerApp.Build([], settings.Select(s => new KeyValuePair<string, string?>(s.Name, s.Value)));
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
        return new RunningServer(app, client);
    }

    public Task<HttpResponseMessage> PostAsync(string path, string json) =>
        Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public async Task<(HttpResponseMessage Response, JsonNode Body)> PostJsonAsync(string path, string json)
    {
        var response = await PostAsync(path, json);
        return (response, await BodyAsync(response));
    }

    public async Task<(HttpResponseMessage Response, JsonNode Body)> GetJsonAsync(string path, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }

        var response = await Client.SendAsync(request);
        return (response, await BodyAsync(response));
    }

    public static async Task<JsonNode> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.Content.Headers.ContentType?.MediaType == "application/json", $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    public static MediaTypeHeaderValue Json => new("application/json");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

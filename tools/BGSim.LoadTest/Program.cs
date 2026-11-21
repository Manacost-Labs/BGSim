// Measures a BGSim.Server build (docs/server.md, "Замеры"): start-up, working set idle and after load, requests per
// second with 1 and N clients. Starts the server itself, one fresh process per round, on a loopback port.
//
//   dotnet run --project tools/BGSim.LoadTest -c Release -- --server <BGSim.Server.exe | BGSim.Server.dll> [options]
//
// --rounds 3      fresh server processes, each measured in full; the spread of every number is printed
// --port 18080    loopback port of the server
// --board <file>  request body (default: board-7x7.json next to this tool)
// --runs 2000     fights per request (fixed: no precision or time stop, so every build does the same work)
// --warmup 100    requests after the first one and before the measured ones (the JIT build tiers up)
// --single 200    requests with one client
// --parallel 1000 requests with --clients clients (the working set "after load" is read after both phases)
// --clients 8
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

var options = Options.Parse(args);
var body = JsonNode.Parse(File.ReadAllText(options.Board))!.AsObject();
body["options"] = new JsonObject { ["runs"] = options.Runs, ["targetHalfWidth"] = 0, ["timeBudgetMs"] = 1000 };
var json = body.ToJsonString();

Console.WriteLine(Invariant($"server {options.Server}"));
Console.WriteLine(Invariant($"machine {Environment.MachineName} / {Environment.ProcessorCount} logical CPUs / {Environment.OSVersion}"));
Console.WriteLine(Invariant($"request {Path.GetFileName(options.Board)}, {options.Runs} runs; warm-up {options.Warmup}; single {options.Single}; parallel {options.Parallel} x {options.Clients} clients"));

var rounds = new List<Round>();
for (var i = 0; i < options.Rounds; i++)
{
    var round = await Measure(options, json);
    rounds.Add(round);
    Console.WriteLine(Invariant($"round {i + 1}: {round}"));
}

Console.WriteLine();
Console.WriteLine("metric                       median    min       max");
Report("health ready, ms", r => r.HealthMs);
Report("first simulate, ms", r => r.FirstSimulateMs);
Report("working set idle, MB", r => r.IdleWorkingSetMb);
Report("private idle, MB", r => r.IdlePrivateMb);
Report("working set loaded, MB", r => r.LoadedWorkingSetMb);
Report("private loaded, MB", r => r.LoadedPrivateMb);
Report("peak working set, MB", r => r.PeakWorkingSetMb);
Report("req/s, 1 client", r => r.SingleRps);
Report(Invariant($"req/s, {options.Clients} clients"), r => r.ParallelRps);
Report("CPU ms per request, loaded", r => r.CpuMsPerRequest);
Report("latency p50 1 client, ms", r => r.SingleP50Ms);
return rounds.Any(r => r.Failures > 0) ? 1 : 0;

void Report(string name, Func<Round, double> value)
{
    var values = rounds.Select(value).Order().ToArray();
    Console.WriteLine(Invariant($"{name,-28} {values[values.Length / 2],-9:0.0} {values[0],-9:0.0} {values[^1],-9:0.0}"));
}

static async Task<Round> Measure(Options options, string json)
{
    var start = new ProcessStartInfo
    {
        FileName = options.Server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : options.Server,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (start.FileName == "dotnet")
    {
        start.ArgumentList.Add(options.Server);
    }

    start.Environment["ASPNETCORE_HTTP_PORTS"] = options.Port.ToString(CultureInfo.InvariantCulture);
    using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256 })
    {
        BaseAddress = new Uri(Invariant($"http://127.0.0.1:{options.Port}")),
        Timeout = TimeSpan.FromSeconds(30),
    };

    var clock = Stopwatch.StartNew();
    using var server = Process.Start(start)!;
    server.OutputDataReceived += (_, _) => { };
    server.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Console.Error.WriteLine("server: " + e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    try
    {
        // Wait for the port with short connect attempts: on Windows a refused loopback connect takes about half a second,
        // which would hide the start-up time.
        while (!await Listening(options.Port))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30) || server.HasExited)
            {
                throw new InvalidOperationException("the server did not start");
            }
        }

        using (var health = await http.GetAsync("/health"))
        {
            health.EnsureSuccessStatusCode();
        }

        var healthMs = clock.Elapsed.TotalMilliseconds;
        var (first, _) = await Post(http, json);
        var firstMs = clock.Elapsed.TotalMilliseconds;

        // Idle: after start and one request, nothing running for two seconds.
        await Task.Delay(2000);
        server.Refresh();
        var idleWs = server.WorkingSet64;
        var idlePrivate = server.PrivateMemorySize64;

        var failures = first ? 0 : 1;
        // Warm-up (not counted): the JIT build tiers its hot code up in the first requests.
        for (var i = 0; i < options.Warmup; i++)
        {
            failures += (await Post(http, json)).Ok ? 0 : 1;
        }

        var single = new List<double>(options.Single);
        var singleClock = Stopwatch.StartNew();
        for (var i = 0; i < options.Single; i++)
        {
            var (ok, ms) = await Post(http, json);
            failures += ok ? 0 : 1;
            single.Add(ms);
        }

        var singleRps = options.Single / singleClock.Elapsed.TotalSeconds;

        var remaining = options.Parallel;
        server.Refresh();
        var cpuBefore = server.TotalProcessorTime;
        var parallelClock = Stopwatch.StartNew();
        var parallelFailures = 0;
        await Task.WhenAll(Enumerable.Range(0, options.Clients).Select(async _ =>
        {
            while (Interlocked.Decrement(ref remaining) >= 0)
            {
                var (ok, _) = await Post(http, json);
                if (!ok)
                {
                    Interlocked.Increment(ref parallelFailures);
                }
            }
        }));
        var parallelRps = options.Parallel / parallelClock.Elapsed.TotalSeconds;
        failures += parallelFailures;

        server.Refresh();
        var cpuPerRequest = (server.TotalProcessorTime - cpuBefore).TotalMilliseconds / options.Parallel;
        var loadedWs = server.WorkingSet64;
        var loadedPrivate = server.PrivateMemorySize64;
        var peak = server.PeakWorkingSet64;
        single.Sort();
        return new Round(healthMs, firstMs, Mb(idleWs), Mb(idlePrivate), Mb(loadedWs), Mb(loadedPrivate), Mb(peak), singleRps, parallelRps, cpuPerRequest,
            single.Count > 0 ? single[single.Count / 2] : 0, failures);
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }
}

static async Task<bool> Listening(int port)
{
    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream,
        System.Net.Sockets.ProtocolType.Tcp);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
    try
    {
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        return true;
    }
    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
    {
        return false;
    }
}

static async Task<(bool Ok, double Ms)> Post(HttpClient http, string json)
{
    var clock = Stopwatch.StartNew();
    using var response = await http.PostAsync("/v1/simulate", new StringContent(json, Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    var ok = response.StatusCode == HttpStatusCode.OK && text.Contains("\"win\"", StringComparison.Ordinal);
    if (!ok)
    {
        Console.Error.WriteLine(Invariant($"{(int)response.StatusCode}: {text}"));
    }

    return (ok, clock.Elapsed.TotalMilliseconds);
}

static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

internal sealed record Round(double HealthMs, double FirstSimulateMs, double IdleWorkingSetMb, double IdlePrivateMb, double LoadedWorkingSetMb,
                             double LoadedPrivateMb, double PeakWorkingSetMb, double SingleRps, double ParallelRps, double CpuMsPerRequest, double SingleP50Ms, int Failures)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"health {HealthMs:0} ms, first {FirstSimulateMs:0} ms, idle {IdleWorkingSetMb:0.0}/{IdlePrivateMb:0.0} MB, loaded {LoadedWorkingSetMb:0.0}/{LoadedPrivateMb:0.0} MB, peak {PeakWorkingSetMb:0.0} MB, {SingleRps:0.0} req/s x1, {ParallelRps:0.0} req/s xN, {CpuMsPerRequest:0.0} CPU ms/req, p50 {SingleP50Ms:0.0} ms, failures {Failures}");
}

internal sealed record Options(string Server, int Rounds, int Port, string Board, int Runs, int Warmup, int Single, int Parallel, int Clients)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        int Int(string name, int fallback) => Value(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

        var server = Value("--server") ?? throw new ArgumentException("--server <path to BGSim.Server(.exe|.dll)> is required");
        return new Options(
            Path.GetFullPath(server),
            Int("--rounds", 3),
            Int("--port", 18080),
            Value("--board") ?? Path.Combine(AppContext.BaseDirectory, "board-7x7.json"),
            Int("--runs", 2000),
            Int("--warmup", 100),
            Int("--single", 200),
            Int("--parallel", 1000),
            Int("--clients", 8));
    }
}

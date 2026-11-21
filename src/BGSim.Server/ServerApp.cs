using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BGSim.Server.Api;
using Hearthpulse.Core.Battlegrounds.Effects;

namespace BGSim.Server;

/// <summary>The HTTP API (docs/server.md): /health, /v1/info, /v1/simulate, /v1/fight.</summary>
internal static class ServerApp
{
    /// <summary>
    /// Builds the server. <paramref name="settings"/> are extra configuration values on top of the environment (tests). The
    /// effect registry and the card tables are loaded here, before the server listens, so /health means ready.
    /// </summary>
    public static WebApplication Build(string[] args, IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        var limits = ServerLimits.From(name => builder.Configuration[name]);
        // Warnings and errors only; request bodies are never logged or kept.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = limits.MaxBodyBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 50;
            kestrel.Limits.MaxRequestLineSize = 4 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton(new SimulationGate(limits.MaxConcurrent, limits.QueueLimit, TimeSpan.FromMilliseconds(limits.QueueTimeoutMs)));
        builder.Services.AddSingleton(new BattleService(limits));
        if (limits.CorsOrigins.Count > 0)
        {
            builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .WithOrigins([.. limits.CorsOrigins])
                .WithMethods("GET", "POST")
                .WithHeaders("Content-Type", "X-API-Key", "Authorization")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        }

        var info = Info(limits);
        var app = builder.Build();
        var log = app.Logger;

        app.Use((context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            return next(context);
        });

        if (limits.CorsOrigins.Count > 0)
        {
            app.UseCors();
        }

        if (limits.ApiKey is { } key)
        {
            var apiKey = new ApiKey(key);
            app.Use((context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/v1") || apiKey.Accepts(context.Request))
                {
                    return next(context);
                }

                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Error(StatusCodes.Status401Unauthorized, "unauthorized", "A valid X-API-Key header is required.").ExecuteAsync(context);
            });
        }

        app.MapGet("/health", static () => "ok");

        var v1 = app.MapGroup("/v1");
        v1.MapGet("/info", () => Results.Json(info, ApiJson.Default.InfoResponse));

        v1.MapPost("/simulate", async Task<IResult> (HttpContext context, SimulationGate gate, BattleService service) =>
        {
            var (request, failure) = await Read(context.Request, ApiJson.Default.SimulateRequest, limits);
            if (request is null)
            {
                return failure!;
            }

            var errors = new InputErrors();
            var battle = BoardMapper.Map(request, errors);
            var options = service.Options(request.Options, request.FirstAttackerEntityId ?? 0, errors, context.RequestAborted);
            if (battle is null || options is null)
            {
                return Invalid(errors);
            }

            using var lease = await gate.EnterAsync(context.RequestAborted);
            if (lease is null)
            {
                return Busy(context);
            }

            try
            {
                return Results.Json(BattleService.Simulate(battle, options), ApiJson.Default.SimulateResponse);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                log.LogError(ex, "Simulation failed");
                return Error(StatusCodes.Status500InternalServerError, "internal", "The simulation failed.");
            }
        });

        v1.MapPost("/fight", async Task<IResult> (HttpContext context, SimulationGate gate, BattleService service) =>
        {
            var (request, failure) = await Read(context.Request, ApiJson.Default.FightRequest, limits);
            if (request is null)
            {
                return failure!;
            }

            var errors = new InputErrors();
            var battle = BoardMapper.Map(request, errors);
            if (battle is null)
            {
                return Invalid(errors);
            }

            using var lease = await gate.EnterAsync(context.RequestAborted);
            if (lease is null)
            {
                return Busy(context);
            }

            try
            {
                return Results.Json(service.Fight(battle, request.Seed), ApiJson.Default.FightResponse);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                log.LogError(ex, "Fight failed");
                return Error(StatusCodes.Status500InternalServerError, "internal", "The fight failed.");
            }
        });

        return app;
    }

    /// <summary>Version, card data and the limits in force; built once at start.</summary>
    private static InfoResponse Info(ServerLimits limits)
    {
        var version = typeof(ServerApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var commit = plus >= 0 && plus + 1 < version.Length ? version[(plus + 1)..] : "unknown";
        var registry = EffectRegistry.Default;
        return new InfoResponse(
            plus >= 0 ? version[..plus] : version,
            commit,
            // Not RuntimeFeature.IsDynamicCodeSupported: PublishAot turns that off in JIT runs too. Native AOT compiles nothing.
            JitInfo.GetCompiledMethodCount() > 0 ? "jit" : "native-aot",
            CombatRelevance.Patch,
            registry.NormalCardIds.Count,
            registry.Problems.Count,
            new LimitsDto(
                limits.MaxBodyBytes,
                limits.MaxRuns,
                limits.DefaultRuns,
                limits.MaxTimeBudgetMs,
                limits.DefaultTimeBudgetMs,
                limits.DefaultTargetHalfWidth,
                limits.MaxConcurrent,
                limits.QueueLimit,
                limits.MaxFightLogLines,
                BoardMapper.MaxBoard,
                limits.ApiKeyRequired,
                limits.CorsOrigins.Count > 0));
    }

    /// <summary>The JSON body as <typeparamref name="T"/>, or the error answer: wrong media type, too large, not valid JSON.</summary>
    private static async Task<(T? Value, IResult? Failure)> Read<T>(HttpRequest request, JsonTypeInfo<T> type, ServerLimits limits)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return (null, Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Content-Type must be application/json."));
        }

        if (request.ContentLength > limits.MaxBodyBytes)
        {
            return (null, TooLarge(limits));
        }

        try
        {
            var value = await JsonSerializer.DeserializeAsync(request.Body, type, request.HttpContext.RequestAborted);
            return value is null
                ? (null, Error(StatusCodes.Status400BadRequest, "invalid_json", "The body must be a JSON object."))
                : (value, null);
        }
        catch (JsonException ex)
        {
            // With UseSystemResourceKeys the reason is the resource key and its arguments (e.g. "UnmappedJsonProperty, colour, ...").
            var path = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path;
            var where = ex.LineNumber is { } line ? $" (line {line + 1}, byte {ex.BytePositionInLine + 1})" : string.Empty;
            var reason = ex.Message.Split(" Path: ", 2)[0].TrimEnd(' ', ',', '.');
            return (null, Error(StatusCodes.Status400BadRequest, "invalid_json",
                $"Not a valid request at {path}{where}: {reason}. Unknown members, numbers in quotes, comments, trailing commas and repeated members are refused."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(limits));
        }
    }

    private static IResult TooLarge(ServerLimits limits) =>
        Error(StatusCodes.Status413PayloadTooLarge, "body_too_large", $"The body is larger than {limits.MaxBodyBytes} bytes.");

    private static IResult Invalid(InputErrors errors) =>
        Results.Json(new ErrorResponse("invalid_request", errors.Items[0].Path + ": " + errors.Items[0].Message, errors.Items),
            ApiJson.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Busy(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        return Error(StatusCodes.Status503ServiceUnavailable, "busy", "Too many simulations at once; retry after a second.");
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), ApiJson.Default.ErrorResponse, statusCode: status);
}

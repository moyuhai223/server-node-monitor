using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SNM.Master.Api;

/// <summary>Uniform envelope for every REST response (docs/API.md 1.1): code 0 = success.</summary>
public sealed class ApiResponse<T>
{
    public int Code { get; init; }
    public string Message { get; init; } = "OK";
    public T? Data { get; init; }
}

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string message = "OK") => new() { Code = 0, Message = message, Data = data };
    public static ApiResponse<object?> Fail(int code, string message, object? data = null) => new() { Code = code, Message = message, Data = data };
}

/// <summary>Business error codes (docs/API.md 1.1).</summary>
public static class ApiCodes
{
    public const int LoginFailed = 10001;
    public const int AccountLocked = 10002;
    public const int OldPasswordWrong = 10010;
    public const int RefreshInvalid = 10011;
    public const int NodeNameExists = 20001;
    public const int NodeNotFound = 20002;
    public const int StateNotAllowed = 20004;
    public const int ChannelTestFailed = 30001;
    public const int GeoIpRefreshFailed = 30002;
}

/// <summary>Thrown by services/endpoints; converted to the envelope by <see cref="ApiExceptionMiddleware"/>.</summary>
public sealed class ApiException(int status, int code, string message, object? data = null) : Exception(message)
{
    public int Status { get; } = status;
    public int Code { get; } = code;
    public object? Payload { get; } = data;

    public static ApiException BadRequest(string message, object? data = null) => new(400, 400, message, data);
    public static ApiException Validation(IReadOnlyDictionary<string, string[]> errors) => new(400, 400, "参数校验失败", new { errors });
    public static ApiException Business(int status, int code, string message, object? data = null) => new(status, code, message, data);
    public static ApiException NotFound(string message = "资源不存在", int code = 404) => new(404, code, message);
    public static ApiException NodeNotFound() => new(404, ApiCodes.NodeNotFound, "节点不存在");
    public static ApiException Unauthorized(int code, string message) => new(401, code, message);
    public static ApiException Forbidden(string message = "无权限") => new(403, 403, message);
    public static ApiException Upstream(int code, string message, object? data = null) => new(502, code, message, data);
}

/// <summary>Collects field errors for the 400 envelope.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public Validator Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) { list = []; _errors[field] = list; }
        list.Add(message);
        return this;
    }

    public void When(bool condition, string field, string message)
    {
        if (condition) Add(field, message);
    }

    public void ThrowIfInvalid()
    {
        if (!HasErrors) return;
        throw ApiException.Validation(_errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
    }
}

/// <summary>decimal &lt;-&gt; JSON string ("49.99") to avoid floating point noise in the browser (docs/API.md 8).</summary>
public sealed class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetDecimal();
        if (reader.TokenType == JsonTokenType.String && decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        throw new JsonException("expected a decimal string");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("0.##", CultureInfo.InvariantCulture));
}

public static class ApiJson
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DictionaryKeyPolicy = null;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.NumberHandling = JsonNumberHandling.Strict;
        o.PropertyNameCaseInsensitive = true;
        o.Converters.Add(new DecimalStringConverter());
        o.Converters.Add(new JsonStringEnumConverter());
    }

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }
}

/// <summary>Maps exceptions to the envelope; unknown exceptions become a generic 500 (details only in the log).</summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex)
        {
            await WriteAsync(ctx, ex.Status, ex.Code, ex.Message, ex.Payload);
        }
        catch (BadHttpRequestException ex)
        {
            var message = ex.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("body", StringComparison.OrdinalIgnoreCase)
                ? "请求体不是合法 JSON"
                : "请求格式错误";
            await WriteAsync(ctx, ex.StatusCode is >= 400 and < 500 ? ex.StatusCode : 400, 400, message, null);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // client went away; nothing to write
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            await WriteAsync(ctx, 500, 500, "服务器发生异常", null);
        }
    }

    public static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail(code, message, data), ApiJson.Options), ctx.RequestAborted);
    }
}

public static class HttpContextExtensions
{
    public static int UserId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : throw ApiException.Unauthorized(401, "未登录");
    }

    public static string? ClientIp(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public static string? UserAgent(this HttpContext ctx) => ctx.Request.Headers.UserAgent.FirstOrDefault();

    /// <summary>Page parameters (docs/API.md 1.2): pageNo >= 1, pageSize 0 = all (capped at 200 otherwise).</summary>
    public static (int PageNo, int PageSize) Paging(this HttpRequest req, int defaultSize = 20)
    {
        var pageNo = int.TryParse(req.Query["pageNo"], out var p) && p >= 1 ? p : 1;
        var pageSize = int.TryParse(req.Query["pageSize"], out var s) ? s : defaultSize;
        if (pageSize < 0) pageSize = defaultSize;
        if (pageSize > 200) pageSize = 200;
        return (pageNo, pageSize);
    }
}

public sealed class PagedData<T>
{
    public IReadOnlyList<T> PageData { get; init; } = [];
    public int Total { get; init; }
    public int PageNo { get; init; }
    public int PageSize { get; init; }
}

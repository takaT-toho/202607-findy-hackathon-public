using Microsoft.Extensions.Options;
using TargetApp;

var builder = WebApplication.CreateBuilder(args);

// 設定を型付きで読み込み、起動時に必須項目を検証する。
builder.Services.AddOptions<MockApiSettings>()
    .Bind(builder.Configuration.GetSection("MockApi"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SreAgentSettings>()
    .Bind(builder.Configuration.GetSection("SreAgent"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// HttpClient を登録する。
builder.Services.AddHttpClient<TransitService>((sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<MockApiSettings>>().Value;
    client.BaseAddress = new Uri(settings.BaseUrl);
});
builder.Services.AddHttpClient<SreAgentClient>();

// エラー通知の経路を選ぶ。既定は開発=Webhook（直接通知）/ 本番=GcpLog（ログ経由）。
// Reporting:Mode で明示的に切り替えることもできる。
var reportingMode = builder.Configuration.GetValue<string>("Reporting:Mode")
    ?? (builder.Environment.IsDevelopment() ? "Webhook" : "GcpLog");
if (string.Equals(reportingMode, "Webhook", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IErrorReporter, WebhookErrorReporter>();
}
else
{
    builder.Services.AddSingleton<IErrorReporter, GcpLogErrorReporter>();
}

var app = builder.Build();

// 静的 UI（wwwroot/index.html）を配信する。
app.UseDefaultFiles();
app.UseStaticFiles();

// スキーマ変更（TransitJsonException）の検知・通知を一箇所にまとめるミドルウェア。
// どのエンドポイントで例外が起きても、ここで 1 回だけ SRE Agent に通知する。
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (TransitJsonException ex)
    {
        var reporter = context.RequestServices.GetRequiredService<IErrorReporter>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Schema drift detected; reporting to SRE Agent");

        var payload = ErrorPayloadBuilder.Build(
            ex,
            nameof(TransitService.FetchStatusAsync),
            filePath: "TargetApp/TransitService.cs");
        await reporter.ReportAsync(payload);

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Schema drift detected",
                errorMessage = ex.Message,
                webhookStatus = "reported",
                rawJson = ex.RawJson
            });
        }
    }
});

// 実ユーザーが叩く想定の読み取りエンドポイント。例外検知・通知は上のミドルウェアが行う。
app.MapGet("/api/transit/status", async (TransitService transit) =>
{
    var status = await transit.FetchStatusAsync();
    return Results.Ok(new { trainStatus = status, schema = "before" });
});

// 手動でデータ取得を試すためのエンドポイント。上流に到達できない場合だけここで 502 を返す。
app.MapPost("/trigger-fetch", async (TransitService transit, ILogger<Program> logger) =>
{
    try
    {
        var status = await transit.FetchStatusAsync();
        return Results.Ok(new { trainStatus = status, schema = "before" });
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Failed to reach MockApi");
        return Results.Problem(title: "Upstream API unreachable", detail: ex.Message, statusCode: 502);
    }
});

app.Run();

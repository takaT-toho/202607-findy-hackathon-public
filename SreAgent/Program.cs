using System.Text.Json;
using System.Text.Json.Serialization;
using SreAgent;

var builder = WebApplication.CreateBuilder(args);

// enum は JSON に文字列で出す（UI で名前をそのまま使うため）。
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// appsettings の設定を型付きで読み込む。
builder.Services.AddOptions<GeminiSettings>()
    .Bind(builder.Configuration.GetSection("Gemini"))
    .ValidateDataAnnotations();
builder.Services.AddOptions<GitHubSettings>()
    .Bind(builder.Configuration.GetSection("GitHub"))
    .ValidateDataAnnotations();

// 本番だけ起動時に必須項目を検証する（開発では ProjectId 等が未設定でも動かせるように）。
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddOptions<GeminiSettings>().ValidateOnStart();
    builder.Services.AddOptions<GitHubSettings>().ValidateOnStart();
}

// イベントログ: 開発はメモリ（再起動で消える）、本番はファイル（再起動後も残る）。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IEventLog, InMemoryEventLog>();
}
else
{
    var eventLogPath = builder.Configuration.GetValue("Monitoring:EventLogPath", "data/eventlog.jsonl");
    builder.Services.AddSingleton<IEventLog>(_ => new FileEventLog(eventLogPath!));
}

// 重複抑制の窓と、修正の最大リトライ回数。
var dedupWindowMinutes = builder.Configuration.GetValue("GitHub:DedupWindowMinutes", 60);
var maxFixAttempts = builder.Configuration.GetValue("Gemini:MaxFixAttempts", 3);
builder.Services.AddSingleton<IDedupGate>(_ => new InMemoryDedupGate(TimeSpan.FromMinutes(dedupWindowMinutes)));

// 同時に走らせる修正処理の上限と、空き待ちの最大秒数。
var maxConcurrentFixes = builder.Configuration.GetValue("Gemini:MaxConcurrentFixes", 2);
var acquireWaitSeconds = builder.Configuration.GetValue("Gemini:AcquireWaitSeconds", 10);
builder.Services.AddSingleton<IConcurrencyGate>(_ => new SemaphoreConcurrencyGate(maxConcurrentFixes));

// 詰まったケースを定期スキャンして自動エスカレーションする常駐サービス。
var stuckScanSeconds = builder.Configuration.GetValue("Monitoring:StuckScanSeconds", 60);
var stuckThresholdMinutes = builder.Configuration.GetValue("Monitoring:StuckThresholdMinutes", 15);
builder.Services.AddHostedService(sp => new StuckMonitor(
    sp.GetRequiredService<IEventLog>(),
    TimeSpan.FromSeconds(stuckScanSeconds),
    TimeSpan.FromMinutes(stuckThresholdMinutes),
    sp.GetRequiredService<ILogger<StuckMonitor>>()));

// ビルド検証。実体（DotnetBuildVerifier）をログ用デコレータで包む。
builder.Services.AddSingleton<DotnetBuildVerifier>();
builder.Services.AddSingleton<IBuildVerifier>(sp => new LoggingBuildVerifier(
    sp.GetRequiredService<DotnetBuildVerifier>(),
    sp.GetRequiredService<IEventLog>(),
    sp.GetRequiredService<ILogger<LoggingBuildVerifier>>()));

// Triage: 決定論ゲート＋分類器。分類器だけ開発=Stub / 本番=LLM で切り替える。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<ITriageClassifier, StubTriageClassifier>();
}
else
{
    builder.Services.AddSingleton<ITriageClassifier, TriageClassifier>();
}
builder.Services.AddSingleton<IErrorTriager, ErrorTriager>();

// レビュアー。開発=Stub（常に承認）/ 本番=LLM。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IReviewer, StubReviewer>();
}
else
{
    builder.Services.AddSingleton<IReviewer, Reviewer>();
}

// AI と Git 操作は、環境で実装を切り替えたうえでログ用デコレータで包む。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<StubAiCore>();
    builder.Services.AddSingleton<IAiCore>(sp => new LoggingAiCore(
        sp.GetRequiredService<StubAiCore>(),
        sp.GetRequiredService<IEventLog>(),
        sp.GetRequiredService<ILogger<LoggingAiCore>>()));

    builder.Services.AddSingleton<StubGitOperator>();
    builder.Services.AddSingleton<IGitOperator>(sp => new LoggingGitOperator(
        sp.GetRequiredService<StubGitOperator>(),
        sp.GetRequiredService<IEventLog>(),
        sp.GetRequiredService<ILogger<LoggingGitOperator>>()));
}
else
{
    builder.Services.AddSingleton<AiCore>();
    builder.Services.AddSingleton<IAiCore>(sp => new LoggingAiCore(
        sp.GetRequiredService<AiCore>(),
        sp.GetRequiredService<IEventLog>(),
        sp.GetRequiredService<ILogger<LoggingAiCore>>()));

    builder.Services.AddSingleton<GitOperator>();
    builder.Services.AddSingleton<IGitOperator>(sp => new LoggingGitOperator(
        sp.GetRequiredService<GitOperator>(),
        sp.GetRequiredService<IEventLog>(),
        sp.GetRequiredService<ILogger<LoggingGitOperator>>()));
}

builder.Services.AddSingleton<WebhookReceiver>(sp => new WebhookReceiver(
    sp.GetRequiredService<IAiCore>(),
    sp.GetRequiredService<IGitOperator>(),
    sp.GetRequiredService<IBuildVerifier>(),
    sp.GetRequiredService<IDedupGate>(),
    sp.GetRequiredService<IErrorTriager>(),
    sp.GetRequiredService<IReviewer>(),
    sp.GetRequiredService<IConcurrencyGate>(),
    sp.GetRequiredService<IEventLog>(),
    maxFixAttempts,
    sp.GetRequiredService<ILogger<WebhookReceiver>>(),
    acquireWaitSeconds));

var app = builder.Build();

// 管理ダッシュボード（静的ファイル）。
app.UseDefaultFiles();
app.UseStaticFiles();

// /webhook/error は HMAC 署名された通知だけ受理する。Secret 未設定なら検証しない（開発用）。
var webhookSecret = app.Configuration.GetValue<string>("Webhook:Secret");
if (!app.Environment.IsDevelopment() && string.IsNullOrEmpty(webhookSecret))
{
    // 本番で Secret 未設定 = エンドポイントが公開状態。誤設定を強く警告する。
    app.Logger.LogWarning(
        "Webhook:Secret 未設定のため /webhook/error の署名検証が無効です（公開状態）。本番では秘密を設定してください。");
}
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/webhook/error")
    {
        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }
        context.Request.Body.Position = 0; // この後のモデルバインドが本文を読めるよう巻き戻す。

        var provided = context.Request.Headers["X-Signature"].FirstOrDefault();
        if (!WebhookSignature.Verify(webhookSecret, body, provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "invalid or missing webhook signature" });
            return;
        }
    }
    await next();
});

// 監視対象アプリが実リクエスト中のエラーを直接通知してくる入口。
app.MapPost("/webhook/error", async (ErrorPayload payload, WebhookReceiver receiver)
    => await receiver.ReceiveErrorAsync(payload, DetectionSource.RealRequestPath));

// ログ駆動の入口。Pub/Sub から届くメッセージを ErrorPayload に復元して同じパイプラインに流す。
app.MapPost("/webhook/error/pubsub", async (JsonElement body, WebhookReceiver receiver, ILoggerFactory lf) =>
{
    var payload = PubSubPushParser.TryParse(body);
    if (payload is null)
    {
        lf.CreateLogger("PubSub").LogWarning("Pub/Sub push could not be parsed into an ErrorPayload");
        // 解釈できないメッセージは捨てたいので 204 を返す（Pub/Sub に再配信させない）。
        return Results.NoContent();
    }
    return await receiver.ReceiveErrorAsync(payload, DetectionSource.LogDriven);
});

// ダッシュボード用: 生のイベント一覧を返す。
app.MapGet("/admin/events", async (IEventLog log, int? count) =>
{
    var events = await log.GetRecentAsync(count ?? 100);
    return Results.Ok(events);
});

// ダッシュボード用: イベントをケース単位に集計して返す。
app.MapGet("/admin/cases", async (IEventLog log, int? count) =>
{
    var events = await log.GetRecentAsync(count ?? 100);
    return Results.Ok(CaseProjection.Fold(events));
});

app.Run();

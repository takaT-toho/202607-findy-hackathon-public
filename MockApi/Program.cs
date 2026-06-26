// 外部 API のスキーマ変更をシミュレートするモックサーバー。
// useNewSchema クエリで、変更前（旧）スキーマと変更後（新）スキーマを切り替えて返す。

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 新スキーマを返すかどうか。起動時は false（旧スキーマ）。再起動でリセットされる。
bool useNewSchemaFlag = false;

// useNewSchema が指定されていればフラグを更新し、現在のフラグに応じて旧/新スキーマを返す。
app.MapGet("/api/transit/status", (bool? useNewSchema) =>
{
    if (useNewSchema.HasValue)
    {
        useNewSchemaFlag = useNewSchema.Value;
    }

    return useNewSchemaFlag
        ? Results.Ok(BuildNewSchema())
        : Results.Ok(BuildOldSchema());
});

app.Run();

// 旧スキーマ: 遅延がフラットな delay_minutes。
static object BuildOldSchema() => new
{
    line_id = "JR-Yamanote",
    line_name = "山手線",
    status = "delayed",
    delay_minutes = 8,
    last_updated = DateTimeOffset.UtcNow
};

// 新スキーマ: 遅延がネストした delays.value（これが TargetApp 側の変換エラーを引き起こす）。
static object BuildNewSchema() => new
{
    line_id = "JR-Yamanote",
    line_name = "山手線",
    status = "delayed",
    delays = new { value = 8, unit = "minutes" },
    last_updated = DateTimeOffset.UtcNow
};

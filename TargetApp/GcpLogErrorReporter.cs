using System.Text.Json;
using System.Text.Json.Nodes;

namespace TargetApp;

// 本番用のエラー通知。SRE Agent を直接叩かず、構造化ログを 1 行出力するだけ。
// あとは GCP 側（Cloud Logging → Pub/Sub）が SRE Agent まで届ける。
// ErrorPayload の各フィールドを JSON のトップレベルに並べて出すことで、
// ログのフィルタリングと、受信側での ErrorPayload 復元がそのままできる。
public class GcpLogErrorReporter : IErrorReporter
{
    // ログのフィルタに使う目印。JSON の marker フィールドとして出力する。
    public const string Marker = "SCHEMA_DRIFT_DETECTED";

    private readonly ILogger<GcpLogErrorReporter> _logger;

    public GcpLogErrorReporter(ILogger<GcpLogErrorReporter> logger)
    {
        _logger = logger;
    }

    public Task ReportAsync(ErrorPayload payload)
    {
        // ErrorPayload の各プロパティをトップレベルに展開し、Cloud Logging 用に severity / message / marker を足す。
        var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
        node["severity"] = "ERROR";
        node["marker"] = Marker;
        node["message"] = $"{Marker}: schema drift detected in {payload.FaultyMethodSource}";

        // 改行を含む値もエスケープされるよう、1 行の JSON として出力する。
        Console.WriteLine(node.ToJsonString());

        // ローカルで様子が分かるよう控えめにログも残す（フィルタには一致しないので二重通知にはならない）。
        _logger.LogInformation("Reported schema drift ({ErrorType}) via structured log", payload.ErrorType);
        return Task.CompletedTask;
    }
}

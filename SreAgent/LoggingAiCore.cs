using System.Diagnostics;
using CSharpFunctionalExtensions;

namespace SreAgent;

// IAiCore をラップして、AI 呼び出しの入出力・所要時間を記録するデコレータ。
// 本来の処理は内側の _inner に任せ、振る舞いは変えずにログ記録だけを足す。
public class LoggingAiCore : IAiCore
{
    private readonly IAiCore _inner;
    private readonly IEventLog _eventLog;
    private readonly ILogger<LoggingAiCore> _logger;

    public LoggingAiCore(IAiCore inner, IEventLog eventLog, ILogger<LoggingAiCore> logger)
    {
        _inner = inner;
        _eventLog = eventLog;
        _logger = logger;
    }

    public async Task<Result<AiOutput, AiError>> GenerateFixAsync(
        ErrorPayload payload, string? previousAttemptErrors = null, ErrorClass errorClass = ErrorClass.SchemaDrift)
    {
        var retryNote = string.IsNullOrWhiteSpace(previousAttemptErrors) ? "" : " (retry with prior errors)";
        _logger.LogInformation("AI request{Retry}: errorType={ErrorType}, class={Class}, rawJsonLen={Len}",
            retryNote, payload.ErrorType, errorClass, payload.RawJsonResponse.Length);
        await _eventLog.RecordAsync(EventType.AiCallStarted,
            $"AI call for {payload.ErrorType} [{errorClass}]{retryNote}",
            $"rawJson: {Truncate(payload.RawJsonResponse, 200)}");

        var sw = Stopwatch.StartNew();
        var result = await _inner.GenerateFixAsync(payload, previousAttemptErrors, errorClass);
        sw.Stop();

        if (result.IsSuccess)
        {
            var output = result.Value;
            _logger.LogInformation("AI response in {Ms}ms: confidence={Confidence}, filePath={Path}",
                sw.ElapsedMilliseconds, output.Confidence, output.FilePath);
            await _eventLog.RecordAsync(EventType.AiCallCompleted,
                $"AI returned fix (confidence={output.Confidence}, {sw.ElapsedMilliseconds}ms)",
                $"PrDescription: {output.PrDescription}\nImpactAnalysis: {output.ImpactAnalysis}");
        }
        else
        {
            _logger.LogWarning("AI failed in {Ms}ms: {Error}",
                sw.ElapsedMilliseconds, result.Error.GetType().Name);
            await _eventLog.RecordAsync(EventType.AiCallFailed,
                $"AI failed: {result.Error.GetType().Name} ({sw.ElapsedMilliseconds}ms)",
                result.Error.Detail);
        }

        return result;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...(truncated)";
}

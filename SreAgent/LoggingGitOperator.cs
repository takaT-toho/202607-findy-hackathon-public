using System.Diagnostics;
using CSharpFunctionalExtensions;

namespace SreAgent;

// IGitOperator をラップして、PR 作成の開始・成功・失敗を記録するデコレータ。
// 本来の処理は内側の _inner に任せ、このクラスはログ記録だけを足す。
public class LoggingGitOperator : IGitOperator
{
    private readonly IGitOperator _inner;
    private readonly IEventLog _eventLog;
    private readonly ILogger<LoggingGitOperator> _logger;

    public LoggingGitOperator(IGitOperator inner, IEventLog eventLog, ILogger<LoggingGitOperator> logger)
    {
        _inner = inner;
        _eventLog = eventLog;
        _logger = logger;
    }

    public async Task<Result<string, GitOperatorError>> CreatePrAsync(PrContext ctx)
    {
        var output = ctx.Output;
        _logger.LogInformation("Creating PR: file={Path}, confidence={Confidence}, attempts={Attempts}, signature={Signature}",
            output.FilePath, output.Confidence, ctx.AttemptCount, ctx.Signature);
        await _eventLog.RecordAsync(EventType.PrCreationStarted,
            $"PR creation started for {output.FilePath} (attempts={ctx.AttemptCount}, tests={ctx.TestsVerified})",
            $"PrDescription: {output.PrDescription}");

        var sw = Stopwatch.StartNew();
        var result = await _inner.CreatePrAsync(ctx);
        sw.Stop();

        if (result.IsSuccess)
        {
            _logger.LogInformation("PR created in {Ms}ms: {Url}", sw.ElapsedMilliseconds, result.Value);
            await _eventLog.RecordAsync(EventType.PrCreated,
                $"PR created: {result.Value} ({sw.ElapsedMilliseconds}ms)",
                detail: null);
        }
        else
        {
            _logger.LogWarning("PR creation failed in {Ms}ms: {Error}",
                sw.ElapsedMilliseconds, result.Error.GetType().Name);
            await _eventLog.RecordAsync(EventType.PrFailed,
                $"PR creation failed: {result.Error.GetType().Name} ({sw.ElapsedMilliseconds}ms)",
                result.Error.Detail);
        }

        return result;
    }
}

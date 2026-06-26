using System.Diagnostics;
using CSharpFunctionalExtensions;

namespace SreAgent;

// IBuildVerifier をラップして、検証の開始・完了・失敗を記録するデコレータ。
public class LoggingBuildVerifier : IBuildVerifier
{
    private readonly IBuildVerifier _inner;
    private readonly IEventLog _eventLog;
    private readonly ILogger<LoggingBuildVerifier> _logger;

    public LoggingBuildVerifier(IBuildVerifier inner, IEventLog eventLog, ILogger<LoggingBuildVerifier> logger)
    {
        _inner = inner;
        _eventLog = eventLog;
        _logger = logger;
    }

    public async Task<Result<Unit, BuildError>> VerifyAsync(string filePath, string fixedFileContent, string? testCode = null)
    {
        var hasTest = !string.IsNullOrWhiteSpace(testCode);
        _logger.LogInformation("Verifying build for {Path} ({Bytes} bytes, tests={HasTest})",
            filePath, fixedFileContent.Length, hasTest);
        await _eventLog.RecordAsync(EventType.BuildVerifyStarted,
            $"Verifying {filePath}{(hasTest ? " + tests" : "")}",
            $"{fixedFileContent.Length} bytes of proposed content");

        var sw = Stopwatch.StartNew();
        var result = await _inner.VerifyAsync(filePath, fixedFileContent, testCode);
        sw.Stop();

        if (result.IsSuccess)
        {
            _logger.LogInformation("Build verified in {Ms}ms", sw.ElapsedMilliseconds);
            await _eventLog.RecordAsync(EventType.BuildVerifyCompleted,
                $"Build verified ({sw.ElapsedMilliseconds}ms)",
                detail: null);
        }
        else
        {
            _logger.LogWarning("Build verification failed in {Ms}ms: {Error}",
                sw.ElapsedMilliseconds, result.Error.GetType().Name);
            await _eventLog.RecordAsync(EventType.BuildVerifyFailed,
                $"Verify failed: {result.Error.GetType().Name} ({sw.ElapsedMilliseconds}ms)",
                result.Error.Detail);
        }

        return result;
    }
}

namespace SreAgent;

// AI による修正生成（GenerateFixAsync）が失敗したときの理由を表す型。
// 派生レコードで失敗の種類を区別する。
public abstract record AiError(string Detail);

public record SchemaMismatchError(string Detail) : AiError(Detail);
public record ApiQuotaExceededError(string Detail) : AiError(Detail);
public record NetworkError(string Detail) : AiError(Detail);
public record MarkerMissingError(string Detail) : AiError(Detail);

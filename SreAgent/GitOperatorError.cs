namespace SreAgent;

// PR 作成（CreatePrAsync）が失敗したときの理由を表す型。
public abstract record GitOperatorError(string Detail);

public record FileNotFoundError(string FilePath) : GitOperatorError($"File not found: {FilePath}");
public record MarkerMissingInOutputError(string Detail) : GitOperatorError(Detail);
public record BranchAlreadyExistsError(string BranchName) : GitOperatorError($"Branch already exists: {BranchName}");
public record GitHubApiError(string Detail) : GitOperatorError(Detail);
public record LocalIoError(string Detail) : GitOperatorError(Detail);
public record BuildVerificationFailedError(BuildError Inner) : GitOperatorError(Inner.Detail);

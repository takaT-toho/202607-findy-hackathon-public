namespace SreAgent;

// ビルド検証（VerifyAsync）が失敗したときの理由を表す型。
public abstract record BuildError(string Detail);

public record CompilationFailed(string Detail) : BuildError(Detail);
public record VerifierUnavailable(string Detail) : BuildError(Detail);

// コンパイルは通ったが、AI が生成したテストが失敗した場合。修正をやり直す対象になる。
public record TestFailed(string Detail) : BuildError(Detail);

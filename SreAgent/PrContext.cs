namespace SreAgent;

// PR を作るのに必要な情報をまとめたもの（AI 出力＋検知やリトライのメタ情報）。
public record PrContext(
    AiOutput Output,
    // エラーの識別子。ブランチ名と重複判定のキーになる。
    string Signature,
    // どの経路で検知されたか。
    DetectionSource DetectionSource,
    // ビルド/テストが通るまでに要した試行回数（1 = 一発成功）。
    int AttemptCount,
    // 生成テストが dotnet test を通過したか（スキップ時は false）。
    bool TestsVerified,
    // 旧スキーマ定義。PR 本文の差分表示に使う。
    string SchemaBefore,
    // 新スキーマの生 JSON。PR 本文の差分表示に使う。
    string SchemaAfter
);

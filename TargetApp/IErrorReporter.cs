namespace TargetApp;

// エラーを SRE Agent へ届ける経路の抽象化。
//   - 開発: WebhookErrorReporter … SRE Agent の /webhook/error を直接叩く
//   - 本番: GcpLogErrorReporter  … 構造化ログ出力 → Cloud Logging → Pub/Sub 経由で届ける
public interface IErrorReporter
{
    // エラーを通知する。送信に失敗しても本処理は止めない（fire-and-forget）。
    Task ReportAsync(ErrorPayload payload);
}

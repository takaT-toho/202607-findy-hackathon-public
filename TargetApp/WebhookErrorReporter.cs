namespace TargetApp;

// 開発環境用のエラー通知。Pub/Sub を介さず、SRE Agent の /webhook/error を直接叩く。
// GCP が無いローカルでも検知→PR の流れを通せる。
public class WebhookErrorReporter : IErrorReporter
{
    private readonly SreAgentClient _client;

    public WebhookErrorReporter(SreAgentClient client)
    {
        _client = client;
    }

    public Task ReportAsync(ErrorPayload payload) => _client.NotifyErrorAsync(payload);
}

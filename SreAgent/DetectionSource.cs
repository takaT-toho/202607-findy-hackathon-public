namespace SreAgent;

// エラーがどの経路でエージェントに届いたかを表す。PR 本文に検知経路を明示するために使う。
public enum DetectionSource
{
    // 実リクエスト中に発生した例外を、アプリが直接 /webhook/error へ通知した。
    RealRequestPath,

    // Cloud Logging のログを起点に Pub/Sub 経由で /webhook/error/pubsub が受信した。
    LogDriven
}

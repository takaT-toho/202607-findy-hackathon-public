# Self-Healing SRE Agent

外部 API の**スキーマ変更（schema drift）を検知し、AI が修正コードと Pull Request を自動生成する「自己修復パイプライン」**のデモ実装です。

## これは何か

外部サービスの API は、ある日突然レスポンス JSON の構造を変えてきます（例: `delay_minutes: 8` が `delays: { value: 8, unit: "minutes" }` に変わる）。
こうした破壊的変更は、それに依存するアプリのデシリアライズを静かに壊し、本番障害につながります。

このプロジェクトは、その障害を**人手を介さず自動で修復する**様子を 3 つのサービスで実演します。

1. アプリが外部 API を叩く → スキーマ変更でデシリアライズ例外が発生
2. エラーを SRE Agent が受け取り、ドリフトを検知
3. AI（Gemini）が修正メソッドを生成 → ビルド検証 → **修正 PR を自動作成**

「運行情報アプリ」を題材にしていますが、仕組み自体は**外部 API 依存を持つ任意のアプリに一般化できる**ものです。

## アーキテクチャ

```
                 ┌─────────────┐
                 │   MockApi   │  外部 API スタブ
                 │  :5074      │  useNewSchema=true でスキーマdrift を発生させる
                 └──────┬──────┘
                        │ GET /api/transit/status
                        ▼
                 ┌─────────────┐
                 │  TargetApp  │  外部 API に依存するアプリ + デモ UI
                 │  :5165      │  drift でデシリアライズ例外 → エラーを通知
                 └──────┬──────┘
                        │ Webhook（エラー通知）
                        ▼
                 ┌─────────────┐
                 │  SreAgent   │  自己修復エージェント + ダッシュボード
                 │  :5180      │  ドリフト検知 → AI 修正生成 → ビルド検証 → 自動 PR
                 └─────────────┘
```

| サービス | 役割 |
|----------|------|
| **MockApi** | 外部 API のスタブ。`useNewSchema=true` で応答スキーマを切り替え、drift を意図的に起こす |
| **TargetApp** | 外部 API を消費するアプリ。スマホ運行情報アプリ風のデモ UI を持ち、エラーを SreAgent へ通知する |
| **SreAgent** | エラーを受信してドリフトを検知し、AI による修正生成・ビルド検証・PR 作成を行う。イベント時系列を見せるダッシュボード付き |

## 必要要件

- [.NET 10 SDK](https://dotnet.microsoft.com/)

## ローカルでの動かし方

開発環境（`ASPNETCORE_ENVIRONMENT=Development`、`dotnet run` の既定）では、課金・本番認証・不可逆な副作用を伴う外部依存（AI 呼び出し・GitHub PR 作成等）は**自動的に Stub 実装に差し替わる**ため、API キーや GCP/GitHub の認証情報なしでデモを通せます。

3 つのターミナルでそれぞれ起動します。

```bash
# Terminal 1: 外部 API スタブ
cd MockApi && dotnet run        # http://localhost:5074

# Terminal 2: 自己修復エージェント + ダッシュボード
cd SreAgent && dotnet run       # http://localhost:5180

# Terminal 3: 対象アプリ + デモ UI
cd TargetApp && dotnet run      # http://localhost:5165
```

| URL | 役割 |
|-----|------|
| `http://localhost:5165/` | Target App のデモ UI（スマホ運行情報アプリ画面） |
| `http://localhost:5180/` | SRE Agent ダッシュボード（イベント時系列） |

## デモシナリオ

1. **正常稼働**: `http://localhost:5165/` を開く。画面は数秒間隔で自動更新し、運行情報が正常表示される。
2. **スキーマ drift を発生させる**:
   ```bash
   curl "http://localhost:5074/api/transit/status?useNewSchema=true"
   ```
   これで MockApi の内部フラグが反転し、以後すべての GET 応答が新スキーマ（`delays.value` ネスト）になる。
3. **自動で劣化を検知**: 数秒以内に Target App 画面が異常を検知し、ユーザー向けの穏当な障害表示に切り替わる。
4. **自己修復が走る**: SRE Agent ダッシュボードにドリフト検知 → AI 修正生成 → ビルド検証 → PR 作成（開発環境では Stub）の一連のイベントが流れる。
5. **復旧**: スキーマを元に戻すと、Target App は自動更新で正常表示に復帰する。
   ```bash
   curl "http://localhost:5074/api/transit/status?useNewSchema=false"
   ```

## テスト

```bash
dotnet test        # SreAgent.Tests（xUnit）
```

ソリューション一括でのビルドも可能です。

```bash
dotnet build SelfHealingSreAgent.slnx
```

## 本番デプロイ（概要）

本番では AI（Gemini / Vertex AI）と GitHub PR 作成を実装側に切り替えて Cloud Run などにデプロイします。
プロジェクト ID やサービスアカウントは環境に合わせて設定してください（以下はプレースホルダ）。

```bash
export PROJECT_ID=<your-gcp-project-id>
export REGION=<your-region>          # 例: asia-northeast1

gcloud run deploy mock-api   --source MockApi   --region "$REGION" --project "$PROJECT_ID"
gcloud run deploy sre-agent  --source SreAgent  --region "$REGION" --project "$PROJECT_ID"
gcloud run deploy target-app --source TargetApp --region "$REGION" --project "$PROJECT_ID"
```

本番認証情報（GitHub PAT・サービスアカウント・Gemini のリージョン等）は環境変数で注入し、**リポジトリにはコミットしないでください**。

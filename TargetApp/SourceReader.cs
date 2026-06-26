using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace TargetApp;

// AI に渡すために「修正対象メソッドのソース」と「期待スキーマの定義」を取り出すユーティリティ。
// TransitService.cs はビルド時に埋め込みリソースとして同梱している。
public static class SourceReader
{
    // 埋め込んだ TransitService.cs から、マーカーで囲まれた methodName のソースを取り出して返す。
    // マーカーが見つからなければ例外を投げる。
    public static string Read(string methodName)
    {
        var source = LoadEmbeddedSource("TransitService.cs");
        var startMarker = $"// [AGENT-MANAGED-START: {methodName}]";
        var endMarker = $"// [AGENT-MANAGED-END: {methodName}]";

        var startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException($"Marker start not found for method: {methodName}");
        }

        var endIndex = source.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException($"Marker end not found for method: {methodName}");
        }

        return source.Substring(startIndex, endIndex - startIndex + endMarker.Length);
    }

    // 型 T から、JsonPropertyName 属性を含む record 定義の文字列を作る（AI に渡す期待スキーマ用）。
    public static string GetRecordDefinition<T>()
    {
        var type = typeof(T);
        var sb = new StringBuilder();
        sb.AppendLine($"public record {type.Name}");
        sb.AppendLine("{");
        foreach (var prop in type.GetProperties())
        {
            var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
            sb.AppendLine($"    [JsonPropertyName(\"{jsonName}\")]");
            sb.AppendLine($"    public required {FormatTypeName(prop.PropertyType)} {prop.Name} {{ get; init; }}");
            sb.AppendLine();
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // 埋め込みリソースからソースファイルの中身を読み込む。
    private static string LoadEmbeddedSource(string resourceName)
    {
        var assembly = typeof(SourceReader).Assembly;
        var fullName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.Ordinal));
        if (fullName is null)
        {
            throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        }

        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new FileNotFoundException($"Embedded resource stream null: {fullName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // よく使う型を C# の型名表記に変換する。
    private static string FormatTypeName(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(DateTime)) return "DateTime";
        return type.Name;
    }
}

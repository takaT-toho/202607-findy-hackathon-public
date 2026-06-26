namespace SreAgent;

// 実行ディレクトリから親をたどって、.git があるリポジトリのルートを探す共有ヘルパー。
public static class RepoLocator
{
    // .git を含むディレクトリの絶対パスを返す。見つからなければ null。
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }
}

using System.Runtime.InteropServices.JavaScript;

Page.Log($"threads build: {(OperatingSystem.IsBrowser() ? "browser" : "?")}, main thread id {Environment.CurrentManagedThreadId}");
await Task.Yield();
Spike.Bench.RunAll(Page.Log, [1, 2, 4, 8], 600);

static partial class Page
{
    public static void Log(string line)
    {
        Console.WriteLine(line);
        try
        {
            AppendLine(line);
        }
        catch (Exception e)
        {
            Console.WriteLine("(page log failed: " + e.GetType().Name + ")");
        }
    }

    [JSImport("dom.appendLine", "main.js")]
    internal static partial void AppendLine(string line);
}

namespace PlayMap.Tests;

/// <summary>A stand-in for a compiler or a tool that starts a helper process, as a linker or npm may, which inherits the tool's stdout
/// and stderr and holds them open after the tool has exited, until the test lets it go (<see cref="DisposeAsync"/>) or for about two
/// minutes at most. A run that waits for the end of the tool's pipes cannot end while the helper holds them, however the machine is
/// paced; a run that ends with the tool ends at once. On Linux the tool is <c>/bin/sh</c>, on Windows a batch file whose helper is
/// started with <c>start /b</c>.</summary>
internal sealed class PipeHolder : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("playmap-pipeholder-");

    /// <summary>A folder of the holder's own, removed with it.</summary>
    public string Folder => _dir.FullName;

    /// <summary>The file whose making lets the helper go; the helper takes it away as it ends.</summary>
    private string LetGo => Path.Combine(Folder, "let-go");

    /// <summary>A tool that starts the helper, makes <paramref name="makes"/> (an empty file), writes <paramref name="says"/> on its
    /// stdout and exits 0.</summary>
    public (string Tool, string[] Args) Tool(string makes, string says)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("/bin/sh", ["-c",
                "(i=0; while [ ! -e \"$1\" ] && [ \"$i\" -lt 1200 ]; do sleep 0.1; i=$((i+1)); done; rm -f \"$1\") & : > \"$2\"; echo \"$3\"",
                "sh", LetGo, makes, says]);
        }
        File.WriteAllText(Path.Combine(Folder, "hold.cmd"),
            "@echo off\r\nfor /l %%i in (1,1,120) do (\r\n  if exist \"%~dp0let-go\" (\r\n    del \"%~dp0let-go\"\r\n    exit /b 0\r\n  )\r\n"
            + "  ping -n 2 127.0.0.1 >nul\r\n)\r\ndel \"%~dp0let-go\" 2>nul\r\n");
        var tool = Path.Combine(Folder, "tool.cmd");
        File.WriteAllText(tool, "@echo off\r\nstart \"\" /b cmd /d /c \"%~dp0hold.cmd\"\r\ntype nul > \"%~1\"\r\necho %~2\r\n");
        return (tool, [makes, says]);
    }

    /// <summary>Lets the helper go, waits for it to take let-go away (it looks every 0.1 s, every second on Windows), and removes the
    /// folder.</summary>
    public async ValueTask DisposeAsync()
    {
        await File.WriteAllTextAsync(LetGo, "");
        for (var i = 0; i < 600 && File.Exists(LetGo); i++)
            await Task.Delay(50);
        _dir.Delete(recursive: true);
    }
}

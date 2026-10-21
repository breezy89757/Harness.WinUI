// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Files;
using Harness.Core.Tools;

namespace Harness.Core.Tests;

public sealed class FileToolTests
{
    private static (TempDir Dir, List<Microsoft.Extensions.AI.AITool> Tools, FileCheckpoints Checkpoints) Setup(Action<TempDir>? seed = null)
    {
        var dir = new TempDir();
        seed?.Invoke(dir);
        var checkpoints = new FileCheckpoints(Path.Combine(dir.Path, "..", "ckpt-" + Guid.NewGuid().ToString("N")[..6]));
        var tools = FileTools.Create(new Sandbox(dir.Path), new CountingApprover(), dir.NewPermissions(), checkpoints: checkpoints).ToList();
        return (dir, tools, checkpoints);
    }

    [Fact]
    public async Task ReadFile_NumbersLinesAndSaysHowToContinue()
    {
        var (dir, tools, _) = Setup(d => d.Write("big.txt", string.Join("\n", Enumerable.Range(1, 50).Select(i => "line " + i))));
        using (dir)
        {
            var result = await tools.CallAsync("read_file", new() { ["path"] = "big.txt", ["limit"] = 10 });

            Assert.Contains("lines 1-10 of 50", result);
            Assert.Contains("call read_file again with offset=11", result);
            Assert.Contains("   1→line 1", result);
        }
    }

    [Fact]
    public async Task ReadFile_ReadsTheWholeFileByDefault()
    {
        var (dir, tools, _) = Setup(d => d.Write("a.txt", "one\ntwo\nthree\n"));
        using (dir)
        {
            var result = await tools.CallAsync("read_file", new() { ["path"] = "a.txt" });

            Assert.Contains("3 lines (the whole file)", result);
        }
    }

    [Fact]
    public async Task SearchText_ShowsContextAndCanListOnlyFiles()
    {
        var (dir, tools, _) = Setup(d =>
        {
            d.Write("src/a.cs", "x\nneedle\ny\n");
            d.Write("src/b.cs", "nothing\n");
        });
        using (dir)
        {
            var withContext = await tools.CallAsync("search_text", new() { ["query"] = "needle", ["context_lines"] = 1 });
            var filesOnly = await tools.CallAsync("search_text", new() { ["query"] = "needle", ["files_only"] = true });

            Assert.Contains("src/a.cs-1- x", withContext);
            Assert.Contains("src/a.cs:2: needle", withContext);
            Assert.Contains("src/a.cs-3- y", withContext);
            Assert.Contains("src/a.cs  (1 match)", filesOnly);
            Assert.DoesNotContain("b.cs", filesOnly);
        }
    }

    [Fact]
    public async Task EditFile_ReportsTheChangedLines()
    {
        var (dir, tools, _) = Setup(d => d.Write("calc.py", "def f(n):\n    return n / (len(n) + 1)\n"));
        using (dir)
        {
            var result = await tools.CallAsync("edit_file", new() { ["path"] = "calc.py", ["old_text"] = "(len(n) + 1)", ["new_text"] = "len(n)" });

            Assert.Contains("+1 -1", result);
            Assert.Contains("- ", result);
            Assert.Contains("+ ", result);
            Assert.Equal("def f(n):\n    return n / len(n)\n", dir.Read("calc.py"));
        }
    }

    [Fact]
    public async Task AgentsMd_CanOnlyBeWrittenWhenAllowed()
    {
        using var dir = new TempDir();
        var sandbox = new Sandbox(dir.Path);
        var tools = FileTools.Create(sandbox, new CountingApprover(), dir.NewPermissions()).ToList();

        var blocked = await tools.CallAsync("write_file", new() { ["path"] = "AGENTS.md", ["content"] = "x" });
        Assert.StartsWith("Error", blocked);
        Assert.False(dir.Exists("AGENTS.md"));

        sandbox.AllowInstructionsWrite = true;
        var allowed = await tools.CallAsync("write_file", new() { ["path"] = "AGENTS.md", ["content"] = "x" });

        Assert.StartsWith("Created", allowed);
        Assert.True(dir.Exists("AGENTS.md"));
    }

    [Fact]
    public async Task ListAndFind_LeaveOutWhatGitIgnores_UnlessAsked()
    {
        var (dir, tools, _) = Setup(d =>
        {
            d.Write(".gitignore", "out/\n*.log\n");
            d.Write("src/app.cs", "x");
            d.Write("out/app.cs", "x");
            d.Write("src/debug.log", "x");
        });
        using (dir)
        {
            var found = await tools.CallAsync("find_files", new() { ["pattern"] = "**/*" });
            var foundAll = await tools.CallAsync("find_files", new() { ["pattern"] = "**/*", ["include_ignored"] = true });

            Assert.Contains("src/app.cs", found);
            Assert.DoesNotContain("out/app.cs", found);
            Assert.DoesNotContain("debug.log", found);
            Assert.Contains("out/app.cs", foundAll);
            Assert.Contains("debug.log", foundAll);
        }
    }

    [Fact]
    public async Task MissingArgument_GetsAPlainMessage()
    {
        var (dir, tools, _) = Setup();
        using (dir)
        {
            var result = await tools.CallAsync("write_file", new() { ["content"] = "x" });

            Assert.Contains("missing the required argument(s): path", result);
        }
    }

    [Fact]
    public async Task Undo_RestoresEditedFilesRemovesNewOnesAndUnmoves()
    {
        var (dir, tools, checkpoints) = Setup(d =>
        {
            d.Write("calc.py", "original\n");
            d.Write("keep.txt", "keep\n");
        });
        using (dir)
        {
            checkpoints.BeginTurn("t1");
            await tools.CallAsync("edit_file", new() { ["path"] = "calc.py", ["old_text"] = "original", ["new_text"] = "changed" });
            await tools.CallAsync("write_file", new() { ["path"] = "new.txt", ["content"] = "new" });
            await tools.CallAsync("move_file", new() { ["source"] = "keep.txt", ["destination"] = "moved/keep.txt" });
            Assert.Equal(3, checkpoints.ChangedFiles("t1").Count);

            var problems = checkpoints.Undo("t1");

            Assert.Empty(problems);
            Assert.Equal("original\n", dir.Read("calc.py"));
            Assert.False(dir.Exists("new.txt"));
            Assert.True(dir.Exists("keep.txt"));
            Assert.False(dir.Exists("moved/keep.txt"));
        }
    }

    [Fact]
    public async Task Undo_OnlyUndoesTheTurnItIsAskedFor()
    {
        var (dir, tools, checkpoints) = Setup(d => d.Write("a.txt", "v0"));
        using (dir)
        {
            checkpoints.BeginTurn("t1");
            await tools.CallAsync("write_file", new() { ["path"] = "a.txt", ["content"] = "v1" });
            checkpoints.BeginTurn("t2");
            await tools.CallAsync("write_file", new() { ["path"] = "a.txt", ["content"] = "v2" });

            checkpoints.Undo("t2");

            Assert.Equal("v1", dir.Read("a.txt"));
        }
    }
}

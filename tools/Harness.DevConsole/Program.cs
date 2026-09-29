// Harness.WinUI — Licensed under the MIT License.
//
// Console harness: exercises Harness.Core end to end against a real OpenAI-compatible endpoint
// (Azure OpenAI's /openai/v1 surface or a LiteLLM gateway) and prints what the stream
// actually reports — text timing, model id, token usage.
//
// Provider resolution matches Harness.WinUI: settings saved through the app's Settings dialog
// (%LOCALAPPDATA%\Harness.WinUI\settings.json, key DPAPI-encrypted) win; otherwise appsettings.json /
// appsettings.local.json + the env var named by Provider.ApiKeyEnvVar.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Harness.Core.Agent;
using Harness.Core.Config;
using Harness.Core.Providers;
using Harness.Core.Tools;

// Usage: Harness.DevConsole [--mcp <mcp.json>] [--yes] [message...]
//   --mcp  MCP config to load (default: %LOCALAPPDATA%\Harness.WinUI\mcp.json if present)
//   --yes  approve every tool call without prompting
//   --image-model <name>  enable generate_image with this model (default: the saved setting)
//   --sandbox <folder>    enable the built-in file tools on this folder
//   --effort <level>      reasoning effort: low / medium / high (default: the model's)
//   --stop-after <secs>   cancel the reply after this many seconds (tests stop + session rollback)
var argList = args.ToList();
var autoApprove = argList.Remove("--yes");
var mcpPath = TakeOption("--mcp");
var imageModelArg = TakeOption("--image-model");
var sandboxArg = TakeOption("--sandbox");
var effortArg = TakeOption("--effort");
var stopAfterArg = TakeOption("--stop-after");

string? TakeOption(string option)
{
    var index = argList.IndexOf(option);
    if (index < 0 || index + 1 >= argList.Count)
        return null;
    var value = argList[index + 1];
    argList.RemoveRange(index, 2);
    return value;
}

AppPaths.MigrateLegacyDataRoot();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables(prefix: "HARNESS_")
    .Build();

var options = config.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>() ?? new HarnessOptions();

var resolved = ProviderResolver.TryResolve(options.Provider);
if (resolved is null)
{
    Console.Error.WriteLine("[config error] No provider configured. Save one via Harness.WinUI's settings dialog,");
    Console.Error.WriteLine($"or set Provider.Endpoint/Model in appsettings and the '{options.Provider.ApiKeyEnvVar}' env var.");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"Provider source : {resolved.Source}");
Console.WriteLine($"Model (config)  : {resolved.Model}");
Console.WriteLine($"API             : {resolved.Api} -> {resolved.Api.Resolve(new Uri(resolved.Endpoint))}");
Console.WriteLine();

await using var tools = new AgentTools();
try
{
    tools.Configure(new ConsoleApprover(autoApprove), new ToolPermissionStore(Path.Combine(Path.GetTempPath(), "harness-devconsole-permissions.json")));
    if ((imageModelArg ?? resolved.ImageModel) is { } imageModel)
        tools.SetImageGeneration(new ImageGenerationSettings(resolved.Endpoint, resolved.ApiKey, imageModel));
    tools.SetSandbox(sandboxArg);

    var servers = McpConfig.Load(mcpPath);
    if (servers.Count > 0)
    {
        Console.WriteLine($"Starting {servers.Count} MCP server(s)...");
        await tools.Mcp.StartAsync(servers);
        foreach (var s in tools.Mcp.Servers)
            Console.WriteLine(s.State == McpServerState.Connected ? $"  [ok]   {s.Name}: {s.ToolCount} tools" : $"  [{s.State}] {s.Name}: {s.Error}");
    }
    Console.WriteLine($"Tools: {string.Join(", ", tools.All.Select(t => t.Name))}");
    Console.WriteLine();

    var session = new ChatSession(
        ChatClientFactory.Create(resolved), options, () => tools.All, ChatClientFactory.SupportsReasoningSummaries(resolved));

    var testMessage = argList.Count > 0 ? string.Join(' ', argList) : "安安,用三句話介紹你自己";
    Console.WriteLine($"> {testMessage}");
    Console.WriteLine();

    var sw = Stopwatch.StartNew();
    TimeSpan? firstText = null;
    var textChunks = 0;
    var totalChars = 0;
    UsageReported? usage = null;
    var otherEvents = new List<string>();

    using var stop = new CancellationTokenSource();
    if (double.TryParse(stopAfterArg, out var stopAfter))
        stop.CancelAfter(TimeSpan.FromSeconds(stopAfter));

    var stateBefore = await session.SaveStateAsync();
    try
    {
        await foreach (var evt in session.SendAsync(testMessage, AppPreferences.ParseEffort(effortArg), stop.Token))
        {
            switch (evt)
            {
                case TextDelta t:
                    firstText ??= sw.Elapsed;
                    textChunks++;
                    totalChars += t.Text.Length;
                    Console.Write(t.Text);
                    break;
                case UsageReported u:
                    usage = usage?.Add(u) ?? u;
                    break;
                case ReasoningDelta r:
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(r.Text);
                    Console.ResetColor();
                    break;
                case ToolCallStarted call:
                    Console.WriteLine($"\n  [tool call] {call.Name} {JsonSerializer.Serialize(call.Arguments)}");
                    break;
                case ToolCallCompleted done:
                    var result = done.Exception?.Message ?? JsonSerializer.Serialize(done.Result);
                    Console.WriteLine($"  [tool done] {(result.Length > 160 ? result[..160] + "..." : result)}");
                    break;
                default:
                    otherEvents.Add(evt.GetType().Name);
                    break;
            }
        }
    }
    catch (Exception) when (stop.IsCancellationRequested)
    {
        Console.WriteLine($"\n[stopped after {sw.Elapsed.TotalSeconds:F1}s — session rolled back]");
        await session.RestoreStateAsync(stateBefore);
    }

    sw.Stop();

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("--- stream stats ---");
    Console.WriteLine($"Text chunks         : {textChunks}");
    Console.WriteLine($"Total characters    : {totalChars}");
    Console.WriteLine($"Time to first text  : {firstText?.TotalMilliseconds:F0} ms");
    Console.WriteLine($"Total time          : {sw.Elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine(usage is null
        ? "Usage               : (not reported)"
        : $"Usage               : in={usage.InputTokens} out={usage.OutputTokens} total={usage.TotalTokens} reasoning={usage.ReasoningTokens} cached={usage.CachedInputTokens}");
    if (otherEvents.Count > 0)
        Console.WriteLine($"Other events        : {string.Join(", ", otherEvents)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[{ex.GetType().Name}] {ex.Message}");
    Environment.ExitCode = 1;
}

sealed class ConsoleApprover(bool autoApprove) : IToolApprover
{
    public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  [approval] {request.ServerName}/{request.ToolName} {JsonSerializer.Serialize(request.Arguments)}");
        if (autoApprove)
        {
            Console.WriteLine("  [approval] auto-approved (--yes)");
            return Task.FromResult(ToolApprovalDecision.AllowOnce);
        }

        Console.Write("  Allow? [y]es / [a]lways / [n]o: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return Task.FromResult(answer switch
        {
            "y" or "yes" => ToolApprovalDecision.AllowOnce,
            "a" or "always" => ToolApprovalDecision.AlwaysAllow,
            _ => ToolApprovalDecision.Deny,
        });
    }
}

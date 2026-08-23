using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.Windows;

/// <summary>
/// Windows adapter for the existing Sysinternals Handle64 singleton technique.
/// Tool acquisition remains a host concern so the UI can preserve its existing
/// download/consent behavior while both frontends share the strategy contract.
/// </summary>
public sealed partial class WindowsHandle64MultiInstanceStrategy(
    Func<CancellationToken, ValueTask<string>> handlePathProvider) : IRobloxMultiInstanceStrategy
{
    private string? _handlePath;
    public RobloxPlatform Platform => RobloxPlatform.Windows;

    public async ValueTask<RobloxLaunchPreparation> PrepareAsync(
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("administrator-required");
        var path = await handlePathProvider(cancellationToken).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("handle64-not-found");
        _handlePath = Path.GetFullPath(path);
        return RobloxLaunchPreparation.Success(request);
    }

    public async ValueTask<SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default)
    {
        if (_handlePath is null)
            return new SingletonReleaseResult(SingletonReleaseStatus.Failed, DiagnosticCode: "strategy-not-prepared");

        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        if (processes.Length == 0)
            return new SingletonReleaseResult(SingletonReleaseStatus.AlreadyAbsent, DiagnosticCode: "no-running-client");
        try
        {
            foreach (var process in processes)
            {
                var query = await RunAsync(_handlePath, cancellationToken,
                    "-accepteula", "-nobanner", "-a", "-p", process.Id.ToString()).ConfigureAwait(false);
                if (query.ExitCode != 0)
                    return new SingletonReleaseResult(SingletonReleaseStatus.Failed, query.ExitCode, "handle-query-failed");
                foreach (var handle in ParseHandles(query.Output))
                {
                    var close = await RunAsync(_handlePath, cancellationToken,
                        "-accepteula", "-nobanner", "-c", handle, "-p", process.Id.ToString(), "-y").ConfigureAwait(false);
                    if (close.ExitCode != 0)
                        return new SingletonReleaseResult(SingletonReleaseStatus.Failed, close.ExitCode, "handle-close-failed");
                }
            }

            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (process)
                {
                    var verify = await RunAsync(_handlePath, cancellationToken,
                        "-accepteula", "-nobanner", "-a", "-p", process.Id.ToString()).ConfigureAwait(false);
                    if (verify.ExitCode != 0 || ParseHandles(verify.Output).Count != 0)
                        return new SingletonReleaseResult(SingletonReleaseStatus.Failed, verify.ExitCode, "singleton-still-present");
                }
            }
            return new SingletonReleaseResult(SingletonReleaseStatus.Released, DiagnosticCode: "verified");
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new SingletonReleaseResult(SingletonReleaseStatus.Failed, DiagnosticCode: "handle-operation-failed");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public ValueTask<MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MacLaunchLevel?>(null);

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static List<string> ParseHandles(string output)
    {
        var handles = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = HandleLine().Match(line);
            if (match.Success && SingletonName().IsMatch(match.Groups["name"].Value.Trim()))
                handles.Add(match.Groups["id"].Value);
        }
        return handles;
    }

    private static async ValueTask<HandleResult> RunAsync(
        string path, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("handle-start-failed");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new HandleResult(process.ExitCode, string.Join(Environment.NewLine, await output, await error));
    }

    [GeneratedRegex(@"^\s*(?<id>[0-9A-Fa-f]+):\s+\S+\s+(?<name>.+)$")]
    private static partial Regex HandleLine();

    [GeneratedRegex(@"\\ROBLOX_singleton(?:Event|Mutex)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingletonName();

    private sealed record HandleResult(int ExitCode, string Output);
}

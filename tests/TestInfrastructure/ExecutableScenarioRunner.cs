using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RobloxAccountManager.TestInfrastructure;

public static class ExecutableScenarioRunner
{
    public static async Task RunAsync(Assembly assembly, TimeSpan timeout)
    {
        var assemblyPath = assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assemblyPath);

        using var process = Process.Start(startInfo)
            ?? throw new AssertFailedException($"Could not start scenario executable '{assemblyPath}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new AssertFailedException($"Scenario executable exceeded its {timeout.TotalMinutes:0.#}-minute timeout.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode,
            $"Scenario executable failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}

using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public sealed record MacOverlayDiagnosticSummary(
    int ClientCount,
    int ReadyClientCount,
    int DiagnosticCount)
{
    public static MacOverlayDiagnosticSummary Summarize(
        IReadOnlyList<MacOverlayClientDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var clients = diagnostics
            .GroupBy(diagnostic => (diagnostic.AccountId, diagnostic.ProcessId))
            .ToArray();
        return new(
            clients.Length,
            clients.Count(client => client.Any(diagnostic => diagnostic.IsReady)),
            diagnostics.Count);
    }
}

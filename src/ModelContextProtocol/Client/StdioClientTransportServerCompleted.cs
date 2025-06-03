namespace ModelContextProtocol.Client;

/// <summary>
/// Provides completion information about an MCP stdio server process.
/// </summary>
public sealed class StdioClientTransportServerCompleted
{
    /// <summary>Gets or sets the process' exit code.</summary>
    public int? ProcessExitCode { get; set; }

    /// <summary>Gets or sets any captured information from the process' standard error.</summary>
    public string? StandardErrorLog { get; set; }
}

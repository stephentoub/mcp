using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

namespace ModelContextProtocol.Client;

/// <summary>Provides the client side of a stdio-based session transport.</summary>
internal sealed class StdioClientSessionTransport : StreamClientSessionTransport
{
    private readonly StdioClientTransportOptions _options;
    private readonly Process _process;
    private readonly Queue<string> _stderrRollingLog;
    private int _cleanedUp;
    private int _raisedUnexpectedExitEvent;

    public StdioClientSessionTransport(StdioClientTransportOptions options, Process process, string endpointName, Queue<string> stderrRollingLog, ILoggerFactory? loggerFactory)
        : base(process.StandardInput, process.StandardOutput, endpointName, loggerFactory)
    {
        _process = process;
        _options = options;
        _stderrRollingLog = stderrRollingLog;
    }

    /// <inheritdoc/>
    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await base.SendMessageAsync(message, cancellationToken);
        }
        catch (IOException)
        {
            // We failed to send due to an I/O error. If the server process has exited, which is then very likely the cause
            // for the I/O error, we should throw an exception for that instead.
            if (await GetUnexpectedExitExceptionAsync(cancellationToken).ConfigureAwait(false) is Exception processExitException)
            {
                throw processExitException;
            }

            throw;
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask CleanupAsync(Exception? error = null, CancellationToken cancellationToken = default)
    {
        // Only clean up once.
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
        {
            return;
        }

        // We've not yet forcefully terminated the server. If it's already shut down, something went wrong,
        // so create an exception with details about that.
        error ??= await GetUnexpectedExitExceptionAsync(cancellationToken).ConfigureAwait(false);

        // Now terminate the server process.
        try
        {
            StdioClientTransport.DisposeProcess(_process, processRunning: true, _options.ShutdownTimeout, Name);
        }
        catch (Exception ex)
        {
            LogTransportShutdownFailed(Name, ex);
        }

        // And handle cleanup in the base type.
        await base.CleanupAsync(error, cancellationToken);
    }

    private async ValueTask<Exception?> GetUnexpectedExitExceptionAsync(CancellationToken cancellationToken)
    {
        if (!StdioClientTransport.HasExited(_process))
        {
            return null;
        }

        Debug.Assert(StdioClientTransport.HasExited(_process));
        int? exitCode = null;
        try
        {
            // The process has exited, but we still need to ensure stderr has been flushed.
#if NET
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
            _process.WaitForExit();
#endif
            exitCode = _process.ExitCode;
        }
        catch { }

        string? stdErrLog = GetStdErrLog();
        RaiseUnexpectedCallback(exitCode, stdErrLog);

        string errorMessage = $"MCP server process exited unexpectedly{(exitCode is not null ? $" (exit code: {(uint?)exitCode})" : null)}.";

        if (stdErrLog is not null)
        {
            errorMessage =
                $"{errorMessage}{Environment.NewLine}" +
                $"Server's stderr tail:{Environment.NewLine}" +
                $"{stdErrLog}";
        }

        return new IOException(errorMessage);
    }

    private string? GetStdErrLog()
    {
        lock (_stderrRollingLog)
        {
            if (_stderrRollingLog.Count > 0)
            {
                return string.Join(Environment.NewLine, _stderrRollingLog);
            }
        }

        return null;
    }

    private void RaiseUnexpectedCallback(int? processExitCode, string? stderrLog)
    {
        if (_options.ServerProcessExitedUnexpectedly is { } callback &&
            Interlocked.Exchange(ref _raisedUnexpectedExitEvent, 1) == 0)
        {
            callback(new()
            {
                ProcessExitCode = processExitCode,
                StandardErrorLog = stderrLog,
            });
        }
    }
}

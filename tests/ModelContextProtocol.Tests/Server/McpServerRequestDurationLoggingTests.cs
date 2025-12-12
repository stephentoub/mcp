using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ModelContextProtocol.Tests.Server;

public class McpServerRequestDurationLoggingTests : ClientServerTestBase
{
    public McpServerRequestDurationLoggingTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    protected override void ConfigureServices(ServiceCollection services, IMcpServerBuilder mcpServerBuilder)
    {
        mcpServerBuilder.WithTools<TestTools>();
    }

    [Fact]
    public async Task RequestHandlerCompleted_LogsElapsedTime()
    {
        // Arrange
        await using McpClient client = await CreateMcpClientForServer();

        // Act
        var result = await client.CallToolAsync(
            "delayed_tool",
            new Dictionary<string, object?> { ["delayMs"] = 50 },
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);

        // Verify the log message contains duration in milliseconds
        Assert.Contains(MockLoggerProvider.LogMessages, log =>
        {
            if (log.LogLevel != LogLevel.Information ||
                !log.Message.Contains("request handler completed") ||
                !log.Message.Contains("ms"))
            {
                return false;
            }

            // Extract duration from log message (should be in format "...completed in XXXms.")
            var match = System.Text.RegularExpressions.Regex.Match(log.Message, @"completed in (\d+(?:\.\d+)?)ms");
            if (!match.Success)
            {
                return false;
            }

            double elapsedMs = double.Parse(match.Groups[1].Value);

            // Duration should be at least 50ms (the delay we introduced)
            // and less than 5 seconds
            return elapsedMs >= 50 && elapsedMs < 5000;
        });
    }

    [Fact]
    public async Task RequestHandlerCompleted_LogsForQuickRequests()
    {
        // Arrange
        await using McpClient client = await CreateMcpClientForServer();

        // Act
        var result = await client.CallToolAsync("quick_tool", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);

        // Verify the log message contains duration in milliseconds
        Assert.Contains(MockLoggerProvider.LogMessages, log =>
        {
            if (log.LogLevel != LogLevel.Information ||
                !log.Message.Contains("request handler completed") ||
                !log.Message.Contains("ms"))
            {
                return false;
            }

            // Extract duration from log message
            var match = System.Text.RegularExpressions.Regex.Match(log.Message, @"completed in (\d+(?:\.\d+)?)ms");
            if (!match.Success)
            {
                return false;
            }

            double elapsedMs = double.Parse(match.Groups[1].Value);

            // Even quick requests should log some duration (should be very small)
            // Should complete quickly (less than 1 second)
            return elapsedMs >= 0 && elapsedMs < 1000;
        });
    }

    [McpServerToolType]
    private sealed class TestTools
    {
        [McpServerTool, Description("A tool that delays for a specified time")]
        public static async Task<string> DelayedTool(
            [Description("Delay in milliseconds")] int delayMs,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delayMs, cancellationToken);
            return $"Delayed for {delayMs}ms";
        }

        [McpServerTool, Description("A tool that completes quickly")]
        public static string QuickTool()
        {
            return "Quick result";
        }
    }
}

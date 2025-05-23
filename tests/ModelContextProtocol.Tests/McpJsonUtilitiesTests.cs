﻿using System.Text.Json;

namespace ModelContextProtocol.Tests;

public static class McpJsonUtilitiesTests
{
    [Fact]
    public static void DefaultOptions_IsSingleton()
    {
        var options = McpJsonUtilities.DefaultOptions;

        Assert.NotNull(options);
        Assert.True(options.IsReadOnly);
        Assert.Same(options, McpJsonUtilities.DefaultOptions);
    }

    [Fact]
    public static void DefaultOptions_UseReflectionWhenEnabled()
    {
        var options = McpJsonUtilities.DefaultOptions;
        Type anonType = new { Id = 42 }.GetType();

        Assert.Equal(JsonSerializer.IsReflectionEnabledByDefault, options.TryGetTypeInfo(anonType, out _));
    }
}

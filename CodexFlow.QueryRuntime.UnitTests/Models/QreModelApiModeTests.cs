using CodexFlow.QueryRuntime.Models;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class QreModelApiModeTests
{
    [Theory]
    [InlineData(null, QreModelApiMode.ChatCompletions)]
    [InlineData("", QreModelApiMode.ChatCompletions)]
    [InlineData("   ", QreModelApiMode.ChatCompletions)]
    [InlineData("chat", QreModelApiMode.ChatCompletions)]
    [InlineData("chat-completions", QreModelApiMode.ChatCompletions)]
    [InlineData("chat_completions", QreModelApiMode.ChatCompletions)]
    [InlineData("completions", QreModelApiMode.ChatCompletions)]
    [InlineData("responses", QreModelApiMode.Responses)]
    [InlineData("response", QreModelApiMode.Responses)]
    [InlineData("anthropic", QreModelApiMode.AnthropicMessages)]
    [InlineData("anthropic-messages", QreModelApiMode.AnthropicMessages)]
    [InlineData("Messages", QreModelApiMode.AnthropicMessages)]
    public void Parse_ResolvesKnownValues(string? value, QreModelApiMode expected)
    {
        Assert.Equal(expected, QreModelApiModeParser.Parse(value));
    }

    [Fact]
    public void Parse_UnknownValue_ThrowsWithClearMessage()
    {
        var ex = Assert.Throws<QreUnsupportedApiModeValueException>(
            () => QreModelApiModeParser.Parse("grpc"));

        Assert.Equal("grpc", ex.Value);
        Assert.Contains("Unsupported QRE api mode value 'grpc'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("chat-completions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnknownValue_ReturnsFalseWithError()
    {
        var ok = QreModelApiModeParser.TryParse("grpc", out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Descriptor_Create_ParsesApiModeAndAbsoluteUri()
    {
        var descriptor = QreModelClientDescriptor.Create(
            "https://example.test/v1", "key", "qwen3-next", "anthropic-messages");

        Assert.Equal(QreModelApiMode.AnthropicMessages, descriptor.ApiMode);
        Assert.Equal(new Uri("https://example.test/v1"), descriptor.ApiUrl);
        Assert.Equal("qwen3-next", descriptor.Model);
    }

    [Fact]
    public void Descriptor_Create_RejectsSchemelessUri()
    {
        Assert.Throws<UriFormatException>(
            () => QreModelClientDescriptor.Create("example.test/v1", "key", "qwen3-next"));
    }

    [Fact]
    public void Descriptor_Create_RejectsBlankModel()
    {
        Assert.Throws<ArgumentException>(
            () => QreModelClientDescriptor.Create("https://example.test/v1", "key", "   "));
    }
}

using SecureChat.Client.Services;
using Xunit;

namespace SecureChat.Tests;

public class NetworkServiceTests
{
    [Theory]
    [InlineData("http://localhost:9000", "http://localhost:9000/hub")]
    [InlineData("http://localhost:9000/", "http://localhost:9000/hub")]
    [InlineData("http://localhost:9000///", "http://localhost:9000/hub")]
    [InlineData("https://chat.example.com", "https://chat.example.com/hub")]
    [InlineData("ws://localhost:9000", "http://localhost:9000/hub")]
    [InlineData("ws://localhost:9000/", "http://localhost:9000/hub")]
    [InlineData("wss://chat.example.com", "https://chat.example.com/hub")]
    [InlineData("wss://chat.example.com/", "https://chat.example.com/hub")]
    [InlineData("localhost:9000", "http://localhost:9000/hub")]
    [InlineData("localhost:9000/", "http://localhost:9000/hub")]
    [InlineData("   http://localhost:9000   ", "http://localhost:9000/hub")]
    [InlineData("   ws://chat.local:8080/   ", "http://chat.local:8080/hub")]
    public void NormalizeHubUrl_FormatsCorrectly(string input, string expected)
    {
        string actual = NetworkService.NormalizeHubUrl(input);
        Assert.Equal(expected, actual);
    }
}

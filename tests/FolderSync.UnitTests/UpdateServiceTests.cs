using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="UpdateService"/>.
/// Validates update detection, version comparison, and network resilience using a mocked HttpClient.
/// </summary>
public class UpdateServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private readonly UpdateService _sut;

    public UpdateServiceTests()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        var client = new HttpClient(_mockHttpMessageHandler.Object) { BaseAddress = new Uri("https://api.github.com") };
        
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(client);

        _sut = new UpdateService(_mockHttpClientFactory.Object);
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content, Exception? exception = null)
    {
        var setup = _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            
        if (exception != null) 
        {
            setup.ThrowsAsync(exception);
        }
        else 
        {
            setup.ReturnsAsync(new HttpResponseMessage { StatusCode = statusCode, Content = new StringContent(content) });
        }
    }

    // ─── Version Comparison & Logic ───────────────────────────────────────────

    [Fact]
    public async Task CheckForUpdates_WhenRemoteIsNewer_ReturnsUpdateAvailable()
    {
        // Arrange
        string json = @"{ ""tag_name"": ""v99.99.99"", ""html_url"": ""https://github.com"" }";
        SetupHttpResponse(HttpStatusCode.OK, json);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        result.Should().NotBeNull();
        result!.IsUpdateAvailable.Should().BeTrue();
        result.VersionName.Should().Be("v99.99.99");
    }

    [Fact]
    public async Task CheckForUpdates_WhenRemoteVersionEqualsLocal_ReturnsNoUpdate()
    {
        // Arrange
        string currentVer = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.4";
        string json = $@"{{ ""tag_name"": ""v{currentVer}"", ""html_url"": ""https://github.com"" }}";
        SetupHttpResponse(HttpStatusCode.OK, json);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        result.Should().NotBeNull();
        result!.IsUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdates_WhenRemoteVersionIsOlder_ShouldReturnNoUpdate()
    {
        // Arrange – simulate a local version that is ahead of the latest remote tag (e.g., development builds)
        string json = @"{ ""tag_name"": ""v0.0.1"", ""html_url"": ""https://github.com"" }";
        SetupHttpResponse(HttpStatusCode.OK, json);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        result.Should().NotBeNull();
        result!.IsUpdateAvailable.Should().BeFalse("a downgrade/rollback version must not be considered an update");
    }

    [Theory]
    [InlineData(@"{ ""tag_name"": ""v1.0.0-beta"", ""html_url"": ""https://github.com"" }")]
    [InlineData(@"{ ""tag_name"": ""1.2.3"", ""html_url"": ""https://github.com"" }")]
    public async Task CheckForUpdates_WithNonStandardTagFormats_ShouldHandleGracefully(string json)
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, json);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert – pre-release and non-prefixed tags must be handled without throwing exceptions.
        if (result != null)
        {
            result.IsUpdateAvailable.Should().BeFalse("pre-release or non-standard tags should not trigger update notifications");
        }
    }

    // ─── HTTP Resilience & Error Handling ──────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CheckForUpdates_WhenHttpError_ShouldReturnNull(HttpStatusCode errorCode)
    {
        // Arrange
        SetupHttpResponse(errorCode, "");

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert – non-success HTTP codes must be handled gracefully.
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(@"{ ""tag_name"": ""not_a_version"" }")]
    [InlineData(@"{ ""tag_name"": ""v1.0.0"" }")] // Missing html_url
    [InlineData("")] // Empty body
    public async Task CheckForUpdates_WithIncompleteOrMalformedData_ReturnsNull(string invalidJson)
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, invalidJson);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        result.Should().BeNull("incomplete JSON payloads cannot be processed");
    }

    [Fact]
    public async Task CheckForUpdates_WhenNetworkException_ReturnsNullWithoutCrashing()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "", new HttpRequestException("No connection"));

        // Act & Assert
        Func<Task> act = () => _sut.CheckForUpdatesAsync();
        await act.Should().NotThrowAsync();
        
        var result = await _sut.CheckForUpdatesAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdates_WhenRequestTimesOut_ShouldReturnNull()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "", new TaskCanceledException("Request timed out"));

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        result.Should().BeNull("network timeouts must result in null, preventing UI freezes");
    }

    // ─── Header Verification ───────────────────────────────────────────────────

    [Fact]
    public async Task CheckForUpdates_ShouldSendCorrectUserAgentHeader()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{}");

        // Act
        await _sut.CheckForUpdatesAsync();

        // Assert
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.Headers.UserAgent.ToString().Contains("FolderSync-AutoUpdater")),
            ItExpr.IsAny<CancellationToken>()
        );
    }
}

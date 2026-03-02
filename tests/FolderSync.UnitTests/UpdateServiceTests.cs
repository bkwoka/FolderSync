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

    [Fact]
    public async Task CheckForUpdates_WhenRemoteIsNewer_ReturnsUpdateAvailable()
    {
        // Arrange: Simulate a newer remote version from the GitHub repository.
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
        // Arrange: Simulate a remote version exactly matching the executing assembly version.
        string currentVer = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.4";
        string json = $@"{{ ""tag_name"": ""v{currentVer}"", ""html_url"": ""https://github.com"" }}";
        SetupHttpResponse(HttpStatusCode.OK, json);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        // Identical versions should result in no update being identified (IsUpdateAvailable: false).
        result.Should().NotBeNull();
        result!.IsUpdateAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(@"{ ""tag_name"": ""not_a_version"" }")]
    public async Task CheckForUpdates_WhenMalformedJson_ReturnsNull(string invalidJson)
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, invalidJson);

        // Act
        var result = await _sut.CheckForUpdatesAsync();

        // Assert
        // Malformed JSON structures should be handled gracefully by returning null.
        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdates_WhenNetworkThrows_ReturnsNullWithoutCrashing()
    {
        // Arrange: Simulate a total network failure/timeout.
        SetupHttpResponse(HttpStatusCode.OK, "", new HttpRequestException("No connection"));

        // Act
        Func<Task> act = async () => await _sut.CheckForUpdatesAsync();

        // Assert
        // The updater should not crash the main thread on network exceptions.
        await act.Should().NotThrowAsync();
        var result = await _sut.CheckForUpdatesAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdates_ShouldSendCorrectUserAgentHeader()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{}");

        // Act
        await _sut.CheckForUpdatesAsync();

        // Assert: Verify that the required User-Agent header is included in the GitHub API request.
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.Headers.UserAgent.ToString().Contains("FolderSync-AutoUpdater")),
            ItExpr.IsAny<CancellationToken>()
        );
    }
}

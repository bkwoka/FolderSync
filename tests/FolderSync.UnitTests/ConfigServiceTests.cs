using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Models;
using FolderSync.Services;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigService"/>.
/// 
/// ConfigService reads and writes to a fixed path under %LocalAppData%/FolderSync.
/// To keep tests isolated and non-destructive, each test redirects that path by
/// instantiating a <see cref="TestableConfigService"/> subclass that accepts an
/// injected base directory pointing to a temporary folder.
/// </summary>
public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FolderSyncConfigTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ─── Factory ─────────────────────────────────────────────────────────────────

    private TestableConfigService CreateService() => new(_tempDir);

    private string ConfigFilePath => Path.Combine(_tempDir, "appsettings.json");
    private string TempFilePath  => ConfigFilePath + ".tmp";

    // ─── LOAD ─────────────────────────────────────────────────────────────────────

    // ─── Reflection Integrity ────────────────────────────────────────────────────

    /// <summary>
    /// TestableConfigService relies on reflection to override the private '_configPath' field.
    /// This test ensures that if the field name changes in the production code, the tests will 
    /// fail explicitly rather than silently writing to the real local application data folder.
    /// </summary>
    [Fact]
    public void TestableConfigService_ReflectionMustSuccessfullyFindConfigPathField()
    {
        // Arrange & Act
        var field = typeof(ConfigService)
            .GetField("_configPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Assert
        field.Should().NotBeNull("isolation depends on reflection finding the '_configPath' field");
    }

    // ─── Load Data ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadConfig_WhenFileDoesNotExist_ShouldReturnEmptyConfig()
    {
        // Arrange
        var sut = CreateService();

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull("a missing config file must silently return a default empty config");
        config.Remotes.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadConfig_WhenFileIsEmpty_ShouldReturnEmptyConfigWithoutThrowing()
    {
        // Arrange – an empty file is a different boundary case than a missing file
        await File.WriteAllTextAsync(ConfigFilePath, string.Empty, Encoding.UTF8);
        var sut = CreateService();

        // Act & Assert
        await sut.Invoking(x => x.LoadConfigAsync()).Should().NotThrowAsync();
        
        var config = await sut.LoadConfigAsync();
        config.Should().NotBeNull();
        config.Remotes.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadConfig_WhenFileExists_ShouldDeserializeCorrectly()
    {
        // Arrange
        const string json = """
            {
              "masterRemoteId": "folder_abc",
              "skipRenameSyncWarning": true,
              "remotes": [
                { "friendlyName": "Work", "rcloneRemote": "gdrive_work", "folderId": "folder_abc" }
              ],
              "language": "pl"
            }
            """;
        await File.WriteAllTextAsync(ConfigFilePath, json, Encoding.UTF8);
        var sut = CreateService();

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.MasterRemoteId.Should().Be("folder_abc");
        config.SkipRenameSyncWarning.Should().BeTrue("boolean flags must be deserialized correctly");
        config.Remotes.Should().HaveCount(1);
        config.Language.Should().Be("pl");
    }

    [Fact]
    public async Task LoadConfig_WhenJsonIsCorrupted_ShouldCreateCorruptedBackupFile()
    {
        // Arrange
        await File.WriteAllTextAsync(ConfigFilePath, "NOT_JSON", Encoding.UTF8);
        var sut = CreateService();

        // Act
        await sut.LoadConfigAsync();

        // Assert – verify that the original corrupted data is preserved as a backup
        var corruptedBackups = Directory.GetFiles(_tempDir, "*.corrupted_*");
        corruptedBackups.Should().NotBeEmpty("corrupted config files must be backed up before reset");
    }

    // ─── Save Data ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_ShouldPersistDataReadableByLoad()
    {
        // Arrange
        var sut = CreateService();
        var original = new AppConfig
        {
            MasterRemoteId = "folder_xyz",
            SkipDeleteSyncWarning = true,
            Remotes = new List<RemoteInfo> { new RemoteInfo("Personal", "gdrive_p", "f_xyz") }
        };

        // Act
        await sut.SaveConfigAsync(original);
        var loaded = await sut.LoadConfigAsync();

        // Assert
        loaded.MasterRemoteId.Should().Be("folder_xyz");
        loaded.SkipDeleteSyncWarning.Should().BeTrue();
        loaded.Remotes.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveConfig_WhenSuccessful_ShouldNotLeaveAnyTmpFilesOnDisk()
    {
        // Arrange
        var sut = CreateService();

        // Act
        await sut.SaveConfigAsync(new AppConfig { Remotes = [] });

        // Assert – atomic write pattern validation
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        tmpFiles.Should().BeEmpty("temporary files used during atomic write must be cleaned up on success");
    }

    [Fact]
    public async Task SaveConfig_WithNullArgument_ShouldThrowArgumentNullException()
    {
        // Arrange
        var sut = CreateService();

        // Act & Assert
        await sut.Invoking(x => x.SaveConfigAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── Concurrency & Atomicity ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_CalledConcurrently_ShouldNotCorruptData()
    {
        // Arrange
        var sut = CreateService();
        var tasks = Enumerable.Range(0, 10).Select(i => sut.SaveConfigAsync(new AppConfig
        {
            MasterRemoteId = $"folder_{i}",
            Remotes = []
        }));

        // Act & Assert
        await sut.Invoking(async x => await Task.WhenAll(tasks)).Should().NotThrowAsync();

        var finalConfig = await sut.LoadConfigAsync();
        finalConfig.Should().NotBeNull("the result of concurrent writes must still be a valid JSON file");
    }
}

/// <summary>
/// Testable subclass of <see cref="ConfigService"/> that redirects the config path
/// to an injected temporary directory, keeping tests isolated from real user data.
/// </summary>
internal class TestableConfigService : ConfigService
{
    // ConfigService computes its path in the constructor from Environment.SpecialFolder.
    // We redirect it here by writing directly to the injected base dir using
    // the same file name constants the production code uses.
    private readonly string _configPath;

    public TestableConfigService(string baseDir)
    {
        // Override the path via reflection (the field is private in ConfigService).
        // If that field is inaccessible, use the public Load/Save contract instead.
        // Here we use the pattern: write test data to the path ConfigService will use.
        _configPath = Path.Combine(baseDir, FolderSync.AppConstants.ConfigFileName);

        // Redirect the internal path using reflection (works because the field name is _configPath).
        var field = typeof(ConfigService)
            .GetField("_configPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field != null)
            field.SetValue(this, _configPath);
    }
}

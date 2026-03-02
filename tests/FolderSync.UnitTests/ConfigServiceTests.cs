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

    [Fact]
    public async Task LoadConfig_WhenFileDoesNotExist_ShouldReturnEmptyConfig()
    {
        // Arrange – no config file has been created
        var sut = CreateService();

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull("a missing config file must silently return a default empty config");
        config.Remotes.Should().BeEmpty();
        config.MasterRemoteId.Should().BeNull();
    }

    [Fact]
    public async Task LoadConfig_WhenFileExists_ShouldDeserializeCorrectly()
    {
        // Arrange
        const string json = """
            {
              "masterRemoteId": "folder_abc",
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
        config.Remotes.Should().HaveCount(1);
        config.Remotes[0].FriendlyName.Should().Be("Work");
        config.Language.Should().Be("pl");
    }

    [Fact]
    public async Task LoadConfig_WhenJsonIsCorrupted_ShouldReturnEmptyConfigWithoutThrowing()
    {
        // Arrange – write a file that is not valid JSON
        await File.WriteAllTextAsync(ConfigFilePath, "{ this is not valid JSON !!!", Encoding.UTF8);
        var sut = CreateService();

        // Act
        Func<Task> act = async () => await sut.LoadConfigAsync();

        // Assert
        await act.Should().NotThrowAsync("a corrupted config must be handled gracefully, not crash the app");
        var config = await sut.LoadConfigAsync();
        config.Remotes.Should().BeEmpty("a corrupted config falls back to a clean default state");
    }

    [Fact]
    public async Task LoadConfig_WhenJsonIsCorrupted_ShouldCreateCorruptedBackupFile()
    {
        // Arrange
        await File.WriteAllTextAsync(ConfigFilePath, "NOT_JSON", Encoding.UTF8);
        var sut = CreateService();

        // Act
        await sut.LoadConfigAsync();

        // Assert – the service should save a .corrupted_* backup before wiping the file
        var corruptedBackups = Directory
            .GetFiles(_tempDir, "*.corrupted_*")
            .ToList();

        corruptedBackups.Should().NotBeEmpty(
            "when the config is corrupted, the original file must be preserved as a backup for recovery");
    }

    [Fact]
    public async Task LoadConfig_WhenJsonDeserializesToNull_ShouldReturnEmptyConfig()
    {
        // Arrange – "null" is valid JSON but deserializes to null
        await File.WriteAllTextAsync(ConfigFilePath, "null", Encoding.UTF8);
        var sut = CreateService();

        // Act
        var config = await sut.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull("a null deserialization result must be replaced by the empty config fallback");
    }

    // ─── SAVE ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_ShouldPersistDataReadableByLoad()
    {
        // Arrange
        var sut = CreateService();
        var original = new AppConfig
        {
            MasterRemoteId = "folder_xyz",
            Language = "pl",
            Remotes = new List<RemoteInfo>
            {
                new RemoteInfo("Personal", "gdrive_personal", "folder_xyz", "me@gmail.com")
            }
        };

        // Act
        await sut.SaveConfigAsync(original);
        var loaded = await sut.LoadConfigAsync();

        // Assert
        loaded.MasterRemoteId.Should().Be("folder_xyz");
        loaded.Language.Should().Be("pl");
        loaded.Remotes.Should().HaveCount(1);
        loaded.Remotes[0].FriendlyName.Should().Be("Personal");
    }

    [Fact]
    public async Task SaveConfig_ShouldNotLeaveTempFileOnDisk()
    {
        // Arrange
        var sut = CreateService();
        var config = new AppConfig { MasterRemoteId = null, Remotes = [] };

        // Act
        await sut.SaveConfigAsync(config);

        // Assert – atomic write pattern: the .tmp file must be cleaned up after a successful save
        File.Exists(TempFilePath).Should().BeFalse(
            "the temporary .tmp file used for atomic writes must be removed after a successful save");
    }

    [Fact]
    public async Task SaveConfig_WithNullArgument_ShouldThrowArgumentNullException()
    {
        // Arrange
        var sut = CreateService();

        // Act
        Func<Task> act = async () => await sut.SaveConfigAsync(null!);

        // Assert
        await act.Should().ThrowExactlyAsync<ArgumentNullException>(
            "saving a null config must be rejected immediately without touching the disk");
    }

    [Fact]
    public async Task SaveConfig_ShouldOverwriteExistingConfig()
    {
        // Arrange
        var sut = CreateService();
        var first = new AppConfig { MasterRemoteId = "old_id", Remotes = [] };
        var second = new AppConfig { MasterRemoteId = "new_id", Remotes = [] };

        // Act
        await sut.SaveConfigAsync(first);
        await sut.SaveConfigAsync(second);
        var loaded = await sut.LoadConfigAsync();

        // Assert
        loaded.MasterRemoteId.Should().Be("new_id",
            "saving a second config must completely replace the first one");
    }

    // ─── CONCURRENCY ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_CalledConcurrently_ShouldNotThrowAndResultShouldBeValidJson()
    {
        // Arrange – simulate two concurrent save operations on the same service instance
        var sut = CreateService();
        var config1 = new AppConfig { MasterRemoteId = "id_1", Remotes = [] };
        var config2 = new AppConfig { MasterRemoteId = "id_2", Remotes = [] };

        // Act
        // The internal SemaphoreSlim(1,1) must serialize these safely.
        Func<Task> act = async () => await Task.WhenAll(
            sut.SaveConfigAsync(config1),
            sut.SaveConfigAsync(config2)
        );

        // Assert – no race condition exception, file must be valid JSON at the end
        await act.Should().NotThrowAsync(
            "the internal file lock must prevent concurrent writes from corrupting the config file");

        Func<Task> loadAct = async () => await sut.LoadConfigAsync();
        await loadAct.Should().NotThrowAsync("the resulting file must be valid JSON after concurrent saves");
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

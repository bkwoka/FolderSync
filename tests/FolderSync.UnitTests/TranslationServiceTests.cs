using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FolderSync.Services;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="TranslationService"/>.
/// TranslationService is a singleton responsible for the entire UI translation.
/// These tests verify key retrieval, fallback mechanisms, runtime language switching,
/// and property change notification effectiveness.
/// </summary>
public class TranslationServiceTests : IDisposable
{
    private readonly FolderSync.Services.Interfaces.ITranslationService _originalInstance;

    public TranslationServiceTests()
    {
        _originalInstance = TranslationService.Instance;
    }

    public void Dispose()
    {
        // Restore the original singleton to ensure isolation across different test suites
        TranslationService.SetInstance(_originalInstance);
    }

    private static TranslationService CreateFreshInstance()
    {
        var ctor = typeof(TranslationService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, Type.EmptyTypes, null);

        ctor.Should().NotBeNull("TranslationService must have a private parameterless constructor");
        return (TranslationService)ctor!.Invoke(null);
    }

    [Theory]
    [InlineData("Error_NeedTwoDrives")]
    [InlineData("Log_Header_Prep")]
    [InlineData("Log_Stage0_Sanitize")]
    [InlineData("Log_Stage0_FixingDuplicates")]
    [InlineData("Log_Stage1_Consolidate")]
    [InlineData("Log_Stage1_MovingOrphan")]
    [InlineData("Log_Stage2_Download")]
    [InlineData("Log_Stage2_Upload")]
    [InlineData("Log_Stage2_Distribute")]
    public void Indexer_WithKnownKey_ShouldReturnNonEmptyStringInEnglish(string key)
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");

        // Act
        string result = sut[key];

        // Assert – key must exist and not be an empty string
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotBe($"[{key}]", "key must be found in the English resource file");
    }

    [Theory]
    [InlineData("Error_NeedTwoDrives")]
    [InlineData("Log_Header_Prep")]
    [InlineData("Log_Stage2_Download")]
    public void Indexer_WithKnownKey_ShouldReturnNonEmptyStringInPolish(string key)
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("pl");

        // Act
        string result = sut[key];

        // Assert – key must be translated in the Polish resource file
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotBe($"[{key}]", "key must be found in the Polish resource file");
    }

    [Fact]
    public void Indexer_WithUnknownKey_ShouldReturnKeyWrappedInBrackets()
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");
        const string mysteryKey = "Missing_Translation_XYZ_9999";

        // Act
        string result = sut[mysteryKey];

        // Assert – convention to signal missing keys to developers during development
        result.Should().Be($"[{mysteryKey}]");
    }

    [Fact]
    public void Indexer_AfterCultureSwitch_ShouldReturnTranslationInNewLanguage()
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");
        string englishResult = sut["Log_Stage2_Download"];

        // Act – switch language at runtime without restarting
        sut.Culture = new CultureInfo("pl");
        string polishResult = sut["Log_Stage2_Download"];

        // Assert – translations must update in real-time
        polishResult.Should().NotBe(englishResult);
    }

    [Fact]
    public void Culture_SetToSameValue_ShouldNotFirePropertyChanged()
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");

        var changedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act – set identical culture
        sut.Culture = new CultureInfo("en");

        // Assert – unnecessary re-renders in UI must be avoided
        changedProperties.Should().BeEmpty();
    }

    [Fact]
    public void Culture_WhenChanged_ShouldFirePropertyChangedForBothCultureAndItemIndexer()
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");

        var changedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        sut.Culture = new CultureInfo("pl");

        // Assert – both properties are required to refresh Avalonia bindings successfully
        changedProperties.Should().Contain("Culture");
        changedProperties.Should().Contain("Item");
    }

    [Fact]
    public void SetInstance_ShouldReplaceTheGlobalSingleton()
    {
        // Arrange
        var mockService = new MockTranslationService();

        // Act
        TranslationService.SetInstance(mockService);

        // Assert – ensures DI can successfully override the default singleton instance
        TranslationService.Instance.Should().BeSameAs(mockService);
    }

    [Fact]
    public async Task Culture_ConcurrentReads_ShouldNotThrow()
    {
        // Arrange
        var sut = CreateFreshInstance();
        sut.Culture = new CultureInfo("en");

        // Act – 20 threads reading Culture concurrently
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            _ = sut.Culture;
            _ = sut["Log_Header_Prep"];
        }));

        // Assert – internal locking must prevent race conditions
        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("concurrent reads of Culture must be thread-safe due to the internal lock");
    }

    private sealed class MockTranslationService : FolderSync.Services.Interfaces.ITranslationService
    {
        public string this[string key] => $"MOCK:{key}";
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace FolderSync.UnitTests;

/// <summary>
/// Unit tests for <see cref="AppConstants"/> focused on supply-chain security.
///
/// RcloneBootstrapper uses SHA256 checksums from AppConstants to verify the integrity of the downloaded
/// Rclone binary before it is executed. If these constants are missing, malformed, or incomplete, the
/// security guard silently fails — the application would run an unverified (potentially malicious) binary.
///
/// These tests are intentionally simple but CRITICAL: they act as a compile-time tripwire that fires
/// if anyone accidentally modifies or removes a hash during a routine update, preventing silent security regressions.
/// </summary>
public class AppConstantsTests
{
    // ─── SHA256 Hash Integrity ────────────────────────────────────────────────────

    /// <summary>
    /// SHA256 hex strings are exactly 64 lowercase hexadecimal characters.
    /// Any deviation (wrong length, uppercase, or non-hex chars) means the hash comparison in
    /// RcloneBootstrapper will ALWAYS fail, silently blocking installation for that platform.
    /// </summary>
    [Fact]
    public void RcloneHashes_AllValues_ShouldBeValidLowercaseSha256HexStrings()
    {
        // Arrange
        var sha256Regex = new Regex("^[a-f0-9]{64}$");

        // Act & Assert
        foreach (var (platform, hash) in AppConstants.RcloneHashes)
        {
            hash.Should().MatchRegex(sha256Regex,
                $"hash for platform '{platform}' must be a 64-character lowercase hex SHA256 digest");
        }
    }

    /// <summary>
    /// All four supported deployment platforms must have a corresponding hash entry.
    /// A missing platform hash means that installation on that OS silently skips
    /// integrity verification — a critical supply-chain security gap.
    /// </summary>
    [Theory]
    [InlineData("windows-amd64")]
    [InlineData("linux-amd64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-amd64")]
    public void RcloneHashes_ShouldContainEntryForEveryDeploymentPlatform(string platformKey)
    {
        // Assert
        AppConstants.RcloneHashes.Should().ContainKey(platformKey,
            $"supply-chain verification requires a SHA256 hash for the '{platformKey}' platform");
    }

    /// <summary>
    /// Prevents accidental accumulation of stale or phantom hashes that do not correspond
    /// to a supported platform. Extra entries are a maintenance hazard and could mask missing ones.
    /// </summary>
    [Fact]
    public void RcloneHashes_ShouldContainExactlyFourPlatformEntries()
    {
        // Assert
        AppConstants.RcloneHashes.Should().HaveCount(4,
            "exactly four platforms are supported (win-amd64, linux-amd64, linux-arm64, osx-amd64); " +
            "update this test when adding or removing platform support");
    }

    /// <summary>
    /// All hash values must be unique. If two platforms share a hash, it is likely a copy-paste error
    /// during an Rclone version upgrade, which would allow a wrong binary to pass verification.
    /// </summary>
    [Fact]
    public void RcloneHashes_AllValues_ShouldBeDistinct()
    {
        // Act
        var hashes = AppConstants.RcloneHashes.Values.ToList();
        var distinctHashes = hashes.Distinct().ToList();

        // Assert
        distinctHashes.Should().HaveCount(hashes.Count,
            "every platform binary is different and must have a unique SHA256 hash — " +
            "duplicate hashes indicate a copy-paste error during a version upgrade");
    }

    // ─── Version Format ───────────────────────────────────────────────────────────

    /// <summary>
    /// RcloneBootstrapper builds the download URL from RcloneTargetVersion directly.
    /// A malformed version string causes a 404 at download time and blocks all installations.
    /// </summary>
    [Fact]
    public void RcloneTargetVersion_ShouldFollowSemVerFormatWithVPrefix()
    {
        // Arrange
        var semverRegex = new Regex(@"^v\d+\.\d+\.\d+$");

        // Assert
        AppConstants.RcloneTargetVersion.Should().MatchRegex(semverRegex,
            "the version string must follow the 'vMAJOR.MINOR.PATCH' format used in GitHub release URLs");
    }

    // ─── Security Parameter Thresholds ────────────────────────────────────────────

    /// <summary>
    /// OWASP Password Storage Cheat Sheet (2024) recommends a minimum of 600,000 iterations
    /// of PBKDF2-SHA256. Falling below this threshold weakens the brute-force resistance of
    /// exported backup files.
    /// Reference: https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html
    /// </summary>
    [Fact]
    public void Pbkdf2Iterations_ShouldMeetOwaspMinimumRecommendation()
    {
        // Assert
        AppConstants.Pbkdf2Iterations.Should().BeGreaterThanOrEqualTo(600_000,
            "OWASP recommends a minimum of 600,000 PBKDF2-SHA256 iterations for password-based key derivation");
    }

    /// <summary>
    /// MinPasswordLength is a UI-enforced guard. If it drops below 10, the application would
    /// allow trivially weak passwords to protect encrypted backup files.
    /// </summary>
    [Fact]
    public void MinPasswordLength_ShouldBeAtLeastTenCharacters()
    {
        // Assert
        AppConstants.MinPasswordLength.Should().BeGreaterThanOrEqualTo(10,
            "short passwords dramatically reduce AES-GCM backup security; minimum is 10 characters");
    }

    // ─── GitHub Metadata ──────────────────────────────────────────────────────────

    /// <summary>
    /// UpdateService and RcloneBootstrapper build GitHub API/release URLs from these constants.
    /// Empty values produce invalid URLs that silently disable update checking and Rclone installation.
    /// </summary>
    [Fact]
    public void GitHubOwnerAndRepo_ShouldNotBeNullOrEmpty()
    {
        // Assert
        AppConstants.GitHubOwner.Should().NotBeNullOrWhiteSpace(
            "GitHubOwner is used to construct GitHub API URLs; an empty value breaks update checking");
        AppConstants.GitHubRepo.Should().NotBeNullOrWhiteSpace(
            "GitHubRepo is used to construct GitHub API URLs; an empty value breaks update checking");
    }
}

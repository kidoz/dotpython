using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace DotPython.PackageCompatibilityTests;

public sealed class AnyverQualificationEvidenceTests
{
    [Fact]
    public void CheckedInEvidence_RecordsEveryCollectedCaseWithoutPromotingSkips()
    {
        var nativeRoot = Path.Combine(FindRepositoryRoot(), "native", "dotpython-abi3");
        var compatibilityPath = Path.Combine(nativeRoot, "anyver-compatibility.json");
        var evidencePath = Path.Combine(nativeRoot, "anyver-upstream-qualification.json");
        using var compatibility = JsonDocument.Parse(File.ReadAllBytes(compatibilityPath));
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var compatibilityRoot = compatibility.RootElement;
        var evidenceRoot = evidence.RootElement;

        Assert.Equal(2, compatibilityRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, evidenceRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("partial", compatibilityRoot.GetProperty("qualificationStatus").GetString());
        Assert.False(compatibilityRoot.GetProperty("supportsCpythonAbi").GetBoolean());
        AssertIdentityMatches(compatibilityRoot, evidenceRoot, "package");
        AssertIdentityMatches(compatibilityRoot, evidenceRoot, "packageVersion");
        AssertIdentityMatches(compatibilityRoot, evidenceRoot, "wheel");
        AssertIdentityMatches(compatibilityRoot, evidenceRoot, "wheelSha256");
        AssertIdentityMatches(compatibilityRoot, evidenceRoot, "sourceRevision");

        var suite = compatibilityRoot.GetProperty("upstreamPythonSuite");
        Assert.Equal(
            "anyver-upstream-qualification.json",
            suite.GetProperty("evidenceFile").GetString()
        );
        Assert.Equal(
            suite.GetProperty("evidenceSha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(evidencePath)))
        );
        Assert.Equal(
            suite.GetProperty("sourceTestFile").GetString(),
            evidenceRoot.GetProperty("sourceTestFile").GetString()
        );
        Assert.Equal(
            suite.GetProperty("sourceTestFileSha256").GetString(),
            evidenceRoot.GetProperty("sourceTestFileSha256").GetString()
        );

        var execution = evidenceRoot.GetProperty("execution");
        Assert.Equal("worker-process", execution.GetProperty("isolation").GetString());
        Assert.False(execution.GetProperty("sourceModified").GetBoolean());
        Assert.Equal(1, execution.GetProperty("suiteAdmissionAttempts").GetInt32());
        Assert.Equal(0, execution.GetProperty("attemptedCases").GetInt32());
        var blockerIds = execution
            .GetProperty("blockers")
            .EnumerateArray()
            .Select(blocker => blocker.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("managed-suite-admission", blockerIds);
        Assert.DoesNotContain("managed-parser-assert-statement", blockerIds);
        Assert.DoesNotContain("managed-parser-with-statement", blockerIds);
        Assert.Contains(
            blockerIds,
            blocker =>
                blocker is not null
                && blocker.StartsWith("managed-dpy3004", StringComparison.Ordinal)
        );
        Assert.All(
            execution.GetProperty("blockers").EnumerateArray(),
            blocker => Assert.True(blocker.GetProperty("occurrences").GetInt32() >= 1)
        );

        var cases = evidenceRoot.GetProperty("cases").EnumerateArray().ToArray();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var testCase in cases)
        {
            var nodeId = Assert.IsType<string>(testCase.GetProperty("nodeId").GetString());
            Assert.StartsWith("tests/test_anyver.py::", nodeId, StringComparison.Ordinal);
            Assert.True(nodeIds.Add(nodeId), $"Duplicate upstream node ID '{nodeId}'.");
            switch (testCase.GetProperty("outcome").GetString())
            {
                case "passed":
                    passed++;
                    break;
                case "failed":
                    failed++;
                    break;
                case "skipped":
                    skipped++;
                    var blocker = Assert.IsType<string>(
                        testCase.GetProperty("blocker").GetString()
                    );
                    Assert.Contains(blocker, blockerIds);
                    break;
                default:
                    Assert.Fail($"Unknown qualification outcome for '{nodeId}'.");
                    break;
            }
        }

        var summary = evidenceRoot.GetProperty("summary");
        AssertCounts(summary, cases.Length, passed, failed, skipped);
        AssertCounts(suite, cases.Length, passed, failed, skipped, collectedName: "collectedCases");
        Assert.Equal(325, cases.Length);
        Assert.Equal(0, passed);
        Assert.Equal(0, failed);
        Assert.Equal(325, skipped);
    }

    private static void AssertIdentityMatches(
        JsonElement compatibility,
        JsonElement evidence,
        string propertyName
    ) =>
        Assert.Equal(
            compatibility.GetProperty(propertyName).GetString(),
            evidence.GetProperty(propertyName).GetString()
        );

    private static void AssertCounts(
        JsonElement element,
        int collected,
        int passed,
        int failed,
        int skipped,
        string collectedName = "collected"
    )
    {
        Assert.Equal(collected, element.GetProperty(collectedName).GetInt32());
        Assert.Equal(passed, element.GetProperty("passed").GetInt32());
        Assert.Equal(failed, element.GetProperty("failed").GetInt32());
        Assert.Equal(skipped, element.GetProperty("skipped").GetInt32());
        Assert.Equal(collected, checked(passed + failed + skipped));
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotPython.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The DotPython repository root could not be located.");
    }
}

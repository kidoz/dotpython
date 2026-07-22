// CA1308: blocker identifiers are lowercase slugs by schema convention, not comparison keys.
#pragma warning disable CA1308

using System.Text.RegularExpressions;
using DotPython.Worker;

namespace DotPython.WorkerTests;

/// <summary>
/// Executes a pinned upstream pytest suite against a worker session and records
/// evidence-grade outcomes: admission blockers when the unmodified module is rejected,
/// and per-case attempts when it is admitted.
/// </summary>
internal static partial class QualificationSuiteRunner
{
    internal const string SuiteAdmissionBlockerId = "managed-suite-admission";
    internal const string ParametrizeBlockerId = "pytest-parametrize-not-attempted";

    internal sealed record QualificationRunResult(
        bool SuiteAdmitted,
        int AttemptedCases,
        IReadOnlyList<AnyverQualificationBlocker> Blockers,
        IReadOnlyList<AnyverQualificationCase> Cases
    );

    internal static async Task<QualificationRunResult> RunAsync(
        WorkerSession session,
        string moduleSource,
        string fileName,
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken,
        bool attemptParametrized = false
    )
    {
        var admission = await session
            .ExecuteAsync(moduleSource, fileName: fileName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Success)
        {
            // DPY2006/DPY2007 are parser-recovery artifacts of a rejected construct, not
            // independent blockers; drop them unless nothing else was reported.
            var rootDiagnostics = admission
                .Diagnostics.Where(diagnostic => diagnostic.Code is not ("DPY2006" or "DPY2007"))
                .ToList();
            if (rootDiagnostics.Count == 0)
            {
                rootDiagnostics = [.. admission.Diagnostics];
            }

            var rootCauses = rootDiagnostics
                .GroupBy(diagnostic => (diagnostic.Code, diagnostic.Message))
                .Select(group => new
                {
                    group.Key.Code,
                    group.Key.Message,
                    Occurrences = group.Count(),
                })
                .OrderByDescending(group => group.Occurrences)
                .ThenBy(group => group.Message, StringComparer.Ordinal)
                .Select(
                    (group, index) =>
                        new AnyverQualificationBlocker
                        {
                            Id = DeriveBlockerId(group.Code, group.Message, index),
                            DiagnosticCode = group.Code,
                            Message = group.Message,
                            Occurrences = group.Occurrences,
                        }
                )
                .ToList();
            var blockers = new List<AnyverQualificationBlocker>
            {
                new()
                {
                    Id = SuiteAdmissionBlockerId,
                    DiagnosticCode = rootCauses[0].DiagnosticCode,
                    Message =
                        "The unmodified upstream module was rejected before any test case "
                        + "could be attempted.",
                    Occurrences = 1,
                },
            };
            blockers.AddRange(rootCauses);
            var skippedCases = nodeIds
                .Select(nodeId => new AnyverQualificationCase
                {
                    NodeId = nodeId,
                    Outcome = "skipped",
                    Blocker = SuiteAdmissionBlockerId,
                })
                .ToList();
            return new QualificationRunResult(false, 0, blockers, skippedCases);
        }

        var attempted = 0;
        var sawParametrized = false;
        var parameterIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var cases = new List<AnyverQualificationCase>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            string? snippet;
            var parameterStart = nodeId.IndexOf('[', StringComparison.Ordinal);
            if (parameterStart >= 0)
            {
                if (!attemptParametrized)
                {
                    sawParametrized = true;
                    cases.Add(
                        new AnyverQualificationCase
                        {
                            NodeId = nodeId,
                            Outcome = "skipped",
                            Blocker = ParametrizeBlockerId,
                        }
                    );
                    continue;
                }

                var bareNodeId = nodeId[..parameterStart];
                var parameterSegments = bareNodeId.Split(
                    "::",
                    StringSplitOptions.RemoveEmptyEntries
                );
                var index = parameterIndexes.GetValueOrDefault(bareNodeId);
                parameterIndexes[bareNodeId] = index + 1;
                snippet = parameterSegments.Length switch
                {
                    3 => $"import pytest\npytest._run_case({parameterSegments[1]}()."
                        + $"{parameterSegments[2]}, '{parameterSegments[2]}', {index})",
                    2 => $"import pytest\npytest._run_case({parameterSegments[1]}, "
                        + $"'{parameterSegments[1]}', {index})",
                    _ => null,
                };
                if (snippet is null)
                {
                    cases.Add(
                        new AnyverQualificationCase
                        {
                            NodeId = nodeId,
                            Outcome = "skipped",
                            Blocker = SuiteAdmissionBlockerId,
                        }
                    );
                    continue;
                }

                attempted++;
                var parametrizedAttempt = await session
                    .ExecuteAsync(
                        snippet,
                        fileName: $"<{nodeId}>",
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                cases.Add(
                    new AnyverQualificationCase
                    {
                        NodeId = nodeId,
                        Outcome = parametrizedAttempt.Success ? "passed" : "failed",
                        Detail = parametrizedAttempt.Success
                            ? null
                            : string.Join(
                                "; ",
                                parametrizedAttempt.Diagnostics.Select(diagnostic =>
                                    $"{diagnostic.Code}: {diagnostic.Message}"
                                )
                            ),
                    }
                );
                continue;
            }

            var segments = nodeId.Split("::", StringSplitOptions.RemoveEmptyEntries);
            snippet = segments.Length switch
            {
                3 => $"{segments[1]}().{segments[2]}()",
                2 => $"{segments[1]}()",
                _ => null,
            };
            if (snippet is null)
            {
                cases.Add(
                    new AnyverQualificationCase
                    {
                        NodeId = nodeId,
                        Outcome = "skipped",
                        Blocker = SuiteAdmissionBlockerId,
                    }
                );
                continue;
            }

            attempted++;
            var attempt = await session
                .ExecuteAsync(
                    snippet,
                    fileName: $"<{nodeId}>",
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            cases.Add(
                new AnyverQualificationCase
                {
                    NodeId = nodeId,
                    Outcome = attempt.Success ? "passed" : "failed",
                    Detail = attempt.Success
                        ? null
                        : string.Join(
                            "; ",
                            attempt.Diagnostics.Select(diagnostic =>
                                $"{diagnostic.Code}: {diagnostic.Message}"
                            )
                        ),
                }
            );
        }

        var attemptBlockers = new List<AnyverQualificationBlocker>();
        if (sawParametrized)
        {
            attemptBlockers.Add(
                new AnyverQualificationBlocker
                {
                    Id = ParametrizeBlockerId,
                    DiagnosticCode = "DPY0000",
                    Message =
                        "Parametrized upstream cases require pytest collection semantics and "
                        + "were not attempted individually.",
                    Occurrences = cases.Count(item => item.Blocker == ParametrizeBlockerId),
                }
            );
        }

        return new QualificationRunResult(true, attempted, attemptBlockers, cases);
    }

    private static string DeriveBlockerId(string code, string message, int index)
    {
        var statement = StatementPattern().Match(message);
        if (statement.Success)
        {
            return $"managed-parser-{statement.Groups[1].Value}-statement";
        }

        var slug = SlugPattern().Replace(message.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        return $"managed-{code.ToLowerInvariant()}-{index}-{slug}";
    }

    [GeneratedRegex("^The '(\\w+)' statement is not supported")]
    private static partial Regex StatementPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}

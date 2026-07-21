using DotPython.Protocol;
using Xunit;

namespace DotPython.WorkerTests;

public sealed class WorkerHandshakeNegotiatorTests
{
    [Fact]
    public void Validate_AcceptsMatchingIdentityAndNegotiatedLimits()
    {
        var selected = WorkerHandshakeNegotiator.Validate(CreateRequest(), CreateResponse());

        Assert.Equal(WorkerProtocolVersion.Current, selected);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("runtime")]
    [InlineData("architecture")]
    [InlineData("environment")]
    [InlineData("feature")]
    [InlineData("version")]
    [InlineData("limits")]
    public void Validate_RejectsProtocolSkew(string field)
    {
        var request = CreateRequest();
        var response = CreateResponse();
        response = field switch
        {
            "provider" => response with
            {
                Identity = response.Identity with { ProviderId = "unexpected" },
            },
            "runtime" => response with
            {
                Identity = response.Identity with { RuntimeId = "unexpected" },
            },
            "architecture" => response with
            {
                Identity = response.Identity with { Architecture = "unexpected" },
            },
            "environment" => response with
            {
                Identity = response.Identity with { EnvironmentHash = "unexpected" },
            },
            "feature" => response with { Features = [] },
            "version" => response with { SelectedVersion = new WorkerProtocolVersion(3, 0) },
            "limits" => response with { Limits = response.Limits with { MaxOutputBytes = 2048 } },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            WorkerHandshakeNegotiator.Validate(request, response)
        );

        Assert.Equal(WorkerProtocolFaultCodes.HandshakeFailed, exception.Fault.Code);
        Assert.Equal(WorkerFaultPhase.Handshake, exception.Fault.Phase);
    }

    private static WorkerHandshakeRequest CreateRequest() =>
        new(
            "1.0.0",
            WorkerProtocolVersion.Current,
            WorkerProtocolVersion.Current,
            "dotpython-managed-abi3",
            "dotpython-managed",
            "arm64",
            "sha256:test",
            ["managed-execution"],
            new WorkerProtocolLimits(4096, 1024, 1, 4)
        );

    private static WorkerHandshakeResponse CreateResponse() =>
        new(
            WorkerProtocolVersion.Current,
            "1.0.0",
            new WorkerIdentity(
                "dotpython-managed-abi3",
                "1.0.0",
                "dotpython-managed",
                "3.14",
                "arm64",
                "sha256:test",
                Guid.NewGuid(),
                1,
                ["managed-execution"]
            ),
            new WorkerProtocolLimits(4096, 1024, 1, 4),
            ["managed-execution"]
        );
}

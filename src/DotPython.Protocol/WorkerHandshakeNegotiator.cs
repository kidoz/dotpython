namespace DotPython.Protocol;

public static class WorkerHandshakeNegotiator
{
    public static WorkerProtocolVersion Validate(
        WorkerHandshakeRequest request,
        WorkerHandshakeResponse response
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ValidateLimits(request.Limits, nameof(request));
        ValidateLimits(response.Limits, nameof(response));

        if (
            request.MinimumVersion.Major != request.MaximumVersion.Major
            || response.SelectedVersion.Major != request.MaximumVersion.Major
            || response.SelectedVersion.CompareTo(request.MinimumVersion) < 0
            || response.SelectedVersion.CompareTo(request.MaximumVersion) > 0
        )
        {
            throw Failed(
                $"Worker protocol {response.SelectedVersion} is outside the supported range "
                    + $"{request.MinimumVersion} through {request.MaximumVersion}."
            );
        }

        var identity = response.Identity;
        RequireEqual(request.ExpectedProviderId, identity.ProviderId, "provider");
        RequireEqual(request.ExpectedRuntimeId, identity.RuntimeId, "runtime");
        RequireEqual(request.ExpectedArchitecture, identity.Architecture, "architecture");
        RequireEqual(request.ExpectedEnvironmentHash, identity.EnvironmentHash, "environment");
        if (identity.WorkerId == Guid.Empty || identity.Generation <= 0)
        {
            throw Failed("The worker identity or generation is invalid.");
        }

        var features = response.Features.ToHashSet(StringComparer.Ordinal);
        foreach (var required in request.RequiredFeatures)
        {
            if (!features.Contains(required))
            {
                throw Failed($"The worker does not provide required feature '{required}'.");
            }
        }

        if (
            response.Limits.MaxMessageBytes > request.Limits.MaxMessageBytes
            || response.Limits.MaxOutputBytes > request.Limits.MaxOutputBytes
            || response.Limits.MaxConcurrentRequests > request.Limits.MaxConcurrentRequests
            || response.Limits.MaxSessions > request.Limits.MaxSessions
        )
        {
            throw Failed("The worker selected limits outside the host policy.");
        }

        return response.SelectedVersion;
    }

    public static WorkerProtocolLimits SelectLimits(
        WorkerProtocolLimits host,
        WorkerProtocolLimits worker
    )
    {
        ValidateLimits(host, nameof(host));
        ValidateLimits(worker, nameof(worker));
        return new WorkerProtocolLimits(
            Math.Min(host.MaxMessageBytes, worker.MaxMessageBytes),
            Math.Min(host.MaxOutputBytes, worker.MaxOutputBytes),
            Math.Min(host.MaxConcurrentRequests, worker.MaxConcurrentRequests),
            Math.Min(host.MaxSessions, worker.MaxSessions)
        );
    }

    private static void ValidateLimits(WorkerProtocolLimits limits, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(limits, parameterName);
        if (
            limits.MaxMessageBytes <= 0
            || limits.MaxOutputBytes <= 0
            || limits.MaxConcurrentRequests <= 0
            || limits.MaxSessions <= 0
        )
        {
            throw new ArgumentOutOfRangeException(parameterName, "Worker limits must be positive.");
        }
    }

    private static void RequireEqual(string expected, string actual, string field)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw Failed(
                $"Worker {field} identity '{actual}' did not match expected '{expected}'."
            );
        }
    }

    private static WorkerProtocolException Failed(string message) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.HandshakeFailed,
                WorkerFaultPhase.Handshake,
                message,
                false
            )
        );
}

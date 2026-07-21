using System.Text.Json;

namespace DotPython.Protocol;

public static class WorkerProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = WorkerProtocolJsonContext
        .Default
        .Options;

    public static WorkerEnvelope CreateEnvelope<T>(
        WorkerMessageType messageType,
        Guid correlationId,
        DateTimeOffset? deadlineUtc,
        T payload,
        WorkerProtocolVersion? version = null
    )
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new WorkerEnvelope(
            version ?? WorkerProtocolVersion.Current,
            messageType,
            correlationId,
            deadlineUtc,
            JsonSerializer.SerializeToElement(payload, Options)
        );
    }

    public static byte[] Serialize(WorkerEnvelope envelope)
    {
        ValidateEnvelope(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    public static WorkerEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(payload, Options);
            if (envelope is null)
            {
                throw Malformed("The worker message contained JSON null.");
            }

            ValidateEnvelope(envelope);
            return envelope;
        }
        catch (JsonException exception)
        {
            throw Malformed("The worker message was not valid protocol JSON.", exception);
        }
    }

    public static T ReadPayload<T>(WorkerEnvelope envelope)
        where T : notnull
    {
        ValidateEnvelope(envelope);
        try
        {
            return envelope.Payload.Deserialize<T>(Options)
                ?? throw Malformed($"The {envelope.MessageType} payload contained JSON null.");
        }
        catch (JsonException exception)
        {
            throw Malformed(
                $"The {envelope.MessageType} payload did not match its protocol contract.",
                exception
            );
        }
    }

    private static void ValidateEnvelope(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Version.Major <= 0 || envelope.Version.Minor < 0)
        {
            throw Malformed("The worker protocol version is invalid.");
        }

        if (envelope.MessageType == WorkerMessageType.None || !Enum.IsDefined(envelope.MessageType))
        {
            throw Malformed("The worker message type is unknown.");
        }

        if (envelope.CorrelationId == Guid.Empty)
        {
            throw Malformed("The worker correlation ID cannot be empty.");
        }

        if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw Malformed("The worker message payload is required.");
        }
    }

    private static WorkerProtocolException Malformed(string message, Exception? inner = null)
    {
        return new WorkerProtocolException(
            new WorkerFault(
                WorkerProtocolFaultCodes.HandshakeFailed,
                WorkerFaultPhase.Framing,
                message,
                false
            ),
            inner
        );
    }
}

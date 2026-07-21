using System.Buffers.Binary;

namespace DotPython.Protocol;

public sealed class WorkerFrameCodec
{
    private const int HeaderSize = sizeof(int);
    private readonly int _maxMessageBytes;

    public WorkerFrameCodec(int maxMessageBytes = WorkerProtocolLimits.DefaultMaxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        _maxMessageBytes = maxMessageBytes;
    }

    public async ValueTask WriteAsync(
        Stream stream,
        WorkerEnvelope envelope,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = WorkerProtocolSerializer.Serialize(envelope);
        if (payload.Length > _maxMessageBytes)
        {
            throw LimitExceeded(payload.Length);
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkerEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        var headerBytes = await ReadAtMostAsync(stream, header, cancellationToken)
            .ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != HeaderSize)
        {
            throw Malformed("The worker frame ended before its length prefix was complete.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0)
        {
            throw Malformed("The worker frame length must be positive.");
        }

        if (length > _maxMessageBytes)
        {
            throw LimitExceeded(length);
        }

        var payload = new byte[length];
        var payloadBytes = await ReadAtMostAsync(stream, payload, cancellationToken)
            .ConfigureAwait(false);
        if (payloadBytes != length)
        {
            throw Malformed("The worker frame ended before its declared payload was complete.");
        }

        return WorkerProtocolSerializer.Deserialize(payload);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static WorkerProtocolException LimitExceeded(int length) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.LimitExceeded,
                WorkerFaultPhase.Framing,
                $"Worker message length {length} bytes exceeds the configured limit.",
                false
            )
        );

    private static WorkerProtocolException Malformed(string message) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.HandshakeFailed,
                WorkerFaultPhase.Framing,
                message,
                false
            )
        );
}

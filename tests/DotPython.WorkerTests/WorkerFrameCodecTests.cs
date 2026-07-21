using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotPython.Protocol;
using Xunit;

namespace DotPython.WorkerTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
public sealed class WorkerFrameCodecTests
{
    [Fact]
    public async Task RoundTrip_PreservesVersionedEnvelopeAcrossPartialReads()
    {
        var expected = WorkerProtocolSerializer.CreateEnvelope(
            WorkerMessageType.CancelRequest,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            new WorkerCancelRequest(Guid.NewGuid())
        );
        await using var encoded = new MemoryStream();
        var codec = new WorkerFrameCodec();
        await codec.WriteAsync(encoded, expected, TestContext.Current.CancellationToken);
        encoded.Position = 0;
        await using var partial = new PartialReadStream(encoded, 1);

        var actual = Assert.IsType<WorkerEnvelope>(
            await codec.ReadAsync(partial, TestContext.Current.CancellationToken)
        );

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.MessageType, actual.MessageType);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(
            WorkerProtocolSerializer.ReadPayload<WorkerCancelRequest>(expected),
            WorkerProtocolSerializer.ReadPayload<WorkerCancelRequest>(actual)
        );
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullOnlyForCleanEndOfStream()
    {
        await using var stream = new MemoryStream();

        var envelope = await new WorkerFrameCodec().ReadAsync(
            stream,
            TestContext.Current.CancellationToken
        );

        Assert.Null(envelope);
    }

    [Theory]
    [InlineData(2, "length prefix")]
    [InlineData(8, "declared payload")]
    public async Task ReadAsync_RejectsTruncatedFrames(int availableBytes, string expectedMessage)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 8);
        await using var stream = new MemoryStream(bytes[..availableBytes]);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await new WorkerFrameCodec().ReadAsync(stream, TestContext.Current.CancellationToken)
        );

        Assert.Equal(WorkerProtocolFaultCodes.HandshakeFailed, exception.Fault.Code);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedFrameBeforeAllocatingPayload()
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 65);
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await new WorkerFrameCodec(64).ReadAsync(stream, TestContext.Current.CancellationToken)
        );

        Assert.Equal(WorkerProtocolFaultCodes.LimitExceeded, exception.Fault.Code);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedJsonAndUnknownMessageTypes()
    {
        var malformed = Encoding.UTF8.GetBytes("not-json");
        await using var malformedStream = Frame(malformed);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await new WorkerFrameCodec().ReadAsync(
                malformedStream,
                TestContext.Current.CancellationToken
            )
        );

        var unknown = Encoding.UTF8.GetBytes(
            "{\"version\":{\"major\":1,\"minor\":0},\"messageType\":999,"
                + "\"correlationId\":\"b64d7456-30d9-4c16-b358-15809ea42644\","
                + "\"deadlineUtc\":null,\"payload\":{}}"
        );
        await using var unknownStream = Frame(unknown);

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await new WorkerFrameCodec().ReadAsync(
                unknownStream,
                TestContext.Current.CancellationToken
            )
        );
    }

    private static MemoryStream Frame(byte[] payload)
    {
        var bytes = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, sizeof(int));
        return new MemoryStream(bytes);
    }

    private sealed class PartialReadStream(Stream inner, int maximumRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumRead));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

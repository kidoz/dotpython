using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Compiler.Artifacts;

public static class DotPythonModuleArtifactSerializer
{
    private const int ChecksumLength = 32;
    private const int MaximumArtifactLength = 64 * 1024 * 1024;
    private const int MaximumCollectionLength = 1_000_000;
    private const int MaximumManifestLength = 1024 * 1024;
    private const int MaximumNestingDepth = 128;
    private const int MaximumStringLength = 1024 * 1024;
    private static readonly byte[] Magic = "DPYMOD\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Serialize(DotPythonModuleArtifact artifact)
    {
        using var destination = new MemoryStream();
        Serialize(destination, artifact);
        return destination.ToArray();
    }

    public static void Serialize(Stream destination, DotPythonModuleArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The artifact destination must be writable.",
                nameof(destination)
            );
        }

        var manifestBytes = DotPythonModuleManifestJson.SerializeToUtf8Bytes(artifact.Manifest);
        if (manifestBytes.Length > MaximumManifestLength)
        {
            throw new InvalidOperationException("The module manifest is too large.");
        }

        using var payloadStream = new MemoryStream();
        var writer = new ArtifactWriter(payloadStream);
        writer.WriteByteArray(manifestBytes);
        WriteCodeObject(writer, artifact.Code, 0);
        if (payloadStream.Length > MaximumArtifactLength)
        {
            throw new InvalidOperationException("The module artifact is too large.");
        }

        var payload = payloadStream.ToArray();
        var checksum = SHA256.HashData(payload);
        destination.Write(Magic);
        WriteUInt16(destination, DotPythonModuleArtifactFormat.CurrentVersion);
        WriteInt32(destination, payload.Length);
        destination.Write(payload);
        destination.Write(checksum);
    }

    public static DotPythonModuleArtifact Deserialize(ReadOnlySpan<byte> artifact) =>
        Deserialize(new MemoryStream(artifact.ToArray(), writable: false));

    public static DotPythonModuleArtifact Deserialize(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The artifact source must be readable.", nameof(source));
        }

        try
        {
            Span<byte> magic = stackalloc byte[Magic.Length];
            source.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic))
            {
                throw new InvalidDataException("The DotPython module artifact magic is invalid.");
            }

            var formatVersion = ReadUInt16(source);
            if (formatVersion != DotPythonModuleArtifactFormat.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Module artifact format {formatVersion} is not supported."
                );
            }

            var payloadLength = ReadInt32(source);
            if (payloadLength < 0 || payloadLength > MaximumArtifactLength)
            {
                throw new InvalidDataException("The module artifact payload length is invalid.");
            }

            var payload = new byte[payloadLength];
            source.ReadExactly(payload);
            Span<byte> expectedChecksum = stackalloc byte[ChecksumLength];
            source.ReadExactly(expectedChecksum);
            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException("The module artifact has trailing data.");
            }

            var actualChecksum = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
            {
                throw new InvalidDataException("The module artifact checksum is invalid.");
            }

            var reader = new ArtifactReader(payload);
            var manifestBytes = reader.ReadByteArray(MaximumManifestLength, "manifest");
            var manifest = DotPythonModuleManifestJson.Deserialize(manifestBytes);
            var code = ReadCodeObject(ref reader, 0);
            reader.EnsureEnd();
            return new DotPythonModuleArtifact(manifest, code);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The module artifact is truncated.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The module artifact contains invalid UTF-8.",
                exception
            );
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The module artifact is invalid.", exception);
        }
    }

    private static void WriteCodeObject(ArtifactWriter writer, PythonCodeObject code, int depth)
    {
        if (depth > MaximumNestingDepth)
        {
            throw new InvalidOperationException("The code-object nesting depth is too large.");
        }

        ValidateCodeObject(code);
        writer.WriteInt32(code.FormatVersion);
        writer.WriteString(code.Name);
        writer.WriteInt32(code.ArgumentCount);
        writer.WriteStrings(code.Names);
        writer.WriteStrings(code.VariableNames);
        writer.WriteStrings(code.CellVariableNames);
        writer.WriteStrings(code.FreeVariableNames);
        writer.WriteInt32(code.Constants.Count);
        foreach (var constant in code.Constants)
        {
            WriteConstant(writer, constant, depth);
        }

        writer.WriteInt32(code.Instructions.Count);
        foreach (var instruction in code.Instructions)
        {
            writer.WriteUInt16((int)instruction.OpCode);
            writer.WriteInt32(instruction.Operand);
            writer.WriteInt32(instruction.Span.Start);
            writer.WriteInt32(instruction.Span.Length);
        }
    }

    private static PythonCodeObject ReadCodeObject(ref ArtifactReader reader, int depth)
    {
        if (depth > MaximumNestingDepth)
        {
            throw new InvalidDataException("The code-object nesting depth is too large.");
        }

        var formatVersion = reader.ReadInt32();
        if (formatVersion != DotPythonBytecodeFormat.CurrentVersion)
        {
            throw new InvalidDataException($"Bytecode format {formatVersion} is not supported.");
        }

        var name = reader.ReadString();
        var argumentCount = reader.ReadNonNegativeInt32("argument count");
        var names = reader.ReadStrings();
        var variableNames = reader.ReadStrings();
        var cellVariableNames = reader.ReadStrings();
        var freeVariableNames = reader.ReadStrings();
        var constantCount = reader.ReadCount("constant");
        var constants = new List<PythonConstant>(constantCount);
        for (var index = 0; index < constantCount; index++)
        {
            constants.Add(ReadConstant(ref reader, depth));
        }

        var instructionCount = reader.ReadCount("instruction");
        var instructions = new List<PythonInstruction>(instructionCount);
        for (var index = 0; index < instructionCount; index++)
        {
            var opCodeValue = reader.ReadUInt16();
            if (!Enum.IsDefined((PythonOpCode)opCodeValue))
            {
                throw new InvalidDataException($"Bytecode operation {opCodeValue} is invalid.");
            }

            var operand = reader.ReadInt32();
            var spanStart = reader.ReadNonNegativeInt32("source span start");
            var spanLength = reader.ReadNonNegativeInt32("source span length");
            instructions.Add(
                new PythonInstruction(
                    (PythonOpCode)opCodeValue,
                    operand,
                    new TextSpan(spanStart, spanLength)
                )
            );
        }

        var code = new PythonCodeObject(
            name,
            instructions,
            constants,
            names,
            variableNames,
            cellVariableNames,
            freeVariableNames,
            argumentCount
        );
        ValidateCodeObject(code);
        return code;
    }

    private static void WriteConstant(ArtifactWriter writer, PythonConstant constant, int depth)
    {
        writer.WriteByte((byte)constant.Type);
        switch (constant.Type)
        {
            case PythonConstantType.NoneValue:
                break;
            case PythonConstantType.TruthValue:
                writer.WriteByte((bool)constant.Value! ? (byte)1 : (byte)0);
                break;
            case PythonConstantType.WholeNumber:
                writer.WriteByteArray(
                    ((BigInteger)constant.Value!).ToByteArray(isUnsigned: false, isBigEndian: true)
                );
                break;
            case PythonConstantType.FloatingPoint:
                writer.WriteDouble((double)constant.Value!);
                break;
            case PythonConstantType.ComplexNumber:
                var complex = (Complex)constant.Value!;
                writer.WriteDouble(complex.Real);
                writer.WriteDouble(complex.Imaginary);
                break;
            case PythonConstantType.TextValue:
                writer.WriteString((string)constant.Value!);
                break;
            case PythonConstantType.ByteSequence:
                writer.WriteByteArray((byte[])constant.Value!);
                break;
            case PythonConstantType.CodeObject:
                WriteCodeObject(writer, (PythonCodeObject)constant.Value!, depth + 1);
                break;
            default:
                throw new InvalidOperationException(
                    $"Constant type '{constant.Type}' is not supported."
                );
        }
    }

    private static PythonConstant ReadConstant(ref ArtifactReader reader, int depth)
    {
        var typeValue = reader.ReadByte();
        if (!Enum.IsDefined((PythonConstantType)typeValue))
        {
            throw new InvalidDataException($"Constant type {typeValue} is invalid.");
        }

        var type = (PythonConstantType)typeValue;
        object? value = type switch
        {
            PythonConstantType.NoneValue => null,
            PythonConstantType.TruthValue => ReadTruthValue(ref reader),
            PythonConstantType.WholeNumber => new BigInteger(
                reader.ReadByteArray(MaximumStringLength, "integer"),
                isUnsigned: false,
                isBigEndian: true
            ),
            PythonConstantType.FloatingPoint => reader.ReadDouble(),
            PythonConstantType.ComplexNumber => new Complex(
                reader.ReadDouble(),
                reader.ReadDouble()
            ),
            PythonConstantType.TextValue => reader.ReadString(),
            PythonConstantType.ByteSequence => reader.ReadByteArray(
                MaximumArtifactLength,
                "byte sequence"
            ),
            PythonConstantType.CodeObject => ReadCodeObject(ref reader, depth + 1),
            _ => throw new InvalidDataException($"Constant type {typeValue} is invalid."),
        };
        return new PythonConstant(type, value);
    }

    private static bool ReadTruthValue(ref ArtifactReader reader) =>
        reader.ReadByte() switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException($"Truth value {value} is invalid."),
        };

    private static void ValidateCodeObject(PythonCodeObject code)
    {
        if (code.FormatVersion != DotPythonBytecodeFormat.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Bytecode format {code.FormatVersion} is not supported."
            );
        }

        if (
            code.Names.Count > MaximumCollectionLength
            || code.VariableNames.Count > MaximumCollectionLength
            || code.CellVariableNames.Count > MaximumCollectionLength
            || code.FreeVariableNames.Count > MaximumCollectionLength
            || code.Constants.Count > MaximumCollectionLength
            || code.Instructions.Count > MaximumCollectionLength
        )
        {
            throw new InvalidDataException("A code-object collection is too large.");
        }

        ValidateClosureMetadata(code);

        for (var index = 0; index < code.Instructions.Count; index++)
        {
            var instruction = code.Instructions[index];
            if (!Enum.IsDefined(instruction.OpCode))
            {
                throw new InvalidDataException($"Instruction {index} has an invalid operation.");
            }

            if (instruction.Span.Start < 0 || instruction.Span.Length < 0)
            {
                throw new InvalidDataException($"Instruction {index} has an invalid source span.");
            }

            ValidateOperand(code, instruction, index);
        }
    }

    private static void ValidateClosureMetadata(PythonCodeObject code)
    {
        var localNames = new HashSet<string>(code.VariableNames, StringComparer.Ordinal);
        if (localNames.Count != code.VariableNames.Count)
        {
            throw new InvalidDataException("A code object contains duplicate local names.");
        }

        var closureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in code.CellVariableNames)
        {
            if (!localNames.Contains(name))
            {
                throw new InvalidDataException(
                    $"Cell variable '{name}' is not a local variable of the code object."
                );
            }

            if (!closureNames.Add(name))
            {
                throw new InvalidDataException("A code object contains duplicate closure names.");
            }
        }

        foreach (var name in code.FreeVariableNames)
        {
            if (!closureNames.Add(name))
            {
                throw new InvalidDataException("A code object contains duplicate closure names.");
            }
        }
    }

    private static void ValidateOperand(
        PythonCodeObject code,
        PythonInstruction instruction,
        int instructionIndex
    )
    {
        switch (instruction.OpCode)
        {
            case PythonOpCode.LoadConstant:
                ValidateIndex(instruction.Operand, code.Constants.Count, instructionIndex);
                if (code.Constants[instruction.Operand].Type == PythonConstantType.CodeObject)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} loads code as a value."
                    );
                }

                break;
            case PythonOpCode.MakeFunction:
            case PythonOpCode.MakeFunctionWithDefaults:
            case PythonOpCode.MakeClass:
                ValidateIndex(instruction.Operand, code.Constants.Count, instructionIndex);
                if (code.Constants[instruction.Operand].Type != PythonConstantType.CodeObject)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} does not reference a code object."
                    );
                }

                break;
            case PythonOpCode.LoadName:
            case PythonOpCode.StoreName:
            case PythonOpCode.DeleteName:
            case PythonOpCode.ImportName:
            case PythonOpCode.LoadAttribute:
            case PythonOpCode.StoreAttribute:
            case PythonOpCode.DeleteAttribute:
            case PythonOpCode.ImportFrom:
                ValidateIndex(instruction.Operand, code.Names.Count, instructionIndex);
                break;
            case PythonOpCode.LoadLocal:
            case PythonOpCode.StoreLocal:
            case PythonOpCode.DeleteLocal:
            case PythonOpCode.ReturnLocal:
            case PythonOpCode.CallLocal:
                ValidateIndex(instruction.Operand, code.VariableNames.Count, instructionIndex);
                break;
            case PythonOpCode.LoadCell:
            case PythonOpCode.StoreCell:
            case PythonOpCode.DeleteCell:
                ValidateIndex(
                    instruction.Operand,
                    code.CellVariableNames.Count + code.FreeVariableNames.Count,
                    instructionIndex
                );
                break;
            case PythonOpCode.Jump:
            case PythonOpCode.JumpIfFalse:
            case PythonOpCode.JumpIfFalseOrPop:
            case PythonOpCode.JumpIfTrueOrPop:
            case PythonOpCode.ForIter:
                if (instruction.Operand < 0 || instruction.Operand > code.Instructions.Count)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an invalid jump target."
                    );
                }

                break;
            case PythonOpCode.SetupExcept:
            case PythonOpCode.SetupFinally:
                if (instruction.Operand < 0 || instruction.Operand >= code.Instructions.Count)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an invalid exception-handler target."
                    );
                }

                break;
            case PythonOpCode.Call:
            case PythonOpCode.CallKeyword:
            case PythonOpCode.UnpackSequence:
            case PythonOpCode.ListAppend:
            case PythonOpCode.DictionaryAdd:
                if (instruction.Operand < 0 || instruction.Operand > MaximumCollectionLength)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an invalid argument count."
                    );
                }

                break;
            case PythonOpCode.BuildList:
            case PythonOpCode.BuildTuple:
            case PythonOpCode.BuildDictionary:
            case PythonOpCode.BuildSet:
                if (instruction.Operand < 0 || instruction.Operand > MaximumCollectionLength)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an invalid element count."
                    );
                }

                break;
            case PythonOpCode.Raise:
                if (instruction.Operand is < 0 or > 2)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an invalid raise argument count."
                    );
                }

                break;
            default:
                if (instruction.Operand != 0)
                {
                    throw new InvalidDataException(
                        $"Instruction {instructionIndex} has an unexpected operand."
                    );
                }

                break;
        }
    }

    private static void ValidateIndex(int value, int count, int instructionIndex)
    {
        if ((uint)value >= (uint)count)
        {
            throw new InvalidDataException(
                $"Instruction {instructionIndex} has an out-of-range operand."
            );
        }
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private sealed class ArtifactWriter
    {
        private readonly Stream _stream;

        internal ArtifactWriter(Stream stream)
        {
            _stream = stream;
        }

        internal void WriteByte(byte value) => _stream.WriteByte(value);

        internal void WriteUInt16(int value) =>
            DotPythonModuleArtifactSerializer.WriteUInt16(_stream, value);

        internal void WriteInt32(int value) =>
            DotPythonModuleArtifactSerializer.WriteInt32(_stream, value);

        internal void WriteDouble(double value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            _stream.Write(bytes);
        }

        internal void WriteString(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length > MaximumStringLength)
            {
                throw new InvalidOperationException("An artifact string is too large.");
            }

            WriteByteArray(bytes);
        }

        internal void WriteStrings(IReadOnlyList<string> values)
        {
            if (values.Count > MaximumCollectionLength)
            {
                throw new InvalidOperationException("An artifact collection is too large.");
            }

            WriteInt32(values.Count);
            foreach (var value in values)
            {
                WriteString(value);
            }
        }

        internal void WriteByteArray(byte[] value)
        {
            WriteInt32(value.Length);
            _stream.Write(value);
        }
    }

    private ref struct ArtifactReader
    {
        private readonly ReadOnlySpan<byte> _artifact;
        private int _offset;

        internal ArtifactReader(ReadOnlySpan<byte> artifact)
        {
            _artifact = artifact;
        }

        internal byte ReadByte()
        {
            EnsureAvailable(sizeof(byte));
            return _artifact[_offset++];
        }

        internal ushort ReadUInt16()
        {
            EnsureAvailable(sizeof(ushort));
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_artifact[_offset..]);
            _offset += sizeof(ushort);
            return value;
        }

        internal int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(_artifact[_offset..]);
            _offset += sizeof(int);
            return value;
        }

        internal double ReadDouble()
        {
            EnsureAvailable(sizeof(long));
            var bits = BinaryPrimitives.ReadInt64LittleEndian(_artifact[_offset..]);
            _offset += sizeof(long);
            return BitConverter.Int64BitsToDouble(bits);
        }

        internal int ReadNonNegativeInt32(string description)
        {
            var value = ReadInt32();
            if (value < 0)
            {
                throw new InvalidDataException($"The artifact {description} is invalid.");
            }

            return value;
        }

        internal int ReadCount(string description)
        {
            var value = ReadNonNegativeInt32($"{description} count");
            if (value > MaximumCollectionLength)
            {
                throw new InvalidDataException($"The artifact {description} count is too large.");
            }

            return value;
        }

        internal string ReadString() =>
            StrictUtf8.GetString(ReadBytes(MaximumStringLength, "string"));

        internal List<string> ReadStrings()
        {
            var count = ReadCount("string");
            var values = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                values.Add(ReadString());
            }

            return values;
        }

        internal byte[] ReadByteArray(int maximumLength, string description) =>
            ReadBytes(maximumLength, description).ToArray();

        internal void EnsureEnd()
        {
            if (_offset != _artifact.Length)
            {
                throw new InvalidDataException("The module artifact payload has trailing data.");
            }
        }

        private ReadOnlySpan<byte> ReadBytes(int maximumLength, string description)
        {
            var length = ReadNonNegativeInt32($"{description} length");
            if (length > maximumLength)
            {
                throw new InvalidDataException($"The artifact {description} is too large.");
            }

            EnsureAvailable(length);
            var value = _artifact.Slice(_offset, length);
            _offset += length;
            return value;
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || length > _artifact.Length - _offset)
            {
                throw new EndOfStreamException();
            }
        }
    }
}

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The managed `random` module: CPython's Mersenne Twister behind the `Random` class and
/// the module functions, with the same algorithms and so the same streams. `SystemRandom`
/// draws from the operating system's own randomness instead.
/// </summary>
internal static class PythonRandomModule
{
    private const string Module = "random";
    private const string StateTypeName = "Random";

    /// <summary>`math.pi * 2`, which the module publishes as `TWOPI`.</summary>
    private const double TwoPi = Math.Tau;

    /// <summary>`4 * exp(-0.5) / sqrt(2)`, the ratio-of-uniforms normal constant.</summary>
    private static readonly double NvMagicConst = 4 * Math.Exp(-0.5) / Math.Sqrt(2.0);

    /// <summary>`log(4)`, `1 + log(4.5)` and `2**-53`, as `Lib/random.py` computes them.</summary>
    private static readonly double Log4 = Math.Log(4.0);
    private static readonly double SgMagicConst = 1.0 + Math.Log(4.5);
    private const double RecipBpf = 1.0 / (1L << 53);

    private const int Bpf = 53;

    /// <summary>The names the module publishes; `random.__all__` is exactly this list.</summary>
    private static readonly string[] PublicNames =
    [
        "Random",
        "SystemRandom",
        "betavariate",
        "binomialvariate",
        "choice",
        "choices",
        "expovariate",
        "gammavariate",
        "gauss",
        "getrandbits",
        "getstate",
        "lognormvariate",
        "normalvariate",
        "paretovariate",
        "randbytes",
        "randint",
        "random",
        "randrange",
        "sample",
        "seed",
        "setstate",
        "shuffle",
        "triangular",
        "uniform",
        "vonmisesvariate",
        "weibullvariate",
    ];

    private static readonly PythonManagedTypeValue RandomType = CreateRandomType();
    private static readonly PythonManagedTypeValue SystemRandomType = CreateSystemRandomType();

    /// <summary>The one instance the module's own functions are bound methods of.</summary>
    private static readonly PythonManagedObjectValue Instance = NewRandom(system: false);

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("Random", RandomType);
        globals.SetValue("SystemRandom", SystemRandomType);
        globals.SetValue("NV_MAGICCONST", new PythonFloatingPointValue(NvMagicConst));
        globals.SetValue("LOG4", new PythonFloatingPointValue(Log4));
        globals.SetValue("SG_MAGICCONST", new PythonFloatingPointValue(SgMagicConst));
        globals.SetValue("BPF", PythonWholeNumberValue.Create(Bpf));
        globals.SetValue("RECIP_BPF", new PythonFloatingPointValue(RecipBpf));
        globals.SetValue("TWOPI", new PythonFloatingPointValue(TwoPi));
        globals.SetValue("_inst", Instance);
        foreach (var name in PublicNames)
        {
            if (name is "Random" or "SystemRandom")
            {
                continue;
            }
            globals.SetValue(
                name,
                new PythonBoundMethodValue(
                    name,
                    Instance,
                    (PythonProtocolFunctionValue)Attribute(RandomType, name)
                )
            );
        }
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. PublicNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    // -----------------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------------

    /// <summary>The generator one `Random` carries, with the value `gauss` holds back.</summary>
    private sealed class RandomState
    {
        internal bool IsSystem { get; init; }

        internal PythonRandomGenerator Generator { get; } = new();

        /// <summary>The second value a `gauss` call computed, which the next one returns.</summary>
        internal double? GaussNext { get; set; }
    }

    private static PythonManagedObjectValue NewRandom(bool system)
    {
        var instance = new PythonManagedObjectValue(
            system ? SystemRandomType : RandomType,
            new RandomState { IsSystem = system }
        );
        if (!system)
        {
            State(instance).Generator.SeedFromSystem();
        }
        return instance;
    }

    private static RandomState State(PythonValue? value) =>
        value is PythonManagedObjectValue { Payload: RandomState state }
            ? state
            : throw Fault(
                $"unbound method Random.{ManagedObjectProtocols.GetTypeName(value ?? PythonNoneValue.Instance)}()"
                    + " must be called with a Random instance as first argument (got nothing instead)",
                default,
                "TypeError"
            );

    /// <summary>One tempered draw. A system generator draws from the operating system.</summary>
    private static uint Next(RandomState state) =>
        state.IsSystem ? SystemWord() : state.Generator.NextUInt32();

    private static uint SystemWord()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    // -----------------------------------------------------------------------------
    // The module's registered types
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateRandomType()
    {
        var type = new PythonManagedTypeValue(StateTypeName)
        {
            Module = Module,
            QualName = "Random",
        };
        type.Attributes["VERSION"] = PythonWholeNumberValue.Create(3);
        type.Attributes["__init__"] = Method(
            "Random",
            "__init__",
            Initialize,
            parameters: ["x"],
            defaults: [PythonNoneValue.Instance]
        );
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, _) => NewRandom(system: false),
            (_, _, _, _) => NewRandom(system: false)
        );
        type.Attributes["seed"] = Method(
            "Random",
            "seed",
            Seed,
            parameters: ["a", "version"],
            defaults: [PythonNoneValue.Instance, PythonWholeNumberValue.Create(2)]
        );
        type.Attributes["getstate"] = Method("Random", "getstate", GetState);
        type.Attributes["setstate"] = Method(
            "Random",
            "setstate",
            (receiver, arguments) => SetState(receiver, arguments, version: 3)
        );
        type.Attributes["random"] = Method("Random", "random", RandomDraw);
        type.Attributes["getrandbits"] = Method(
            "Random",
            "getrandbits",
            GetRandBits,
            parameters: ["k"]
        );
        type.Attributes["randbytes"] = Method("Random", "randbytes", RandBytes, parameters: ["n"]);
        type.Attributes["randrange"] = Method(
            "Random",
            "randrange",
            RandRange,
            parameters: ["start", "stop", "step"],
            defaults: [null, PythonNoneValue.Instance, PythonWholeNumberValue.Create(1)]
        );
        type.Attributes["randint"] = Method("Random", "randint", RandInt, parameters: ["a", "b"]);
        type.Attributes["choice"] = Method("Random", "choice", Choice, parameters: ["seq"]);
        type.Attributes["shuffle"] = Method("Random", "shuffle", Shuffle, parameters: ["x"]);
        type.Attributes["sample"] = Method(
            "Random",
            "sample",
            Sample,
            parameters: ["population", "k", "counts"],
            defaults: [null, null, PythonNoneValue.Instance]
        );
        type.Attributes["choices"] = Method(
            "Random",
            "choices",
            Choices,
            parameters: ["population", "weights", "cum_weights", "k"],
            defaults:
            [
                null,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                PythonWholeNumberValue.Create(1),
            ]
        );
        type.Attributes["uniform"] = Method("Random", "uniform", Uniform, parameters: ["a", "b"]);
        type.Attributes["triangular"] = Method(
            "Random",
            "triangular",
            Triangular,
            parameters: ["low", "high", "mode"],
            defaults:
            [
                new PythonFloatingPointValue(0.0),
                new PythonFloatingPointValue(1.0),
                PythonNoneValue.Instance,
            ]
        );
        type.Attributes["normalvariate"] = Method(
            "Random",
            "normalvariate",
            (receiver, arguments) => NormalVariate(receiver, arguments, gaussCache: false),
            parameters: ["mu", "sigma"],
            defaults: [new PythonFloatingPointValue(0.0), new PythonFloatingPointValue(1.0)]
        );
        type.Attributes["gauss"] = Method(
            "Random",
            "gauss",
            (receiver, arguments) => NormalVariate(receiver, arguments, gaussCache: true),
            parameters: ["mu", "sigma"],
            defaults: [new PythonFloatingPointValue(0.0), new PythonFloatingPointValue(1.0)]
        );
        type.Attributes["lognormvariate"] = Method(
            "Random",
            "lognormvariate",
            LogNormVariate,
            parameters: ["mu", "sigma"]
        );
        type.Attributes["expovariate"] = Method(
            "Random",
            "expovariate",
            ExposVariate,
            parameters: ["lambd"],
            defaults: [new PythonFloatingPointValue(1.0)]
        );
        type.Attributes["vonmisesvariate"] = Method(
            "Random",
            "vonmisesvariate",
            VonMisesVariate,
            parameters: ["mu", "kappa"]
        );
        type.Attributes["gammavariate"] = Method(
            "Random",
            "gammavariate",
            GammaVariate,
            parameters: ["alpha", "beta"]
        );
        type.Attributes["betavariate"] = Method(
            "Random",
            "betavariate",
            BetaVariate,
            parameters: ["alpha", "beta"]
        );
        type.Attributes["paretovariate"] = Method(
            "Random",
            "paretovariate",
            ParetoVariate,
            parameters: ["alpha"]
        );
        type.Attributes["weibullvariate"] = Method(
            "Random",
            "weibullvariate",
            WeibullVariate,
            parameters: ["alpha", "beta"]
        );
        type.Attributes["binomialvariate"] = Method(
            "Random",
            "binomialvariate",
            BinomialVariate,
            parameters: ["n", "p"],
            defaults: [PythonWholeNumberValue.Create(1), new PythonFloatingPointValue(0.5)]
        );
        type.Attributes["__getstate__"] = Method("Random", "__getstate__", GetState);
        type.Attributes["__setstate__"] = Method(
            "Random",
            "__setstate__",
            (receiver, arguments) => SetState(receiver, arguments, version: 3)
        );
        type.Attributes["__reduce__"] = Method("Random", "__reduce__", Reduce);
        type.Attributes["gauss_next"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "gauss_next",
                (_, arguments) => ReadGaussNext(arguments[0])
            ),
            new PythonProtocolFunctionValue(
                "gauss_next",
                (_, arguments) => WriteGaussNext(arguments[0], arguments[1])
            ),
            new PythonProtocolFunctionValue(
                "gauss_next",
                (_, arguments) => WriteGaussNext(arguments[0], PythonNoneValue.Instance)
            )
        );
        return type;
    }

    /// <summary>
    /// `SystemRandom`: the same methods, over the operating system's randomness. Its
    /// state is not its own, so `seed` does nothing and the state pair refuses.
    /// </summary>
    private static PythonManagedTypeValue CreateSystemRandomType()
    {
        var type = new PythonManagedTypeValue("SystemRandom")
        {
            Module = Module,
            QualName = "SystemRandom",
        };
        type.Attributes["__module__"] = new PythonTextValue(Module);
        type.Attributes["VERSION"] = PythonWholeNumberValue.Create(3);
        type.Attributes["__init__"] = Method(
            "SystemRandom",
            "__init__",
            (_, _) => PythonNoneValue.Instance
        );
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, _) => NewRandom(system: true),
            (_, _, _, _) => NewRandom(system: true)
        );
        type.Attributes["random"] = Method(
            "SystemRandom",
            "random",
            (_, _) => new PythonFloatingPointValue(SystemDouble())
        );
        type.Attributes["getrandbits"] = Method(
            "SystemRandom",
            "getrandbits",
            SystemGetRandBits,
            parameters: ["k"]
        );
        type.Attributes["randbytes"] = Method(
            "SystemRandom",
            "randbytes",
            SystemRandBytes,
            parameters: ["n"]
        );
        type.Attributes["seed"] = Method(
            "SystemRandom",
            "seed",
            (_, _) => PythonNoneValue.Instance
        );
        var refuse = new PythonProtocolFunctionValue(
            "_notimplemented",
            (_, _) =>
                throw Fault(
                    "System entropy source does not have state.",
                    default,
                    "NotImplementedError"
                )
        );
        type.Attributes["getstate"] = refuse;
        type.Attributes["setstate"] = refuse;
        type.Attributes["_notimplemented"] = refuse;
        type.SetDeclaredBases(new PythonTupleValue([RandomType]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, RandomType, PythonBuiltinFunctions.Object])
        );
        return type;
    }

    /// <summary>One `random()` from the operating system: 53 bits of entropy.</summary>
    private static double SystemDouble()
    {
        Span<byte> bytes = stackalloc byte[7];
        RandomNumberGenerator.Fill(bytes);
        var value = 0L;
        for (var index = 0; index < 7; index++)
        {
            value |= (long)bytes[index] << (8 * index);
        }
        return (value >> 3) * RecipBpf;
    }

    private static PythonValue Attribute(PythonManagedTypeValue type, string name) =>
        type.Attributes[name];

    // -----------------------------------------------------------------------------
    // Methods
    // -----------------------------------------------------------------------------

    private static PythonValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var self = receiver!;
        var state = State(self);
        SeedInto(
            self,
            state,
            arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance,
            2,
            default
        );
        if (state.IsSystem)
        {
            // A system generator has no state of its own to seed.
            state.Generator.SeedFromSystem();
        }
        state.GaussNext = null;
        return PythonNoneValue.Instance;
    }

    private static PythonValue Seed(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var self = receiver!;
        var state = State(self);
        var a = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
        var version =
            arguments.Count > 1 && arguments[1] is PythonWholeNumberValue whole
                ? (int)whole.Value
                : 2;
        SeedInto(self, state, a, version, default);
        state.GaussNext = null;
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `seed(a, version)`: what Python accepts, converted the way Python converts it —
    /// a string or bytes seed is hashed into a number, a float becomes its own hash, and
    /// `None` draws from the system.
    /// </summary>
    private static void SeedInto(
        PythonValue self,
        RandomState state,
        PythonValue a,
        int version,
        TextSpan span
    )
    {
        if (state.IsSystem)
        {
            return;
        }
        if (version == 1 && a is PythonTextValue or PythonByteSequenceValue or PythonByteArrayValue)
        {
            state.Generator.SeedNumber(Version1Seed(a));
            return;
        }
        if (version == 2 && a is PythonTextValue or PythonByteSequenceValue or PythonByteArrayValue)
        {
            state.Generator.SeedNumber(Version2Seed(a));
            return;
        }
        switch (a)
        {
            case PythonNoneValue:
                state.Generator.SeedFromSystem();
                return;
            case PythonTruthValue truth:
                state.Generator.SeedNumber(truth.Value ? BigInteger.One : BigInteger.Zero);
                return;
            case PythonWholeNumberValue whole:
                state.Generator.SeedNumber(whole.Value);
                return;
            case PythonFloatingPointValue floatingPoint:
                // A float is seeded by its own hash, cast to an unsigned word count.
                state.Generator.SeedNumber(
                    new BigInteger((ulong)PythonNumericHash.Float(a, floatingPoint.Value))
                );
                return;
        }
        _ = self;
        throw Fault(
            "The only supported seed types are:\n" + "None, int, float, str, bytes, and bytearray.",
            span,
            "TypeError"
        );
    }

    /// <summary>The version 1 string seed: the classic 64-bit polynomial over the text.</summary>
    private static BigInteger Version1Seed(PythonValue value)
    {
        // Version 1 reads the text's own code points: a `bytes` seed is decoded latin-1
        // first, which leaves every byte a code point of its own.
        var text = TextOf(value);
        var x = text.Length > 0 ? (ulong)text[0] << 7 : 0UL;
        foreach (var character in text)
        {
            x = (1000003UL * x) ^ character;
        }
        x ^= (ulong)text.Length;
        return x == ulong.MaxValue ? -2 : x;
    }

    /// <summary>The version 2 seed: the text beside its own SHA-512 digest, as one number.</summary>
    private static BigInteger Version2Seed(PythonValue value)
    {
        var bytes = BytesOf(value);
        var digest = SHA512.HashData(bytes);
        var combined = new byte[bytes.Length + digest.Length];
        bytes.CopyTo(combined, 0);
        digest.CopyTo(combined, bytes.Length);
        // `int.from_bytes(...)` reads the text as a big-endian number.
        return new BigInteger(combined, isUnsigned: true, isBigEndian: true);
    }

    private static string TextOf(PythonValue value) =>
        value switch
        {
            PythonTextValue text => text.Value,
            PythonByteSequenceValue bytes => new string([.. bytes.Value.Select(one => (char)one)]),
            PythonByteArrayValue array => new string([.. array.Value.Select(one => (char)one)]),
            _ => string.Empty,
        };

    private static byte[] BytesOf(PythonValue value) =>
        value switch
        {
            PythonTextValue text => Encoding.UTF8.GetBytes(text.Value),
            PythonByteSequenceValue bytes => [.. bytes.Value],
            PythonByteArrayValue array => [.. array.Value],
            _ => [],
        };

    private static PythonValue GetState(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        RequireNoArguments("Random", "getstate", arguments);
        var state = State(receiver!);
        var words = new List<PythonValue>();
        foreach (var word in state.Generator.Words)
        {
            words.Add(PythonWholeNumberValue.Create(word));
        }
        words.Add(PythonWholeNumberValue.Create(state.Generator.Index));
        return new PythonTupleValue([
            PythonWholeNumberValue.Create(3),
            new PythonTupleValue([.. words]),
            state.GaussNext is { } next
                ? new PythonFloatingPointValue(next)
                : PythonNoneValue.Instance,
        ]);
    }

    private static PythonNoneValue SetState(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        int version
    )
    {
        var state = State(receiver!);
        if (arguments[0] is not PythonTupleValue { Elements.Length: 3 } outer)
        {
            throw Fault("state must be a 3-tuple", default, "ValueError");
        }
        var outerVersion = outer.Elements[0] switch
        {
            PythonWholeNumberValue whole => (int)whole.Value,
            _ => -1,
        };
        var internalState = outer.Elements[1];
        var gaussNext = outer.Elements[2];
        if (outerVersion is not (2 or 3))
        {
            throw Fault(
                $"state with version {outerVersion} passed to Random.setstate() of version {version}",
                default,
                "ValueError"
            );
        }
        if (internalState is not PythonTupleValue { Elements.Length: 625 } internalTuple)
        {
            throw Fault("state vector must be a tuple of 625 elements", default, "ValueError");
        }
        var words = new uint[624];
        for (var index = 0; index < 624; index++)
        {
            var element = internalTuple.Elements[index];
            if (element is not PythonWholeNumberValue whole)
            {
                throw Fault("state vector must contain integers only", default, "ValueError");
            }
            var value = whole.Value;
            if (outerVersion == 2 && value.Sign < 0)
            {
                // A version 2 state was stored as signed words.
                value += ((BigInteger)1) << 32;
            }
            if (value.Sign < 0 || value > uint.MaxValue)
            {
                throw Fault("invalid state information", default, "TypeError");
            }
            words[index] = (uint)value;
        }
        var position = internalTuple.Elements[624];
        if (position is not PythonWholeNumberValue positionValue)
        {
            throw Fault("state vector must contain integers only", default, "ValueError");
        }
        if (positionValue.Value.Sign < 0 || positionValue.Value > 624)
        {
            throw Fault("invalid state information", default, "TypeError");
        }
        state.Generator.Restore(words, (int)positionValue.Value);
        state.GaussNext = gaussNext is PythonFloatingPointValue floating ? floating.Value : null;
        return PythonNoneValue.Instance;
    }

    private static PythonValue Reduce(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        RequireNoArguments("Random", "__reduce__", arguments);
        var state = State(receiver!);
        return new PythonTupleValue([
            state.IsSystem ? SystemRandomType : RandomType,
            new PythonTupleValue([]),
            GetState(receiver, []),
        ]);
    }

    private static PythonValue ReadGaussNext(PythonValue self) =>
        State(self).GaussNext is { } value
            ? new PythonFloatingPointValue(value)
            : PythonNoneValue.Instance;

    private static PythonNoneValue WriteGaussNext(PythonValue self, PythonValue value)
    {
        State(self).GaussNext = value is PythonFloatingPointValue floating ? floating.Value : null;
        return PythonNoneValue.Instance;
    }

    /// <summary>`random()`: 27 bits and 26 bits, as one 53-bit fraction.</summary>
    private static PythonValue RandomDraw(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        RequireNoArguments("Random", "random", arguments);
        var state = State(receiver!);
        var a = Next(state) >> 5;
        var b = Next(state) >> 6;
        return new PythonFloatingPointValue((a * 67108864.0 + b) / 9007199254740992.0);
    }

    private static PythonValue GetRandBits(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var k = (long)RequireIndex(arguments[0], default);
        return PythonWholeNumberValue.Create(RandBits(state, k));
    }

    private static BigInteger RandBits(RandomState state, long k)
    {
        if (k < 0)
        {
            throw Fault("Cannot convert negative int", default, "ValueError");
        }
        if (k == 0)
        {
            // Nothing is asked for, so nothing is drawn.
            return BigInteger.Zero;
        }
        var words = (int)((k - 1) / 32 + 1);
        var result = BigInteger.Zero;
        for (var word = 0; word < words; word++, k -= 32)
        {
            var value = Next(state);
            if (k < 32)
            {
                value >>= (int)(32 - k);
            }
            result |= new BigInteger(value) << (32 * word);
        }
        return result;
    }

    private static PythonValue SystemGetRandBits(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var k = (long)RequireIndex(arguments[0], default);
        if (k < 0)
        {
            throw Fault("number of bits must be non-negative", default, "ValueError");
        }
        var bytes = (int)((k + 7) / 8);
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        var value = new BigInteger(buffer, isUnsigned: true, isBigEndian: true);
        return PythonWholeNumberValue.Create(value >> (int)(bytes * 8 - k));
    }

    private static PythonValue RandBytes(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var n = (long)RequireIndex(arguments[0], default);
        if (n < 0)
        {
            throw Fault("number of bytes must be non-negative", default, "ValueError");
        }
        var value = RandBits(state, n * 8);
        var bytes = new byte[n];
        var written = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(written, bytes, Math.Min(bytes.Length, written.Length));
        return new PythonByteSequenceValue(bytes);
    }

    private static PythonValue SystemRandBytes(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var n = (long)RequireIndex(arguments[0], default);
        if (n < 0)
        {
            throw Fault("negative argument not allowed", default, "ValueError");
        }
        var bytes = new byte[n];
        RandomNumberGenerator.Fill(bytes);
        return new PythonByteSequenceValue(bytes);
    }

    /// <summary>`_randbelow(n)`: the smallest power-of-two draw that lands under `n`.</summary>
    private static BigInteger RandBelow(RandomState state, BigInteger n)
    {
        if (n.Sign <= 0 && !state.IsSystem)
        {
            return BigInteger.Zero;
        }
        var k = GetBitLength(n);
        var r = RandBits(state, k);
        while (r >= n)
        {
            r = RandBits(state, k);
        }
        return r;
    }

    private static long GetBitLength(BigInteger value) => value.GetBitLength();

    private static PythonValue RandRange(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var start = RequireIndex(arguments[0], default);
        var hasStop = arguments.Count > 1 && arguments[1] is not PythonNoneValue;
        if (!hasStop)
        {
            if (
                arguments.Count > 2
                && arguments[2] is PythonWholeNumberValue { Value: var step }
                && step != 1
            )
            {
                throw Fault("Missing a non-None stop argument", default, "TypeError");
            }
            if (start > 0)
            {
                return PythonWholeNumberValue.Create(RandBelow(state, start));
            }
            throw Fault("empty range for randrange()", default, "ValueError");
        }
        var stop = RequireIndex(arguments[1], default);
        var width = stop - start;
        var istep = arguments.Count > 2 ? RequireIndex(arguments[2], default) : BigInteger.One;
        if (istep == 1)
        {
            if (width > 0)
            {
                return PythonWholeNumberValue.Create(start + RandBelow(state, width));
            }
            throw Fault($"empty range in randrange({start}, {stop})", default, "ValueError");
        }
        BigInteger n;
        if (istep > 0)
        {
            n = (width + istep - 1) / istep;
        }
        else if (istep < 0)
        {
            n = (width + istep + 1) / istep;
        }
        else
        {
            throw Fault("zero step for randrange()", default, "ValueError");
        }
        if (n <= 0)
        {
            throw Fault("empty range for randrange()", default, "ValueError");
        }
        return PythonWholeNumberValue.Create(start + istep * RandBelow(state, n));
    }

    private static PythonValue RandInt(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var a = RequireIndex(arguments[0], default);
        var b = RequireIndex(arguments[1], default);
        if (b < a)
        {
            throw Fault($"empty range in randint({a}, {b})", default, "ValueError");
        }
        return PythonWholeNumberValue.Create(a + RandBelow(state, b - a + 1));
    }

    private static PythonValue Choice(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var sequence = arguments[0];
        var length = Length(sequence, default);
        if (length == 0)
        {
            throw Fault("Cannot choose from an empty sequence", default, "IndexError");
        }
        return ManagedObjectProtocols.GetItem(
            sequence,
            PythonWholeNumberValue.Create(RandBelow(state, length)),
            default
        );
    }

    private static PythonValue Shuffle(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var target = arguments[0];
        var length = Length(target, default);
        for (var index = length - 1; index > 0; index--)
        {
            var other = RandBelow(state, index + 1);
            var first = ManagedObjectProtocols.GetItem(
                target,
                PythonWholeNumberValue.Create(index),
                default
            );
            var second = ManagedObjectProtocols.GetItem(
                target,
                PythonWholeNumberValue.Create(other),
                default
            );
            ManagedObjectProtocols.SetItem(
                target,
                PythonWholeNumberValue.Create(index),
                second,
                default
            );
            ManagedObjectProtocols.SetItem(
                target,
                PythonWholeNumberValue.Create(other),
                first,
                default
            );
        }
        return PythonNoneValue.Instance;
    }

    private static PythonValue Sample(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var population = arguments[0];
        if (!IsSequence(population))
        {
            throw Fault(
                "Population must be a sequence.  For dicts or sets, use sorted(d).",
                default,
                "TypeError"
            );
        }
        var n = Length(population, default);
        var counts = arguments.Count > 2 ? arguments[2] : PythonNoneValue.Instance;
        if (counts is not PythonNoneValue)
        {
            return SampleWithCounts(state, population, arguments[1], counts, n);
        }
        var k = (int)RequireIndex(arguments[1], default);
        if (k < 0 || k > n)
        {
            throw Fault("Sample larger than population or is negative", default, "ValueError");
        }
        var result = new PythonValue[k];
        var setSize = 21;
        if (k > 5)
        {
            setSize += (int)Math.Pow(4, Math.Ceiling(Math.Log(k * 3) / Math.Log(4)));
        }
        if (n <= setSize)
        {
            var pool = new List<PythonValue>(n);
            for (var index = 0; index < n; index++)
            {
                pool.Add(
                    ManagedObjectProtocols.GetItem(
                        population,
                        PythonWholeNumberValue.Create(index),
                        default
                    )
                );
            }
            for (var index = 0; index < k; index++)
            {
                var j = (int)RandBelow(state, n - index);
                result[index] = pool[j];
                pool[j] = pool[n - index - 1];
            }
        }
        else
        {
            var selected = new HashSet<int>();
            for (var index = 0; index < k; index++)
            {
                var j = (int)RandBelow(state, n);
                while (!selected.Add(j))
                {
                    j = (int)RandBelow(state, n);
                }
                result[index] = ManagedObjectProtocols.GetItem(
                    population,
                    PythonWholeNumberValue.Create(j),
                    default
                );
            }
        }
        return new PythonListValue([.. result]);
    }

    private static PythonListValue SampleWithCounts(
        RandomState state,
        PythonValue population,
        PythonValue k,
        PythonValue counts,
        int n
    )
    {
        var elements = ManagedObjectProtocols.MaterializeValues(counts, default, null);
        if (elements.Count != n)
        {
            throw Fault(
                "The number of counts does not match the population",
                default,
                "ValueError"
            );
        }
        var cumulative = new List<BigInteger>(n);
        var running = BigInteger.Zero;
        foreach (var element in elements)
        {
            running += RequireIndex(element, default);
            cumulative.Add(running);
        }
        var total = cumulative.Count > 0 ? cumulative[^1] : BigInteger.Zero;
        if (total.Sign < 0)
        {
            throw Fault("Counts must be non-negative", default, "ValueError");
        }
        var selections = SampleRange(state, total, (int)RequireIndex(k, default));
        var result = new List<PythonValue>();
        foreach (var selection in selections)
        {
            var index = BisectRight(cumulative, selection);
            result.Add(
                ManagedObjectProtocols.GetItem(
                    population,
                    PythonWholeNumberValue.Create(index),
                    default
                )
            );
        }
        return new PythonListValue(result);
    }

    private static List<BigInteger> SampleRange(RandomState state, BigInteger total, int k)
    {
        var n = (int)total;
        if (k < 0 || k > n)
        {
            throw Fault("Sample larger than population or is negative", default, "ValueError");
        }
        var pool = new List<int>(n);
        for (var index = 0; index < n; index++)
        {
            pool.Add(index);
        }
        var result = new List<BigInteger>(k);
        for (var index = 0; index < k; index++)
        {
            var j = (int)RandBelow(state, n - index);
            result.Add(pool[j]);
            pool[j] = pool[n - index - 1];
        }
        return result;
    }

    private static int BisectRight(List<BigInteger> values, BigInteger target)
    {
        int low = 0,
            high = values.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (target < values[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    private static PythonValue Choices(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var population = arguments[0];
        var weights = arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance;
        var cumWeights = arguments.Count > 2 ? arguments[2] : PythonNoneValue.Instance;
        var k = arguments.Count > 3 ? (int)RequireIndex(arguments[3], default) : 1;
        var n = Length(population, default);
        if (weights is not PythonNoneValue && cumWeights is not PythonNoneValue)
        {
            throw Fault("Cannot specify both weights and cumulative weights", default, "TypeError");
        }
        var result = new List<PythonValue>(k);
        if (weights is PythonNoneValue && cumWeights is PythonNoneValue)
        {
            for (var index = 0; index < k; index++)
            {
                var pick = (int)Math.Floor(RandomValue(state) * n);
                result.Add(
                    ManagedObjectProtocols.GetItem(
                        population,
                        PythonWholeNumberValue.Create(pick),
                        default
                    )
                );
            }
            return new PythonListValue(result);
        }

        var cumulative = new List<double>();
        if (cumWeights is not PythonNoneValue)
        {
            foreach (
                var element in ManagedObjectProtocols.MaterializeValues(cumWeights, default, null)
            )
            {
                cumulative.Add(RequireNumber(element, default));
            }
        }
        else
        {
            var running = 0.0;
            foreach (
                var element in ManagedObjectProtocols.MaterializeValues(weights, default, null)
            )
            {
                running += RequireNumber(element, default);
                cumulative.Add(running);
            }
        }
        if (cumulative.Count != n)
        {
            throw Fault(
                "The number of weights does not match the population",
                default,
                "ValueError"
            );
        }
        var total = cumulative[^1] + 0.0;
        if (total <= 0.0)
        {
            throw Fault("Total of weights must be greater than zero", default, "ValueError");
        }
        if (double.IsInfinity(total))
        {
            throw Fault("Total of weights must be finite", default, "ValueError");
        }
        for (var index = 0; index < k; index++)
        {
            var pick = BisectRightDoubles(cumulative, RandomValue(state) * total, n - 1);
            result.Add(
                ManagedObjectProtocols.GetItem(
                    population,
                    PythonWholeNumberValue.Create(pick),
                    default
                )
            );
        }
        return new PythonListValue(result);
    }

    private static int BisectRightDoubles(List<double> values, double target, int high)
    {
        int low = 0;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (target < values[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    /// <summary>
    /// `uniform`: Python's own arithmetic over the two bounds, so a bound that is not a
    /// float is reported by the operation that refused it.
    /// </summary>
    private static PythonValue Uniform(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = State(receiver!);
        var a = arguments[0];
        var b = arguments[1];
        var difference = PythonVirtualMachine.ApplyBinaryOperator(
            PythonOpCode.BinarySubtract,
            b,
            a,
            default
        );
        var scaled = PythonVirtualMachine.ApplyBinaryOperator(
            PythonOpCode.BinaryMultiply,
            difference,
            new PythonFloatingPointValue(RandomValue(state)),
            default
        );
        return PythonVirtualMachine.ApplyBinaryOperator(PythonOpCode.BinaryAdd, a, scaled, default);
    }

    private static PythonValue Triangular(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var low = RequireNumber(arguments[0], default);
        var high = RequireNumber(arguments[1], default);
        var mode = arguments.Count > 2 ? arguments[2] : PythonNoneValue.Instance;
        var u = RandomValue(state);
        double c;
        if (mode is PythonNoneValue)
        {
            c = 0.5;
        }
        else
        {
            var modeValue = RequireNumber(mode, default);
            if (high == low)
            {
                return new PythonFloatingPointValue(low);
            }
            c = (modeValue - low) / (high - low);
        }
        if (u > c)
        {
            u = 1.0 - u;
            c = 1.0 - c;
            (low, high) = (high, low);
        }
        return new PythonFloatingPointValue(low + (high - low) * Math.Sqrt(u * c));
    }

    private static PythonFloatingPointValue NormalVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool gaussCache
    )
    {
        var state = State(receiver!);
        var mu = RequireNumber(arguments[0], default);
        var sigma = RequireNumber(arguments[1], default);
        if (gaussCache)
        {
            var cached = state.GaussNext;
            state.GaussNext = null;
            if (cached is { } value)
            {
                return new PythonFloatingPointValue(mu + value * sigma);
            }
            var x2Pi = RandomValue(state) * TwoPi;
            var g2Rad = Math.Sqrt(-2.0 * Math.Log(1.0 - RandomValue(state)));
            var z = Math.Cos(x2Pi) * g2Rad;
            state.GaussNext = Math.Sin(x2Pi) * g2Rad;
            return new PythonFloatingPointValue(mu + z * sigma);
        }
        while (true)
        {
            var u1 = RandomValue(state);
            var u2 = 1.0 - RandomValue(state);
            var ratio = NvMagicConst * (u1 - 0.5) / u2;
            var zz = ratio * ratio / 4.0;
            if (zz <= -Math.Log(u2))
            {
                return new PythonFloatingPointValue(mu + ratio * sigma);
            }
        }
    }

    private static PythonValue LogNormVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var mu = RequireNumber(arguments[0], default);
        var sigma = RequireNumber(arguments[1], default);
        while (true)
        {
            var u1 = RandomValue(state);
            var u2 = 1.0 - RandomValue(state);
            var ratio = NvMagicConst * (u1 - 0.5) / u2;
            var zz = ratio * ratio / 4.0;
            if (zz <= -Math.Log(u2))
            {
                return new PythonFloatingPointValue(Math.Exp(mu + ratio * sigma));
            }
        }
    }

    private static PythonValue ExposVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var lambda = RequireNumber(arguments[0], default);
        return new PythonFloatingPointValue(-Math.Log(1.0 - RandomValue(state)) / lambda);
    }

    private static PythonValue VonMisesVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var mu = RequireNumber(arguments[0], default);
        var kappa = RequireNumber(arguments[1], default);
        if (kappa <= 1e-6)
        {
            return new PythonFloatingPointValue(TwoPi * RandomValue(state));
        }
        var s = 0.5 / kappa;
        var r = s + Math.Sqrt(1.0 + s * s);
        double z;
        while (true)
        {
            var u1 = RandomValue(state);
            z = Math.Cos(Math.PI * u1);
            var d = z / (r + z);
            var u2 = RandomValue(state);
            if (u2 < 1.0 - d * d || u2 <= (1.0 - d) * Math.Exp(d))
            {
                break;
            }
        }
        var q = 1.0 / r;
        var f = (q + z) / (1.0 + q * z);
        var u3 = RandomValue(state);
        var theta = u3 > 0.5 ? mu + Math.Acos(f) : mu - Math.Acos(f);
        var result = theta % TwoPi;
        return new PythonFloatingPointValue(result < 0 ? result + TwoPi : result);
    }

    private static PythonValue GammaVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var alpha = RequireNumber(arguments[0], default);
        var beta = RequireNumber(arguments[1], default);
        if (alpha <= 0.0 || beta <= 0.0)
        {
            throw Fault("gammavariate: alpha and beta must be > 0.0", default, "ValueError");
        }
        if (alpha > 1.0)
        {
            var ainv = Math.Sqrt(2.0 * alpha - 1.0);
            var bbb = alpha - Log4;
            var ccc = alpha + ainv;
            while (true)
            {
                var u1 = RandomValue(state);
                if (u1 <= 1e-7 || u1 >= 0.9999999)
                {
                    continue;
                }
                var u2 = 1.0 - RandomValue(state);
                var v = Math.Log(u1 / (1.0 - u1)) / ainv;
                var x = alpha * Math.Exp(v);
                var z = u1 * u1 * u2;
                var r = bbb + ccc * v - x;
                if (r + SgMagicConst - 4.5 * z >= 0.0 || r >= Math.Log(z))
                {
                    return new PythonFloatingPointValue(x * beta);
                }
            }
        }
        if (alpha == 1.0)
        {
            return new PythonFloatingPointValue(-Math.Log(1.0 - RandomValue(state)) * beta);
        }
        var e = Math.E;
        while (true)
        {
            double x;
            var u = RandomValue(state);
            var b = (e + alpha) / e;
            var p = b * u;
            if (p <= 1.0)
            {
                x = Math.Pow(p, 1.0 / alpha);
            }
            else
            {
                x = -Math.Log((b - p) / alpha);
            }
            var u1 = RandomValue(state);
            if (p > 1.0)
            {
                if (u1 <= Math.Pow(x, alpha - 1.0))
                {
                    return new PythonFloatingPointValue(x * beta);
                }
            }
            else if (u1 <= Math.Exp(-x))
            {
                return new PythonFloatingPointValue(x * beta);
            }
        }
    }

    private static PythonValue BetaVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var alpha = RequireNumber(arguments[0], default);
        var beta = RequireNumber(arguments[1], default);
        var y = GammaValue(state, alpha, 1.0);
        if (y != 0.0)
        {
            return new PythonFloatingPointValue(y / (y + GammaValue(state, beta, 1.0)));
        }
        return new PythonFloatingPointValue(0.0);
    }

    /// <summary>`gammavariate`'s own draw, which `betavariate` reaches for.</summary>
    private static double GammaValue(RandomState state, double alpha, double beta)
    {
        if (alpha <= 0.0 || beta <= 0.0)
        {
            throw Fault("gammavariate: alpha and beta must be > 0.0", default, "ValueError");
        }
        if (alpha > 1.0)
        {
            var ainv = Math.Sqrt(2.0 * alpha - 1.0);
            var bbb = alpha - Log4;
            var ccc = alpha + ainv;
            while (true)
            {
                var u1 = RandomValue(state);
                if (u1 <= 1e-7 || u1 >= 0.9999999)
                {
                    continue;
                }
                var u2 = 1.0 - RandomValue(state);
                var v = Math.Log(u1 / (1.0 - u1)) / ainv;
                var x = alpha * Math.Exp(v);
                var z = u1 * u1 * u2;
                var r = bbb + ccc * v - x;
                if (r + SgMagicConst - 4.5 * z >= 0.0 || r >= Math.Log(z))
                {
                    return x * beta;
                }
            }
        }
        if (alpha == 1.0)
        {
            return -Math.Log(1.0 - RandomValue(state)) * beta;
        }
        var e = Math.E;
        while (true)
        {
            double x;
            var u = RandomValue(state);
            var b = (e + alpha) / e;
            var p = b * u;
            if (p <= 1.0)
            {
                x = Math.Pow(p, 1.0 / alpha);
            }
            else
            {
                x = -Math.Log((b - p) / alpha);
            }
            var u1 = RandomValue(state);
            if (p > 1.0)
            {
                if (u1 <= Math.Pow(x, alpha - 1.0))
                {
                    return x * beta;
                }
            }
            else if (u1 <= Math.Exp(-x))
            {
                return x * beta;
            }
        }
    }

    private static PythonValue ParetoVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var alpha = RequireNumber(arguments[0], default);
        var u = 1.0 - RandomValue(state);
        return new PythonFloatingPointValue(Math.Pow(u, -1.0 / alpha));
    }

    private static PythonValue WeibullVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var alpha = RequireNumber(arguments[0], default);
        var beta = RequireNumber(arguments[1], default);
        var u = 1.0 - RandomValue(state);
        return new PythonFloatingPointValue(alpha * Math.Pow(-Math.Log(u), 1.0 / beta));
    }

    private static PythonValue BinomialVariate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = State(receiver!);
        var n = (int)RequireIndex(arguments[0], default);
        var p = RequireNumber(arguments[1], default);
        if (n < 0)
        {
            throw Fault("n must be non-negative", default, "ValueError");
        }
        if (p <= 0.0 || p >= 1.0)
        {
            if (p == 0.0)
            {
                return PythonWholeNumberValue.Create(0);
            }
            if (p == 1.0)
            {
                return PythonWholeNumberValue.Create(n);
            }
            throw Fault("p must be in the range 0.0 <= p <= 1.0", default, "ValueError");
        }
        if (n == 1)
        {
            return PythonWholeNumberValue.Create(RandomValue(state) < p ? 1 : 0);
        }
        if (p > 0.5)
        {
            return PythonWholeNumberValue.Create(n - (int)BinomialValue(state, n, 1.0 - p));
        }
        return PythonWholeNumberValue.Create((int)BinomialValue(state, n, p));
    }

    private static double BinomialValue(RandomState state, long n, double p)
    {
        if (n * p < 10.0)
        {
            // The geometric method: the waiting time between successes.
            var x = 0L;
            var y = 0L;
            var c = Math.Log2(1.0 - p);
            if (c == 0)
            {
                return x;
            }
            while (true)
            {
                var draw = RandomValue(state);
                if (draw == 0.0)
                {
                    continue;
                }
                y += (long)Math.Floor(Math.Log2(draw) / c) + 1;
                if (y > n)
                {
                    return x;
                }
                x += 1;
            }
        }

        // The transformed rejection with squeeze method.
        var setup = false;
        var spq = Math.Sqrt(n * p * (1.0 - p));
        var b = 1.15 + 2.53 * spq;
        var a = -0.0873 + 0.0248 * b + 0.01 * p;
        var c0 = n * p + 0.5;
        var vr = 0.92 - 4.2 / b;
        double alpha = 0;
        double lpq = 0;
        double m = 0;
        double h = 0;
        while (true)
        {
            var u = RandomValue(state) - 0.5;
            var us = 0.5 - Math.Abs(u);
            if (us == 0)
            {
                continue;
            }
            var k = Math.Floor((2.0 * a / us + b) * u + c0);
            if (k < 0 || k > n)
            {
                continue;
            }
            var v = RandomValue(state);
            if (us >= 0.07 && v <= vr)
            {
                return k;
            }
            if (!setup)
            {
                alpha = (2.83 + 5.1 / b) * spq;
                lpq = Math.Log(p / (1.0 - p));
                m = Math.Floor((n + 1) * p);
                h = LogGamma(m + 1) + LogGamma(n - m + 1);
                setup = true;
            }
            v *= alpha / (a / (us * us) + b);
            if (Math.Log(v) <= h - LogGamma(k + 1) - LogGamma(n - k + 1) + (k - m) * lpq)
            {
                return k;
            }
        }
    }

    /// <summary>
    /// `math.lgamma`, which the binomial's rejection test measures with: the Lanczos
    /// approximation the C library's own `lgamma` agrees with to the last bit over the
    /// whole numbers the test reaches.
    /// </summary>
    private static double LogGamma(double value)
    {
        ReadOnlySpan<double> coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];
        if (value < 0.5)
        {
            // Reflection: lgamma(x) = log(pi / |sin(pi x)|) - lgamma(1 - x).
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * value))) - LogGamma(1.0 - value);
        }
        value -= 1.0;
        var series = 0.99999999999980993;
        for (var index = 0; index < coefficients.Length; index++)
        {
            series += coefficients[index] / (value + index + 1);
        }
        var t = value + coefficients.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (value + 0.5) * Math.Log(t) - t + Math.Log(series);
    }

    /// <summary>The next `random()` draw of a generator, as a double.</summary>
    private static double RandomValue(RandomState state) =>
        state.IsSystem ? SystemDouble() : GeneratorDouble(state.Generator);

    private static double GeneratorDouble(PythonRandomGenerator generator)
    {
        var a = generator.NextUInt32() >> 5;
        var b = generator.NextUInt32() >> 6;
        return (a * 67108864.0 + b) / 9007199254740992.0;
    }

    // -----------------------------------------------------------------------------
    // Values and members
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Whether a value is a `collections.abc.Sequence`: the kinds the runtime holds
    /// itself, and any object whose own class answers `__getitem__` and `__len__`.
    /// </summary>
    private static bool IsSequence(PythonValue value) =>
        value
            is PythonTextValue
                or PythonListValue
                or PythonTupleValue
                or PythonByteSequenceValue
                or PythonRangeValue
        || (
            value is PythonManagedObjectValue instance
            && UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__")
            && UserObjectProtocols.DefinesSpecialMethod(instance, "__len__")
        );

    /// <summary>
    /// `len(value)`, in Python's own words for a value that has none — which is what
    /// `shuffle` of a non-sequence reports.
    /// </summary>
    private static int Length(PythonValue value, TextSpan span)
    {
        try
        {
            return (int)ManagedObjectProtocols.GetLength(value, span);
        }
        catch (PythonRuntimeException fault) when (fault.PythonExceptionTypeName == "TypeError")
        {
            throw Fault(
                $"object of type '{ManagedObjectProtocols.GetTypeName(value)}' has no len()",
                span,
                "TypeError"
            );
        }
    }

    /// <summary>A call that names no parameters takes none, in Python's own words.</summary>
    private static void RequireNoArguments(
        string owner,
        string name,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count > 0)
        {
            throw Fault(
                $"{owner}.{name}() takes no arguments ({arguments.Count} given)",
                default,
                "TypeError"
            );
        }
    }

    private static BigInteger RequireIndex(PythonValue value, TextSpan span) =>
        PythonBuiltinFunctions.RequireIndex(value, span);

    private static double RequireNumber(PythonValue value, TextSpan span) =>
        PromoteTruth(value) switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floating => floating.Value,
            var other => throw Fault(
                $"must be real number, not {ManagedObjectProtocols.GetTypeName(other)}",
                span,
                "TypeError"
            ),
        };

    private static PythonValue PromoteTruth(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? 1 : 0)
            : value;

    /// <summary>
    /// A method of one of the module's types, called the way Python reaches it: with the
    /// receiver the call bound, and with the keywords the call named.
    /// </summary>
    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null,
        int positionalOnly = 0
    ) => PythonUserTypes.Method(owner, name, body, null, parameters, defaults, positionalOnly);

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4052", message, span, type);
}

using System.Security.Cryptography;
using DotPython.Contracts;

namespace DotPython;

/// <summary>Combines a static export contract with an opaque compiled module artifact.</summary>
public sealed class PythonModuleDefinition
{
    private readonly byte[] _artifact;

    /// <summary>Initializes a compiled module definition.</summary>
    public PythonModuleDefinition(PythonModuleContract contract, ReadOnlySpan<byte> artifact)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (artifact.IsEmpty)
        {
            throw new ArgumentException(
                "The compiled module artifact cannot be empty.",
                nameof(artifact)
            );
        }

        Contract = contract;
        _artifact = artifact.ToArray();
        ArtifactFingerprint = Convert.ToHexStringLower(SHA256.HashData(_artifact));
    }

    /// <summary>Gets the static module export contract.</summary>
    public PythonModuleContract Contract { get; }

    /// <summary>Gets a SHA-256 fingerprint of the compiled artifact.</summary>
    public string ArtifactFingerprint { get; }

    /// <summary>Gets the opaque compiled module artifact.</summary>
    public ReadOnlyMemory<byte> Artifact => _artifact;
}

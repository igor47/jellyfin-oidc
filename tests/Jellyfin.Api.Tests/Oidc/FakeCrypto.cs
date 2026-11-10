using System;
using MediaBrowser.Model.Cryptography;

namespace Jellyfin.Api.Tests.Oidc;

internal sealed class FakeCrypto : ICryptoProvider
{
    private int _counter;

    public string DefaultHashMethod => "PBKDF2-SHA512";

    // A distinct hash per call, so tests can tell a password was replaced.
    public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password)
        => new(DefaultHashMethod, BitConverter.GetBytes(++_counter), new byte[] { 1 });

    public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => false;

    public byte[] GenerateSalt() => new byte[] { 4 };

    public byte[] GenerateSalt(int length) => new byte[length];
}

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Clearspace.Services;

// Version 1: fixed authenticated header followed by a single AES-GCM message.
// Size is bounded so unauthenticated files cannot request unbounded allocations or KDF work.
internal static class FileLockFormat
{
    internal const int HeaderSize = 156;
    internal const int MaxFileSize = 64 * 1024 * 1024;
    internal const int Iterations = 600_000;
    internal static ReadOnlySpan<byte> Magic => "CSLOCK01"u8;

    internal static byte[] Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    // CHANGED (folder locking): the password overloads now wrap a one-use FileLockKeys, so a single
    // file still gets its own random salt exactly as before. Folder operations share one FileLockKeys
    // so the 600,000-iteration derivation runs once per folder instead of once per file.
    internal static (byte[] Header, byte[] Ciphertext) Encrypt(byte[] plaintext, ReadOnlySpan<char> password)
    {
        using var keys = new FileLockKeys(password);
        return Encrypt(plaintext, keys);
    }

    // NEW (folder locking): encrypt with a shared key cache. Every file still gets its own random data
    // key and nonces; only the password salt (and so the derived wrapping key) is shared.
    internal static (byte[] Header, byte[] Ciphertext) Encrypt(byte[] plaintext, FileLockKeys keys)
    {
        if (plaintext.Length > MaxFileSize) throw new IOException("File locking supports files up to 64 MB.");
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        Guid.NewGuid().TryWriteBytes(header.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), Iterations);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28, 8), plaintext.LongLength);
        keys.LockSalt.CopyTo(header.AsSpan(36, 32)); // CHANGED: password salt comes from the key cache
        RandomNumberGenerator.Fill(header.AsSpan(68, 12)); // wrapping nonce
        RandomNumberGenerator.Fill(header.AsSpan(128, 12)); // content nonce, independent key
        var dek = RandomNumberGenerator.GetBytes(32);
        var kek = keys.KeyFor(header.AsSpan(36, 32)); // CHANGED: owned (and later cleared) by the cache
        try
        {
            using (var wrapper = new AesGcm(kek, 16))
                wrapper.Encrypt(header.AsSpan(68, 12), dek, header.AsSpan(80, 32), header.AsSpan(112, 16), header.AsSpan(0, 68));
            var ciphertext = new byte[plaintext.Length];
            using var cipher = new AesGcm(dek, 16);
            cipher.Encrypt(header.AsSpan(128, 12), plaintext, ciphertext, header.AsSpan(140, 16), header.AsSpan(0, 140));
            return (header, ciphertext);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    internal static int Validate(byte[] header, long totalLength)
    {
        if (header.Length != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("This is not a supported Clearspace locked file.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(28, 8));
        if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4)) != Iterations ||
            length < 0 || length > MaxFileSize || totalLength != HeaderSize + length)
            throw new InvalidDataException("The locked file is incomplete, damaged, or uses an unsupported format.");
        return (int)length;
    }

    // NEW (reset-proof): true when these keys open this header (checks only the wrapped data key, so it's
    // quick apart from deriving the key for the header's salt). Used to accept a file's own older password.
    internal static bool Opens(byte[] header, FileLockKeys keys)
    {
        if (header.Length != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4)) != Iterations)
            return false;
        var dek = new byte[32];
        try
        {
            var kek = keys.KeyFor(header.AsSpan(36, 32));
            using var wrapper = new AesGcm(kek, 16);
            wrapper.Decrypt(header.AsSpan(68, 12), header.AsSpan(80, 32), header.AsSpan(112, 16), dek, header.AsSpan(0, 68));
            return true;
        }
        catch (CryptographicException) { return false; }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    internal static byte[] Decrypt(byte[] header, byte[] ciphertext, ReadOnlySpan<char> password)
    {
        using var keys = new FileLockKeys(password);
        return Decrypt(header, ciphertext, keys);
    }

    // NEW (folder locking): decrypt with a shared key cache, so files locked together (same salt)
    // derive the wrapping key only once.
    // CHANGED (switch to current password): tries `keys`, then its Fallback chain, so one run can open files
    // locked with an older password and files already on the current one.
    internal static byte[] Decrypt(byte[] header, byte[] ciphertext, FileLockKeys keys)
    {
        Validate(header, HeaderSize + ciphertext.LongLength);
        if (keys.Fallback is not null && !Opens(header, keys))
            for (var candidate = keys.Fallback; candidate is not null; candidate = candidate.Fallback)
                if (Opens(header, candidate)) return DecryptWith(header, ciphertext, candidate);
        return DecryptWith(header, ciphertext, keys);
    }

    private static byte[] DecryptWith(byte[] header, byte[] ciphertext, FileLockKeys keys)
    {
        var kek = keys.KeyFor(header.AsSpan(36, 32));
        var dek = new byte[32];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using (var wrapper = new AesGcm(kek, 16))
                wrapper.Decrypt(header.AsSpan(68, 12), header.AsSpan(80, 32), header.AsSpan(112, 16), dek, header.AsSpan(0, 68));
            using var cipher = new AesGcm(dek, 16);
            cipher.Decrypt(header.AsSpan(128, 12), ciphertext, header.AsSpan(140, 16), plaintext, header.AsSpan(0, 140));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("The password is incorrect or the locked file has been changed. The file was not unlocked.");
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }
}

// NEW (folder locking): holds a copy of the password and the keys derived from it for one lock or
// unlock operation. New locks made through one instance share a random salt; keys for other salts
// (files locked at other times) are derived on first use and reused. Everything is cleared on Dispose.
internal sealed class FileLockKeys : IDisposable
{
    private readonly char[] _password;
    private readonly Dictionary<string, byte[]> _derived = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private byte[]? _lockSalt;
    private bool _disposed;

    internal FileLockKeys(ReadOnlySpan<char> password) => _password = password.ToArray();

    // NEW (locked folders): keys rebuilt from a saved session key (salt + wrapping key) so an opened
    // folder can be locked again without the password. Such an instance can only lock with that salt;
    // it has no password to derive keys for any other salt.
    internal static FileLockKeys FromSessionKey(ReadOnlySpan<byte> sessionKey)
    {
        if (sessionKey.Length != 64) throw new InvalidDataException("The saved folder key is damaged.");
        var keys = new FileLockKeys(ReadOnlySpan<char>.Empty) { _fromSession = true };
        keys._lockSalt = sessionKey[..32].ToArray();
        keys._derived[Convert.ToHexString(sessionKey[..32])] = sessionKey[32..].ToArray();
        return keys;
    }

    // NEW (locked folders): salt + wrapping key for this instance's lock salt, for saving with DPAPI.
    // The caller must clear the returned array.
    internal byte[] ExportSessionKey()
    {
        var salt = LockSalt.ToArray();
        var key = KeyFor(salt);
        var result = new byte[64];
        salt.CopyTo(result, 0);
        key.CopyTo(result, 32);
        return result;
    }

    private bool _fromSession;

    // NEW (switch to current password): other keys to try when these don't open a file.
    internal FileLockKeys? Fallback { get; set; }

    internal ReadOnlySpan<byte> LockSalt
    {
        get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _lockSalt ??= RandomNumberGenerator.GetBytes(32); } }
    }

    internal byte[] KeyFor(ReadOnlySpan<byte> salt)
    {
        var id = Convert.ToHexString(salt);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_derived.TryGetValue(id, out var key))
            {
                // NEW (locked folders): a session-key instance has no password to derive other keys from.
                if (_fromSession) throw new CryptographicException("This file was locked with a different key than the one saved for this folder.");
                _derived[id] = key = FileLockFormat.Derive(_password, salt);
            }
            return key;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
            foreach (var key in _derived.Values) CryptographicOperations.ZeroMemory(key);
            _derived.Clear();
            if (_lockSalt is not null) CryptographicOperations.ZeroMemory(_lockSalt);
        }
    }
}

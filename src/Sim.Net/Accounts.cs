using System.Security.Cryptography;
using System.Text.Json;

namespace Sim.Net;

/// <summary>
/// Player accounts on a dedicated server, so a public server isn't "whoever types your name". Kept in its own
/// file beside the world save (never in the world, which every client receives).
///
/// The password never crosses the network. The server stores only a salted key, PBKDF2-SHA256(password, salt).
/// To join, it sends the salt and a fresh random nonce; the client derives the key and answers with
/// HMAC-SHA256(key, nonce). The first time a name joins, the client sends the derived key to register it (plain
/// TCP, so someone watching that very first join could learn the key; never the password itself).
/// Too many wrong answers lock a name for a while.
/// </summary>
public sealed class AccountBook
{
    public const int Iterations = 100_000;
    public const int KeyBytes = 32;
    public const int SaltBytes = 16;
    public const int NonceBytes = 32;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(1);

    public sealed class Account
    {
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Key { get; set; } = "";
        public int Iterations { get; set; } = AccountBook.Iterations;
        public DateTimeOffset Created { get; set; }
    }

    private sealed class Failures
    {
        public int Count;
        public DateTimeOffset LockedUntil;
    }

    private readonly Dictionary<string, Failures> _failures = new(StringComparer.Ordinal);

    public SortedDictionary<string, Account> Accounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Where the book is saved (null: kept in memory only, e.g. in tests).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Path { get; private set; }

    public static AccountBook Load(string path)
    {
        var book = File.Exists(path)
            ? JsonSerializer.Deserialize<AccountBook>(File.ReadAllText(path)) ?? new AccountBook()
            : new AccountBook();
        book.Accounts = new SortedDictionary<string, Account>(book.Accounts, StringComparer.Ordinal);
        book.Path = path;
        return book;
    }

    public void Save()
    {
        if (Path == null) return;
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Path, overwrite: true);
    }

    public bool Has(string name) => Accounts.ContainsKey(name);

    public bool Forget(string name)
    {
        bool removed = Accounts.Remove(name);
        _failures.Remove(name);
        if (removed) Save();
        return removed;
    }

    public static byte[] NewRandom(int bytes) => RandomNumberGenerator.GetBytes(bytes);

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    public static byte[] Proof(byte[] key, byte[] nonce) => HMACSHA256.HashData(key, nonce);

    /// <summary>Registers a name with the key its player derived (base64). False if taken or malformed.</summary>
    public bool Register(string name, byte[] salt, string keyBase64, DateTimeOffset now)
    {
        if (Has(name) || !TryDecode(keyBase64, KeyBytes, out _)) return false;
        Accounts[name] = new Account { Name = name, Salt = Convert.ToBase64String(salt), Key = keyBase64, Created = now };
        Save();
        return true;
    }

    /// <summary>Whether a proof answers the nonce for this account. Counts failures towards a lockout.</summary>
    public bool Verify(string name, byte[] nonce, string proofBase64, DateTimeOffset now)
    {
        if (!Accounts.TryGetValue(name, out var account) || IsLocked(name, now)) return false;
        bool ok = TryDecode(proofBase64, KeyBytes, out var proof) &&
                  CryptographicOperations.FixedTimeEquals(proof, Proof(Convert.FromBase64String(account.Key), nonce));
        if (ok)
        {
            _failures.Remove(name);
            return true;
        }
        var f = _failures.TryGetValue(name, out var existing) ? existing : _failures[name] = new Failures();
        if (++f.Count >= MaxFailures)
        {
            f.LockedUntil = now + LockoutTime;
            f.Count = 0;
        }
        return false;
    }

    public bool IsLocked(string name, DateTimeOffset now) => _failures.TryGetValue(name, out var f) && f.LockedUntil > now;

    private static bool TryDecode(string base64, int length, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }
        return bytes.Length == length;
    }
}

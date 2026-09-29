using System.Security.Cryptography;
using System.Text;

namespace MediaSorter.Engine;

/// <summary>One bank row of the support section.</summary>
public sealed record SupportBank(string Bank, string Number);

/// <summary>
/// Loads the support details (bank account numbers) for the support section.
///
/// The real text is never stored in plain form, neither in the binary nor in the
/// source. It is scrambled with a repeating XOR key and sealed with a SHA-256
/// checksum of the clear text. The section only renders when the seal matches, so
/// the usual "swap in my own account number and re-upload the exe" trick breaks
/// the checksum and hides the whole section instead of showing the attacker's
/// number. Rebuilding from public source is still possible for anyone; this
/// blocks patching the distributed executable, which is the realistic threat.
/// </summary>
public static class SupportInfo
{
    /// <summary>Scrambled details: Base64 of the XOR'd UTF-8 text.</summary>
    internal const string Payload =
        "HRcLHwg3GgFUJxNAGAlGRV9HTBZPCHRcbigCMAoBB0UwTx0eDEFZS0wXTwd0VFQ=";

    /// <summary>SHA-256 of the clear text, Base64 encoded. Fails closed when tampered.</summary>
    internal const string Seal = "HeDyesb2yZFi3AX5shx1cZemPfbqj0Nop2cUwXYDWl4=";

    internal const string ScrambleKey = "MediaSorter.support.v1";

    /// <summary>Bank rows, or null when the payload fails verification.</summary>
    public static IReadOnlyList<SupportBank>? Load() => Decode(Payload, Seal, ScrambleKey);

    /// <summary>Decode and verify an arbitrary payload. Returns null on any failure.</summary>
    public static IReadOnlyList<SupportBank>? Decode(string payload, string seal, string key)
    {
        try
        {
            var scrambled = Convert.FromBase64String(payload);
            var keyBytes = Encoding.UTF8.GetBytes(key);

            if (keyBytes.Length == 0 || scrambled.Length == 0)
                return null;

            var clear = new byte[scrambled.Length];
            for (var i = 0; i < scrambled.Length; i++)
                clear[i] = (byte)(scrambled[i] ^ keyBytes[i % keyBytes.Length]);

            var text = Encoding.UTF8.GetString(clear);
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

            if (!string.Equals(hash, seal, StringComparison.Ordinal))
                return null; // tampered: refuse to show anything

            return Parse(text);
        }
        catch
        {
            return null; // malformed input: fail closed
        }
    }

    /// <summary>
    /// Clear text layout: one "Bank name|account number" per line. Rows that
    /// don't look like a real account number are dropped rather than shown.
    /// </summary>
    private static IReadOnlyList<SupportBank>? Parse(string text)
    {
        var banks = new List<SupportBank>();

        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2)
                continue;

            var bank = parts[0].Trim();
            var number = parts[1].Trim();

            if (bank.Length == 0 || !IsAccountNumber(number))
                continue;

            banks.Add(new SupportBank(bank, number));
        }

        return banks.Count == 0 ? null : banks;
    }

    private static bool IsAccountNumber(string value)
    {
        if (value.Length is < 10 or > 11)
            return false;

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }
}

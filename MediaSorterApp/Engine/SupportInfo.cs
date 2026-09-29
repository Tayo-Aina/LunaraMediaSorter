using System.Security.Cryptography;
using System.Text;

namespace MediaSorter.Engine;

/// <summary>One bank row of the support section.</summary>
public sealed record SupportBank(string Bank, string Number);

/// <summary>The support details shown by the in-app support section.</summary>
public sealed record SupportData(string AccountName, IReadOnlyList<SupportBank> Banks);

/// <summary>
/// Loads the support details (account name and bank account numbers).
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
        "ARAKCBMyZSIGCgRHFwADUC0TGkUKBxt8QFZZS1h0XG4oAjAKAQdFME8dHgxBWUtMF08HF31F";

    /// <summary>SHA-256 of the clear text, Base64 encoded. Fails closed when tampered.</summary>
    internal const string Seal = "3iCW6+U5yZY5lCA/63DFKrH3+bVCenaDbtrt7iPntOA=";

    internal const string ScrambleKey = "MediaSorter.support.v1.Lunara";

    /// <summary>Decoded details, or null when the payload fails verification.</summary>
    public static SupportData? Load() => Decode(Payload, Seal, ScrambleKey);

    /// <summary>Decode and verify an arbitrary payload. Returns null on any failure.</summary>
    public static SupportData? Decode(string payload, string seal, string key)
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
    /// Clear text layout: line 1 is the account name, every following line is
    /// "Bank name|account number". Rows that don't look like a real account
    /// number are dropped rather than shown.
    /// </summary>
    private static SupportData? Parse(string text)
    {
        var lines = text.Split('\n');

        var name = lines.Length > 0 ? lines[0].Trim() : "";
        if (name.Length == 0)
            return null;

        var banks = new List<SupportBank>();

        for (var i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split('|', 2);
            if (parts.Length != 2)
                continue;

            var bank = parts[0].Trim();
            var number = parts[1].Trim();

            if (bank.Length == 0 || !IsAccountNumber(number))
                continue;

            banks.Add(new SupportBank(bank, number));
        }

        return banks.Count == 0 ? null : new SupportData(name, banks);
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

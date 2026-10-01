using System.Security.Cryptography;
using System.Text;

namespace MediaSorter.Engine;

/// <summary>One bank row of the support section.</summary>
public sealed record SupportBank(string Bank, string Number);

/// <summary>The support details shown by the support section (banks plus the source link).</summary>
public sealed record SupportData(IReadOnlyList<SupportBank> Banks, string? RepoUrl);

/// <summary>
/// Loads the support details (bank account numbers and the source repository link).
///
/// The real text is never stored in plain form, neither in the binary nor in the
/// source. It is scrambled with a repeating XOR key and sealed with a SHA-256
/// checksum of the clear text. The section only renders when the seal matches, so
/// the usual "swap in my own account number and re-upload the exe" trick breaks
/// the checksum and hides the whole section instead of showing the attacker's
/// number. The repo link is checked a second time against a fixed path in code,
/// so even a payload that passes the seal can only ever point at our repository.
/// Rebuilding from public source is still possible for anyone; this blocks
/// patching the distributed executable, which is the realistic threat.
/// </summary>
public static class SupportInfo
{
    /// <summary>Scrambled details: Base64 of the XOR'd UTF-8 text.</summary>
    internal const string Payload =
        "HRcLHwg3GgFUJxNAGAlGRV9HTBZPCHRcbigCMAoBB0UwTx0eDEFZS0wXTwd0VFRjEzYfHQgNBloDBkpfQBUdWh5EL0sHBgx8OxMNCl9vGhsRXyMHGk8EUAAAAAAAAAAAAAAA";

    /// <summary>SHA-256 of the clear text, Base64 encoded. Fails closed when tampered.</summary>
    internal const string Seal = "HTqUtK54rRMmKqCUbTH96T4tmvtNcOEIZNBMVaCopWk=";

    internal const string ScrambleKey = "MediaSorter.support.v1";

    /// <summary>
    /// The only repository path the section will ever open, checked in code on top
    /// of the seal. If the project is renamed or moved, this and the payload both
    /// need updating (regenerate the payload, then adjust this path).
    /// </summary>
    private const string TrustedRepoPath = "/Tayo-Aina/LunaraMediaSorter";

    /// <summary>Bank rows and source link, or null when the payload fails verification.</summary>
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
    /// Clear text layout: one "Bank name|account number" per line, plus an optional
    /// "repo|url" line. Rows that don't look like a real account number are dropped
    /// rather than shown; a repo line that doesn't point at our repository hides
    /// the whole section, because it can only get there through a rebuilt payload.
    /// </summary>
    private static SupportData? Parse(string text)
    {
        var banks = new List<SupportBank>();
        string? repo = null;

        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2)
                continue;

            var label = parts[0].Trim();
            var value = parts[1].Trim();

            if (string.Equals(label, "repo", StringComparison.Ordinal))
            {
                if (!IsTrustedRepoUrl(value))
                    return null; // a link to anywhere else means the payload was rebuilt

                repo = value;
                continue;
            }

            if (label.Length == 0 || !IsAccountNumber(value))
                continue;

            banks.Add(new SupportBank(label, value));
        }

        return banks.Count == 0 ? null : new SupportData(banks, repo);
    }

    /// <summary>The link must be a plain https URL on our own repository, nothing else.</summary>
    private static bool IsTrustedRepoUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath.TrimEnd('/'), TrustedRepoPath, StringComparison.OrdinalIgnoreCase);

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

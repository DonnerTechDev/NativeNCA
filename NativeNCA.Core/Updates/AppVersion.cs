using System.Reflection;

namespace NativeNCA.Core.Updates;

/// <summary>
/// Semantic version as used in release tags (<c>v0.2.0</c>, <c>v1.0.0-beta.1</c>).
/// Build metadata after <c>+</c> (the SDK appends the commit) is ignored, as SemVer requires.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Pre-release part without the dash (<c>beta.1</c>), empty for a release.</summary>
    public string PreRelease { get; }

    public bool IsPreRelease => PreRelease.Length > 0;

    private AppVersion(int major, int minor, int patch, string preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version! : throw new FormatException($"Cannot parse version \"{text}\"");

    /// <summary>Accepts an optional leading <c>v</c>, a missing patch (<c>1.2</c>) and build metadata.</summary>
    public static bool TryParse(string? text, out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string preRelease = string.Empty;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = s[(dash + 1)..];
            s = s[..dash];
            if (preRelease.Length == 0 || preRelease.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return false;
        }

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3) return false;

        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i]))
                return false;
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>Version of the running NativeNCA, from the informational version CI stamps from the tag.</summary>
    public static AppVersion FromAssembly(Assembly assembly)
    {
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(info, out var version)) return version!;

        var v = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new AppVersion(v.Major, v.Minor, Math.Max(0, v.Build), string.Empty);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release is newer than any of its pre-releases.
        if (!IsPreRelease || !other.IsPreRelease)
            return other.IsPreRelease.CompareTo(IsPreRelease);

        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNum = int.TryParse(a[i], out int an) && a[i].All(char.IsAsciiDigit);
            bool bNum = int.TryParse(b[i], out int bn) && b[i].All(char.IsAsciiDigit);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,   // numeric identifiers sort before alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i])
            };
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}

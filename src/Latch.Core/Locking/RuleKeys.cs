using System.Security.Cryptography;
using System.Text;
using Latch.Core.Contract;

namespace Latch.Core.Locking;

/// <summary>Stable ids for apps: the same target always gets the same key, which is also its rule id and icon key.</summary>
public static class RuleKeys
{
    public static string For(RuleKind kind, string target)
    {
        var normalized = kind == RuleKind.Exe ? "exe:" + RuleMatcher.NormalizePath(target) : "pkg:" + target.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}

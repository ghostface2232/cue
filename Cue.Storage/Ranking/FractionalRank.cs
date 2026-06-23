namespace Cue.Storage.Ranking;

/// <summary>
/// A LexoRank-style fractional string rank generator. Produces opaque keys that sort
/// <b>lexicographically</b> (ordinal / byte order — the same collation SQLite's default
/// <c>BINARY</c> uses for <c>ORDER BY sort_order</c>), with the defining property that a new key
/// can always be generated strictly between any two neighbors without renumbering either of them.
/// </summary>
/// <remarks>
/// This is a faithful port of the well-known <i>fractional indexing</i> algorithm (the same family
/// Figma and Jira's LexoRank use): every key is an <i>integer header</i> (which keeps common
/// inserts short and bounded) followed by an optional fractional tail that only grows when items
/// are repeatedly inserted into the same gap. Keys never carry a trailing zero, which is what keeps
/// "between" reversible and the ordering total.
/// <para>
/// It is a <b>pure</b> helper: no clock, no IO, no domain knowledge. Rank <i>assignment</i> (which
/// record gets which key, and when to rebalance) is the rank service / store's job, per the
/// architecture — the domain only ever holds the resulting string.
/// </para>
/// <para>
/// All comparisons here are <see cref="StringComparison.Ordinal"/> on purpose: the keys must order
/// identically in C# memory and in the SQLite index, and the digit alphabet is arranged in strictly
/// ascending ASCII order so character order equals digit order.
/// </para>
/// </remarks>
public static class FractionalRank
{
    /// <summary>Base-62 digit alphabet, ascending in ASCII so lexical order equals digit order.</summary>
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>The smallest legal integer header: 'A' followed by 26 zero digits.</summary>
    private static readonly string SmallestInteger = "A" + new string('0', 26);

    /// <summary>
    /// Generates a key that sorts strictly between <paramref name="before"/> and
    /// <paramref name="after"/>. Pass <c>null</c> for an open end: <paramref name="before"/> null
    /// means "before everything" (prepend), <paramref name="after"/> null means "after everything"
    /// (append). With both null it returns the canonical first key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// If either bound is malformed, or <paramref name="before"/> is not strictly less than
    /// <paramref name="after"/>.
    /// </exception>
    public static string Between(string? before, string? after)
    {
        if (before is not null) ValidateOrderKey(before);
        if (after is not null) ValidateOrderKey(after);
        if (before is not null && after is not null && string.CompareOrdinal(before, after) >= 0)
            throw new ArgumentException($"Order keys out of order: '{before}' >= '{after}'.");

        if (before is null)
        {
            if (after is null) return "a" + Digits[0]; // "a0"
            var ib = IntegerPart(after);
            var fb = after[ib.Length..];
            if (string.Equals(ib, SmallestInteger, StringComparison.Ordinal))
                return ib + Midpoint("", fb);
            if (string.CompareOrdinal(ib, after) < 0) return ib;
            var dec = DecrementInteger(ib)
                ?? throw new ArgumentException("Cannot generate a key below the smallest possible key.");
            return dec;
        }

        if (after is null)
        {
            var ia = IntegerPart(before);
            var fa = before[ia.Length..];
            var inc = IncrementInteger(ia);
            return inc is null ? ia + Midpoint(fa, null) : inc;
        }

        var headA = IntegerPart(before);
        var fracA = before[headA.Length..];
        var headB = IntegerPart(after);
        var fracB = after[headB.Length..];
        if (string.Equals(headA, headB, StringComparison.Ordinal))
            return headA + Midpoint(fracA, fracB);

        var incA = IncrementInteger(headA)
            ?? throw new ArgumentException("Cannot increment the integer header any further.");
        return string.CompareOrdinal(incA, after) < 0 ? incA : headA + Midpoint(fracA, null);
    }

    /// <summary>
    /// Generates <paramref name="count"/> keys, in ascending order, evenly distributed strictly
    /// between <paramref name="before"/> and <paramref name="after"/> (each may be <c>null</c> for an
    /// open end). Used by the rebalance safety net to re-rank a whole list at once.
    /// </summary>
    public static IReadOnlyList<string> EvenlyBetween(string? before, string? after, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return Array.Empty<string>();
        if (count == 1) return new[] { Between(before, after) };

        if (after is null)
        {
            var result = new List<string>(count);
            var cursor = Between(before, null);
            result.Add(cursor);
            for (var i = 0; i < count - 1; i++)
            {
                cursor = Between(cursor, null);
                result.Add(cursor);
            }
            return result;
        }

        if (before is null)
        {
            var result = new List<string>(count);
            var cursor = Between(null, after);
            result.Add(cursor);
            for (var i = 0; i < count - 1; i++)
            {
                cursor = Between(null, cursor);
                result.Add(cursor);
            }
            result.Reverse();
            return result;
        }

        var mid = count / 2;
        var middle = Between(before, after);
        var keys = new List<string>(count);
        keys.AddRange(EvenlyBetween(before, middle, mid));
        keys.Add(middle);
        keys.AddRange(EvenlyBetween(middle, after, count - mid - 1));
        return keys;
    }

    // ---- Internals (faithful port of the reference algorithm) ----------------

    private static string Midpoint(string a, string? b)
    {
        var zero = Digits[0];
        if (b is not null && string.CompareOrdinal(a, b) >= 0)
            throw new ArgumentException($"Midpoint bounds out of order: '{a}' >= '{b}'.");
        if ((a.Length > 0 && a[^1] == zero) || (b is { Length: > 0 } && b[^1] == zero))
            throw new ArgumentException("Order key fraction must not have a trailing zero.");

        if (b is not null)
        {
            // Keep the longest common prefix; recurse on the remainder, padding `a` with zeros.
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : zero) == b[n]) n++;
            if (n > 0)
                return b[..n] + Midpoint(n < a.Length ? a[n..] : "", b[n..]);
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Digits.Length;
        if (digitB - digitA > 1)
        {
            var midDigit = (int)Math.Round(0.5 * (digitA + digitB), MidpointRounding.AwayFromZero);
            return Digits[midDigit].ToString();
        }

        if (b is { Length: > 1 })
            return b[..1];

        // `b` is null or a single digit: descend into `a`'s fraction (or pad with the max gap).
        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : "", null);
    }

    private static int IntegerLength(char head) => head switch
    {
        >= 'a' and <= 'z' => head - 'a' + 2,
        >= 'A' and <= 'Z' => 'Z' - head + 2,
        _ => throw new ArgumentException($"Invalid order-key header digit: '{head}'."),
    };

    private static string IntegerPart(string key)
    {
        if (key.Length == 0) throw new ArgumentException("Order key must not be empty.");
        var length = IntegerLength(key[0]);
        if (length > key.Length) throw new ArgumentException($"Invalid order key: '{key}'.");
        return key[..length];
    }

    private static void ValidateInteger(string value)
    {
        if (value.Length != IntegerLength(value[0]))
            throw new ArgumentException($"Invalid integer part of order key: '{value}'.");
    }

    private static void ValidateOrderKey(string key)
    {
        if (string.Equals(key, SmallestInteger, StringComparison.Ordinal))
            throw new ArgumentException($"Invalid order key (reserved smallest): '{key}'.");
        var head = IntegerPart(key);
        var frac = key[head.Length..];
        if (frac.Length > 0 && frac[^1] == Digits[0])
            throw new ArgumentException($"Order key fraction must not have a trailing zero: '{key}'.");
    }

    private static string? IncrementInteger(string value)
    {
        ValidateInteger(value);
        var head = value[0];
        var digs = value[1..].ToCharArray();
        var carry = true;
        for (var i = digs.Length - 1; carry && i >= 0; i--)
        {
            var d = Digits.IndexOf(digs[i]) + 1;
            if (d == Digits.Length) { digs[i] = Digits[0]; }
            else { digs[i] = Digits[d]; carry = false; }
        }
        if (!carry) return head + new string(digs);

        if (head == 'Z') return "a" + Digits[0];
        if (head == 'z') return null;
        var h = (char)(head + 1);
        // Above 'a' the integer headers grow by one digit; up through 'Z' they shrink by one.
        var tail = h > 'a' ? new string(digs) + Digits[0] : new string(digs[..^1]);
        return h + tail;
    }

    private static string? DecrementInteger(string value)
    {
        ValidateInteger(value);
        var head = value[0];
        var digs = value[1..].ToCharArray();
        var borrow = true;
        for (var i = digs.Length - 1; borrow && i >= 0; i--)
        {
            var d = Digits.IndexOf(digs[i]) - 1;
            if (d == -1) { digs[i] = Digits[^1]; }
            else { digs[i] = Digits[d]; borrow = false; }
        }
        if (!borrow) return head + new string(digs);

        if (head == 'a') return "Z" + Digits[^1];
        if (head == 'A') return null;
        var h = (char)(head - 1);
        // Below 'Z' the integer headers grow by one digit; at/above 'Z' (the 'a'..'z' run) they shrink.
        var tail = h < 'Z' ? new string(digs) + Digits[^1] : new string(digs[..^1]);
        return h + tail;
    }
}

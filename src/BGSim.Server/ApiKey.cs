using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace BGSim.Server;

/// <summary>The optional shared key of the /v1 endpoints: <c>X-API-Key: &lt;key&gt;</c> or <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
internal sealed class ApiKey(string key)
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(key);

    public bool Accepts(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-API-Key", out var header) && header.Count == 1)
        {
            return Matches(header);
        }

        if (request.Headers.Authorization is { Count: 1 } authorization
            && authorization.ToString() is var value
            && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Matches(value["Bearer ".Length..].Trim());
        }

        return false;
    }

    private bool Matches(StringValues given) => FixedTimeEquals(Encoding.UTF8.GetBytes(given.ToString()), _expected);

    /// <summary>
    /// Compares in time that depends only on the expected key's length, not on where the first difference is or on the
    /// given key's length.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool FixedTimeEquals(ReadOnlySpan<byte> given, ReadOnlySpan<byte> expected)
    {
        var difference = given.Length ^ expected.Length;
        for (var i = 0; i < expected.Length; i++)
        {
            difference |= expected[i] ^ (i < given.Length ? given[i] : 0);
        }

        return difference == 0;
    }
}

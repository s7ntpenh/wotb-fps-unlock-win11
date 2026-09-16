// Sig.cs - wildcard byte-signature search, fast enough for a 70 MB image.
// Pattern entries are 0..255 for a literal byte, or any negative value for "any byte".

using System.Collections.Generic;

public static class BlitzSig
{
    public static int[] Find(byte[] buffer, int[] pattern, int maxHits)
    {
        var hits = new List<int>();
        if (pattern.Length == 0 || buffer.Length < pattern.Length) return hits.ToArray();

        int last = buffer.Length - pattern.Length;
        int first = pattern[0];

        for (int i = 0; i <= last; i++)
        {
            if (first >= 0 && buffer[i] != (byte)first) continue;
            int j = 1;
            for (; j < pattern.Length; j++)
            {
                int p = pattern[j];
                if (p >= 0 && buffer[i + j] != (byte)p) break;
            }
            if (j != pattern.Length) continue;
            hits.Add(i);
            if (hits.Count >= maxHits) break;
        }
        return hits.ToArray();
    }
}

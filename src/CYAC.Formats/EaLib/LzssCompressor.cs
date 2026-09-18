namespace CYAC.Formats.EaLib;

/// <summary>
/// LZSS <b>compressor</b> for EALIB flag=0x01 assets — the inverse of
/// <see cref="Lzss"/> (decoder reference: image@0x2FA64 in the L1 image).
///
/// <para><b>Status: EXACT.</b>  <see cref="Compress"/> reproduces EA's shipping
/// byte stream for <b>138 / 138</b> compressed assets across all six archives
/// (proved by <c>--selftest-editor</c> stage B).  The encoder is therefore not
/// merely "a" valid encoder but a reconstruction of the one EA used, which is
/// what lets the editor re-emit a modified asset without importing an unrelated
/// encoder's idea of the same data.</para>
///
/// <para><b>How the encoder was recovered</b> (black-box: a token census
/// over every match token in all 138 shipping streams, no external source):</para>
/// <list type="number">
/// <item>Decode each stream back into its (literal | match(distance, length))
/// token sequence, then, at every token position, compute the best match
/// achievable by exhaustive search.</item>
/// <item>EA's chosen length equals that maximum at <b>every</b> token, and EA
/// never emits a literal where a length&gt;=3 match exists — once the search is
/// restricted to distance &lt;= 4078.  So the encoder is plain GREEDY.</item>
/// <item>The largest distance EA ever emits is exactly <b>4078 = N - F</b>
/// (<see cref="MaxDistance"/>) — the classic ring-buffer bound where the F-byte
/// lookahead occupies the newest slots.  The single scenario.bin position that
/// looked non-greedy needed distance 4093, i.e. just outside it.</item>
/// <item>That left only the tie-break among equally long matches (33,006 tied
/// decisions).  A plain "oldest" rule agrees 78.6% of the time and "newest"
/// 8.3% — i.e. the choice is a side effect of the encoder's SEARCH STRUCTURE.
/// Modelling that structure as the textbook LZSS binary-search-tree match
/// finder (one BST node per ring slot, keyed on the string starting there,
/// inserted/deleted as the window slides) reproduces EA's token choice
/// everywhere: 138/138 byte-identical.</item>
/// </list>
///
/// <para><b>Format constants</b> (all independently confirmed by the census):
/// ring N = 4096 pre-filled with 0x20; lookahead F = 18; first write position
/// r = N - F = 0x0FEE; match length 3..18 stored as <c>len - 3</c> in the low
/// nibble; 12-bit ring position stored as <c>lo8</c> + high nibble; control
/// byte = 8 flags, LSB first, 1 = literal, 0 = match; no end marker (the
/// decoder stops on the declared size, so a trailing partial control group is
/// legal and EA emits one).</para>
/// </summary>
public static class LzssCompressor
{
    public const int WindowSize = 4096;                  // N
    public const int MaxMatch = 18;                      // F
    public const int MinMatch = 3;                       // THRESHOLD + 1
    public const byte FillByte = 0x20;
    public const int InitPos = WindowSize - MaxMatch;    // 0x0FEE

    /// <summary>
    /// Longest back-reference distance the encoder emits: 4078 = N - F.  The
    /// decoder would accept 1..4095, but EA never exceeds this (census maximum
    /// = 4078 exactly), because the sliding window only ever holds N - F bytes
    /// of searchable history.
    /// </summary>
    public const int MaxDistance = InitPos;

    /// <summary>
    /// Upper bound on hash-chain candidates inspected per position by the
    /// <see cref="Policy.GreedyFarthest"/> / <see cref="Policy.GreedyNearest"/>
    /// diagnostic encoders.  Not used by <see cref="Policy.EaExact"/>.
    /// </summary>
    public const int MaxChain = 4096;

    /// <summary>Encoder variant.  Production code should always use <see cref="EaExact"/>.</summary>
    public enum Policy
    {
        /// <summary>
        /// The reconstructed original encoder: greedy, BST match finder.
        /// Byte-identical to EA on all 138 shipping assets.
        /// </summary>
        EaExact,
        /// <summary>Greedy hash-chain search, oldest match on ties (diagnostic).</summary>
        GreedyFarthest,
        /// <summary>Greedy hash-chain search, newest match on ties (diagnostic; fastest).</summary>
        GreedyNearest,
    }

    /// <summary>Compress to a bare LZSS stream (NO size header).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input, Policy policy = Policy.EaExact) =>
        policy == Policy.EaExact ? CompressExact(input) : CompressGreedy(input, policy);

    /// <summary>
    /// Compress to the on-disk EALIB asset framing: u32-LE decompressed size
    /// followed by the LZSS stream — the framing
    /// <see cref="Lzss.DecompressAsset"/> consumes.  (The header is FOUR bytes,
    /// verified on all 138 shipping flag=0x01 assets.)
    /// </summary>
    public static byte[] CompressAsset(ReadOnlySpan<byte> decoded, Policy policy = Policy.EaExact)
    {
        byte[] stream = Compress(decoded, policy);
        byte[] outBuf = new byte[4 + stream.Length];
        int len = decoded.Length;
        outBuf[0] = (byte)len;
        outBuf[1] = (byte)(len >> 8);
        outBuf[2] = (byte)(len >> 16);
        outBuf[3] = (byte)(len >> 24);
        stream.CopyTo(outBuf.AsSpan(4));
        return outBuf;
    }

    // =====================================================================
    //  The reconstructed original encoder.
    //
    //  State: a ring buffer `text` of N + F - 1 bytes (the extra F-1 bytes
    //  mirror the head so a match comparison can run past the wrap point), and
    //  a binary search tree over the N ring slots.  Node p is keyed by the
    //  string text[p..p+F).  256 sentinel roots (indices N+1..N+256) split the
    //  tree by first byte.  Inserting slot r walks the tree comparing against
    //  each visited node and records the DEEPEST match seen on the path — that
    //  walk is simultaneously the match search, and its path order is what
    //  decides EA's tie-breaks.
    // =====================================================================

    private const int Nil = WindowSize;

    private static byte[] CompressExact(ReadOnlySpan<byte> data)
    {
        byte[] text = new byte[WindowSize + MaxMatch - 1];
        int[] lson = new int[WindowSize + 1];
        int[] rson = new int[WindowSize + 257];
        int[] dad = new int[WindowSize + 1];

        for (int i = WindowSize + 1; i < WindowSize + 257; i++) rson[i] = Nil;
        for (int i = 0; i < WindowSize; i++) dad[i] = Nil;

        int matchPos = 0, matchLen = 0;

        // ---- BST insert; also performs the match search -------------------
        void InsertNode(int r)
        {
            int cmp = 1;
            int p = WindowSize + 1 + text[r];
            rson[r] = lson[r] = Nil;
            matchLen = 0;
            while (true)
            {
                if (cmp >= 0)
                {
                    if (rson[p] != Nil) p = rson[p];
                    else { rson[p] = r; dad[r] = p; return; }
                }
                else
                {
                    if (lson[p] != Nil) p = lson[p];
                    else { lson[p] = r; dad[r] = p; return; }
                }
                int i = 1;
                for (; i < MaxMatch; i++)
                {
                    cmp = text[r + i] - text[p + i];
                    if (cmp != 0) break;
                }
                if (i > matchLen)
                {
                    matchPos = p;
                    matchLen = i;
                    if (i >= MaxMatch) break;
                }
            }
            // Full-length match: node r takes node p's place in the tree.
            dad[r] = dad[p]; lson[r] = lson[p]; rson[r] = rson[p];
            dad[lson[p]] = r; dad[rson[p]] = r;
            if (rson[dad[p]] == p) rson[dad[p]] = r; else lson[dad[p]] = r;
            dad[p] = Nil;
        }

        // ---- BST delete (slot p just fell out of the window) --------------
        void DeleteNode(int p)
        {
            if (dad[p] == Nil) return;
            int q;
            if (rson[p] == Nil) q = lson[p];
            else if (lson[p] == Nil) q = rson[p];
            else
            {
                q = lson[p];
                if (rson[q] != Nil)
                {
                    do { q = rson[q]; } while (rson[q] != Nil);
                    rson[dad[q]] = lson[q]; dad[lson[q]] = dad[q];
                    lson[q] = lson[p]; dad[lson[p]] = q;
                }
                rson[q] = rson[p]; dad[rson[p]] = q;
            }
            dad[q] = dad[p];
            if (rson[dad[p]] == p) rson[dad[p]] = q; else lson[dad[p]] = q;
            dad[p] = Nil;
        }

        List<byte> outBytes = new List<byte>(data.Length / 2 + 16);
        byte[] codeBuf = new byte[1 + MaxMatch];   // 1 control byte + at most 8 literals... 17 is the classic bound
        codeBuf[0] = 0;
        int codePtr = 1, mask = 1;

        int src = 0;
        int s = 0, r0 = WindowSize - MaxMatch;
        for (int i = s; i < r0; i++) text[i] = FillByte;

        int len = 0;
        while (len < MaxMatch && src < data.Length) text[r0 + len++] = data[src++];
        if (len == 0) return Array.Empty<byte>();

        // Seed the tree with the F slots preceding the write cursor.
        for (int i = 1; i <= MaxMatch; i++) InsertNode((r0 - i) & (WindowSize - 1));
        InsertNode(r0);

        do
        {
            if (matchLen > len) matchLen = len;
            if (matchLen < MinMatch)
            {
                matchLen = 1;
                codeBuf[0] |= (byte)mask;          // flag 1 = literal
                codeBuf[codePtr++] = text[r0];
            }
            else
            {                                       // flag 0 = match
                codeBuf[codePtr++] = (byte)(matchPos & 0xFF);
                codeBuf[codePtr++] = (byte)(((matchPos >> 4) & 0xF0) | (matchLen - MinMatch));
            }
            mask = (mask << 1) & 0xFF;
            if (mask == 0)
            {
                for (int i = 0; i < codePtr; i++) outBytes.Add(codeBuf[i]);
                codeBuf[0] = 0; codePtr = 1; mask = 1;
            }

            int lastMatchLen = matchLen;
            int k = 0;
            for (; k < lastMatchLen && src < data.Length; k++)
            {
                byte c = data[src++];
                DeleteNode(s);
                text[s] = c;
                if (s < MaxMatch - 1) text[s + WindowSize] = c;
                s = (s + 1) & (WindowSize - 1);
                r0 = (r0 + 1) & (WindowSize - 1);
                InsertNode(r0);
            }
            while (k++ < lastMatchLen)
            {
                DeleteNode(s);
                s = (s + 1) & (WindowSize - 1);
                r0 = (r0 + 1) & (WindowSize - 1);
                if (--len != 0) InsertNode(r0);
            }
        } while (len > 0);

        if (codePtr > 1)
            for (int i = 0; i < codePtr; i++) outBytes.Add(codeBuf[i]);
        return outBytes.ToArray();
    }

    // =====================================================================
    //  Diagnostic hash-chain encoder.
    //
    //  Kept because it independently validates the format model: it derives the
    //  legal token set straight from the DECODER's state machine rather than
    //  from any assumption about how EA searched, and it produces streams the
    //  decoder accepts (round-trip 138/138) at essentially EA's size.  Useful
    //  as a cross-check if the exact encoder is ever suspected.
    //
    //  Derivation: the decoder appends EVERY emitted byte to the ring, so at
    //  output position p the ring holds the last 4096 emitted bytes and a match
    //  token (q, L) reads E[p + i - d] with d = (winPos - q) mod 4096 and
    //  E[t] = 0x20 for t < 0.  That identity holds through overlap, so the
    //  format is plain LZ77 over the input virtually prefixed by 4096 spaces.
    // =====================================================================

    private static byte[] CompressGreedy(ReadOnlySpan<byte> input, Policy policy)
    {
        bool nearest = policy == Policy.GreedyNearest;
        int n = input.Length;
        byte[] buf = new byte[WindowSize + n];
        buf.AsSpan(0, WindowSize).Fill(FillByte);
        input.CopyTo(buf.AsSpan(WindowSize));

        const int HashSize = 1 << 16;
        int[] head = new int[HashSize];
        int[] prev = new int[buf.Length];
        Array.Fill(head, -1);
        Array.Fill(prev, -1);

        int inserted = 0;
        void InsertUpTo(int limit)
        {
            for (; inserted + MinMatch <= buf.Length && inserted < limit; inserted++)
            {
                int h = Hash3(buf, inserted);
                prev[inserted] = head[h];
                head[h] = inserted;
            }
        }
        InsertUpTo(WindowSize);

        List<byte> outBytes = new List<byte>(n / 2 + 16);
        int ctrlIdx = -1, ctrlBit = 8;
        void PutFlag(int bit)
        {
            if (ctrlBit == 8) { ctrlIdx = outBytes.Count; outBytes.Add(0); ctrlBit = 0; }
            if (bit != 0) outBytes[ctrlIdx] = (byte)(outBytes[ctrlIdx] | (1 << ctrlBit));
            ctrlBit++;
        }

        int p = 0;
        while (p < n)
        {
            int cur = WindowSize + p;
            InsertUpTo(cur + 1);

            int bestLen = 0, bestDist = 0;
            int maxLen = Math.Min(MaxMatch, n - p);
            if (maxLen >= MinMatch)
            {
                int minPos = Math.Max(0, cur - MaxDistance);
                int chain = MaxChain;
                for (int cand = prev[cur]; cand >= minPos && chain-- > 0; cand = prev[cand])
                {
                    int l = 0;
                    while (l < maxLen && buf[cand + l] == buf[cur + l]) l++;
                    if (l < MinMatch) continue;
                    int dist = cur - cand;
                    bool better = l > bestLen
                                  || (l == bestLen && bestLen > 0
                                      && (nearest ? dist < bestDist : dist > bestDist));
                    if (better) { bestLen = l; bestDist = dist; }
                    if (bestLen == maxLen && nearest) break;
                }
            }

            if (bestLen >= MinMatch)
            {
                int q = (InitPos + p - bestDist) & (WindowSize - 1);
                PutFlag(0);
                outBytes.Add((byte)(q & 0xFF));
                outBytes.Add((byte)(((q >> 4) & 0xF0) | (bestLen - MinMatch)));
                p += bestLen;
            }
            else
            {
                PutFlag(1);
                outBytes.Add(buf[cur]);
                p++;
            }
        }
        return outBytes.ToArray();
    }

    private static int Hash3(byte[] b, int i) =>
        ((b[i] << 8) ^ (b[i + 1] << 4) ^ b[i + 2]) & 0xFFFF;
}

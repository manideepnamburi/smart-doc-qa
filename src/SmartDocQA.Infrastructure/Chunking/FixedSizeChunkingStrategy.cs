// ── Add this to the constructor in FixedSizeChunkingStrategy.cs ──
//
// Replace this:
//
//     public FixedSizeChunkingStrategy(IOptions<ChunkingOptions> options)
//     {
//         _options = options.Value;
//     }
//
// With this:

    public FixedSizeChunkingStrategy(IOptions<ChunkingOptions> options)
    {
        _options = options.Value;

        // Guard against an infinite loop: if Overlap >= TokenSize, the sliding
        // window in ChunkAsync never advances (start += TokenSize - Overlap
        // becomes zero or negative), and ingestion hangs forever on the first
        // non-empty page. Fail fast at startup instead of hanging mid-request.
        if (_options.Overlap >= _options.TokenSize)
        {
            throw new InvalidOperationException(
                $"Invalid chunking configuration: Overlap ({_options.Overlap}) must be " +
                $"less than TokenSize ({_options.TokenSize}). " +
                $"Check the Chunking section in appsettings.json — Overlap must always " +
                $"be smaller than TokenSize, or the chunker will loop forever on ingestion.");
        }
    }

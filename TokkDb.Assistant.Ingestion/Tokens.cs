namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// How many tokens a piece of text is likely to cost.
///
/// <b>An estimate, and deliberately a slight over-estimate.</b> The real number depends on the
/// model's tokeniser, and the only honest way to get it is to ask the model - which D-12's
/// budget harness does, against Ollama, for the scenarios section 6.2 names. What is wanted here
/// is different: something deterministic, with no dependency and no vocabulary file, that can
/// decide how much of a spreadsheet to include while the spreadsheet is being read.
///
/// The rule approximates how a byte-pair tokeniser behaves rather than dividing by four: a run
/// of letters or digits costs about one token per four characters, and everything else - a pipe,
/// a comma, a newline - is a token of its own. That is close for prose and conservative for the
/// structured text this is mostly used on, where the punctuation-heavy shapes are exactly what a
/// characters-over-four rule underestimates.
///
/// Being over rather than under is the point. A budget kept by an estimate that runs low is not
/// a budget, and the failure only shows up as a model that has quietly stopped seeing the end of
/// its prompt.
/// </summary>
public static class Tokens
{
    /// <summary>Characters of a word per token, roughly, in a byte-pair tokeniser.</summary>
    private const int CharactersPerToken = 4;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var tokens = 0;
        var run = 0;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                run++;
                continue;
            }

            if (run > 0)
            {
                tokens += (run + CharactersPerToken - 1) / CharactersPerToken;
                run = 0;
            }

            // A space usually joins the word after it rather than standing alone; everything
            // else stands alone.
            if (character is not ' ') tokens++;
        }

        if (run > 0) tokens += (run + CharactersPerToken - 1) / CharactersPerToken;

        return tokens;
    }
}

/// <summary>
/// How many prompt tokens a rendering may take, from the budgets of section 6.2.
///
/// The budgets there are per model call and cover the whole prompt - the instructions, the
/// schema digest, the output shape and the data. So the numbers here are what is left for the
/// data after the rest of the call has been paid for, which is roughly half. They are named
/// after the operation rather than given as numbers at the call site, because a number at a call
/// site is a number nobody can trace back to the requirement it came from.
/// </summary>
public sealed record RenderingBudget(int Tokens)
{
    /// <summary>
    /// Mapping (records to storage) has 3 000 for the whole call. A little under half of it is
    /// the file, leaving room for the digest of candidate collections and the shape of the answer.
    /// </summary>
    public static readonly RenderingBudget Mapping = new(1_200);

    /// <summary>
    /// Extraction (prose to records) has 4 000 for the whole call, and section 6.2 says that is
    /// "one text chunk plus an output shape". The chunk is most of it.
    /// </summary>
    public static readonly RenderingBudget Extraction = new(2_500);

    /// <summary>A structural proposal has 2 000, and one collection's worth of file within it.</summary>
    public static readonly RenderingBudget StructuralProposal = new(800);
}

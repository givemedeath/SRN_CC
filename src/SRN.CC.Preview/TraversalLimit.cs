namespace SRN.CC.Preview;

/// <summary>
/// Identifies which traversal budget excluded assets from a dependency closure.
/// </summary>
/// <remarks>
/// A budget excludes nodes rather than aborting the walk. <see cref="Count"/> and <see cref="Depth"/>
/// admit nothing further once reached, so they read as a stop; <see cref="Size"/> skips an oversized
/// node and keeps going, so smaller siblings behind it still enter the closure.
/// </remarks>
public enum TraversalLimit
{
    /// <summary>
    /// The closure completed within every budget.
    /// </summary>
    None = 0,

    /// <summary>
    /// The depth budget stopped the traversal.
    /// </summary>
    Depth = 1,

    /// <summary>
    /// The resolved-asset count budget stopped the traversal.
    /// </summary>
    Count = 2,

    /// <summary>
    /// The accumulated payload size budget stopped the traversal.
    /// </summary>
    Size = 3,
}

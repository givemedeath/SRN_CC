namespace SRN.CC.Preview;

/// <summary>
/// Identifies which traversal budget stopped a dependency closure before it completed.
/// </summary>
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

using Microsoft.Extensions.Compliance.Classification;

namespace SourceFlow;

/// <summary>
/// SourceFlow's data classification taxonomy (doc 10 §5), wired to Microsoft.Extensions.Compliance.Redaction:
/// <c>[PersonalData]</c> members are <see cref="PersonalData"/>, <c>[Secret]</c> members are <see cref="Secret"/>.
/// </summary>
public static class SourceFlowTaxonomy
{
    /// <summary>The taxonomy name.</summary>
    public const string TaxonomyName = "SourceFlow";

    /// <summary>Personal data: redacted in logs and telemetry, encrypted per subject in storage.</summary>
    public static DataClassification PersonalData { get; } = new(TaxonomyName, nameof(PersonalData));

    /// <summary>Secrets: always redacted.</summary>
    public static DataClassification Secret { get; } = new(TaxonomyName, nameof(Secret));

    /// <summary>Both classifications.</summary>
    public static DataClassificationSet All { get; } = new(PersonalData, Secret);
}

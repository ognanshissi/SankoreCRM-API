namespace Sankore.Modules.Leads.Domain;

public enum QuestionType
{
    /// <summary>Yes / No question. Score = weight when answer is "true".</summary>
    YesNo,
    /// <summary>Pick one option from a predefined list. Score = weight when any option is selected.</summary>
    SingleChoice,
    /// <summary>Pick multiple options. Score = weight when at least one option is selected.</summary>
    MultiChoice,
    /// <summary>Numeric input. Score = weight when value > 0.</summary>
    Numeric,
    /// <summary>Free text. Score = weight when any non-empty text is provided.</summary>
    Text
}

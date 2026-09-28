namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// Every enum of the Customers module (M01). Persisted as strings
/// (<c>HasConversion&lt;string&gt;()</c>) and serialized as strings over HTTP.
/// </summary>
public enum ClientType { Individual, Legal }

public enum ClientStatus { PendingKyc, Active, Suspended, KycRejected, Archived, Merged }

public enum Gender { Male, Female, Other }

public enum MaritalStatus { Single, Married, Divorced, Widowed, FreeUnion }

public enum IdentityDocumentType
{
    NationalIdCard,
    Passport,
    DriverLicense,
    ConsularCard,
    VoterCard,
    ResidencePermit,
    Other
}

public enum ContactPointType { Phone, Email, Address }

public enum RiskLevel { Unknown, Low, Medium, High }

public enum ControlType { Ownership, VotingRights, Manager, Other }

public enum RelationshipType { Spouse, Child, Dependent, Guarantor, Proxy, Parent, Sibling, Other }

public enum GroupType { SolidarityGroup, Tontine, Vsla }

public enum GroupStatus { Forming, Active, Suspended, Dissolved }

public enum GroupOfficeRole { Member, President, Treasurer, Secretary }

public enum DuplicateCandidateStatus { ToReview, Rejected, Merged }

public enum MergeRequestStatus { PendingApproval, Approved, Rejected, Executed, Cancelled }

public enum ExportJobStatus { Queued, Running, Completed, Failed }

/// <summary>Fields whose clear value is only ever revealed through an audited reveal endpoint.</summary>
public enum SensitiveField
{
    IdentityDocumentNumber,
    DateOfBirth,
    DeclaredIncome,
    Phone,
    Email,
    PostalAddress,
    RegistrationNumber,
    TaxIdNumber
}

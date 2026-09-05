namespace Sankore.Shared.Kernel;

public class ContactInfo
{
    public Address Address { get; private set; } = null!;

    public PhoneNumbers Phones { get; private set; } = null!;
    
    public string AdditionalEmail { get; private set; } = string.Empty;
    
    private ContactInfo() { }

    public static ContactInfo Create(PhoneNumbers phones)
    {
        if (phones == null) throw new ArgumentNullException(nameof(phones), "At least one phone number is required.");

        return new ContactInfo
        {
            Phones = phones,
        };
    }
}

public sealed class PhoneNumbers
{
    public PhoneNumber? HomeNumber { get; set; }
    public PhoneNumber? MobileNumber { get; set; }
    public PhoneNumber? WorkNumber { get; set; }
    
    public PhoneNumbers() {}
    
    public static PhoneNumbers Create(PhoneNumber homeNumber, PhoneNumber mobileNumber, PhoneNumber workNumber)
    {
        if (homeNumber == null && mobileNumber == null && workNumber == null)
            throw new ArgumentNullException(nameof(homeNumber), "At least one phone number is required.");
        
        return new PhoneNumbers()
        {
            HomeNumber = homeNumber,
            MobileNumber = mobileNumber,
            WorkNumber = workNumber
        };
    }
}

public sealed record PhoneNumber
{
    public string Contact { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; } 
    
    private PhoneNumber() {}

    public static PhoneNumber Create(string contact)
    {
        if (string.IsNullOrWhiteSpace(contact)) throw new ArgumentNullException(nameof(contact), "Contact cannot be null or empty.");

        return new PhoneNumber
        {
            Contact = contact,
            IsPrimary = true
        };
    }

    public static PhoneNumber CreatePrimary(string contact) =>
        Create(contact) with { IsPrimary = true };
}
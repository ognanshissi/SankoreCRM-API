namespace Sankore.Shared.Infrastructure.Persistence;

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// One mapping for <see cref="PersonIdentity"/> and <see cref="PepStatus"/>, shared by every module
/// that stores a person (BNF-B-02).
///
/// <para>
/// <b>Why here and not in each module.</b> The value objects are in the Kernel, which may not
/// reference EF Core; the modules may not reference each other. Without a shared mapping, M13's
/// household and M01's household would each declare their own columns — and the column list is
/// exactly what a conversion snapshot has to line up field for field. Each module still passes its
/// OWN <see cref="IFieldEncryptor"/>, so the shape is shared and the keys are not: a lead's birth
/// date stays undecryptable with M01's key.
/// </para>
///
/// <para>
/// Column names are explicit and unprefixed by default (<c>last_name</c>, <c>birth_date_enc</c>,
/// <c>pep_function</c>…) rather than left to EF's owned-type convention, which would produce
/// <c>identity_last_name</c>. They match the DDL of the specification, and the <c>_enc</c> suffix
/// is a deliberate signal: somebody reading the table in psql can see which columns hold
/// ciphertext without having to find the mapping.
/// </para>
/// </summary>
public static class PersonMapping
{
    /// <summary>
    /// Maps <see cref="PersonIdentity"/> as an owned type of <typeparamref name="TOwner"/>.
    /// </summary>
    /// <param name="encryptor">
    /// The OWNING MODULE's encryptor — keyed, never the shared unkeyed one. Two modules sharing an
    /// encryptor here is the failure <c>AddFieldProtection</c> now throws to prevent.
    /// </param>
    /// <param name="prefix">
    /// Column prefix, for an entity that stores more than one person in the same row. Empty by
    /// default, which is the normal case: one person per row.
    /// </param>
    /// <param name="required">
    /// Whether the owner must have an identity. True for a household member — a member with no
    /// person is not a row worth having. False for an aggregate whose identity is collected later.
    /// </param>
    public static EntityTypeBuilder<TOwner> OwnsPersonIdentity<TOwner>(
        this EntityTypeBuilder<TOwner> builder,
        Expression<Func<TOwner, PersonIdentity?>> navigation,
        IFieldEncryptor encryptor,
        string prefix = "",
        bool required = true)
        where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(encryptor);

        builder.OwnsOne(navigation, p =>
        {
            p.Property(i => i.LastName)
                .HasColumnName($"{prefix}last_name").HasMaxLength(100).IsRequired();
            p.Property(i => i.FirstName)
                .HasColumnName($"{prefix}first_name").HasMaxLength(100).IsRequired();
            p.Property(i => i.UsageName)
                .HasColumnName($"{prefix}usage_name").HasMaxLength(100);

            // SENSITIVE. Encrypted, therefore unindexable and uncomparable in SQL — a search by
            // date of birth goes through the module's DateOfBirth blind index, not through this.
            p.Property(i => i.BirthDate)
                .HasColumnName($"{prefix}birth_date_enc")
                .HasConversion(EncryptedConverters.Date(encryptor));

            p.Property(i => i.Gender)
                .HasColumnName($"{prefix}gender")
                .HasConversion<string>().HasMaxLength(10).IsRequired();

            // SENSITIVE.
            p.Property(i => i.AddressLine)
                .HasColumnName($"{prefix}address_enc")
                .HasConversion(EncryptedConverters.Text(encryptor));

            p.Property(i => i.PostalCode)
                .HasColumnName($"{prefix}postal_code").HasMaxLength(10);
            p.Property(i => i.City)
                .HasColumnName($"{prefix}city").HasMaxLength(100);
            p.Property(i => i.CountryCode)
                .HasColumnName($"{prefix}country_code").HasMaxLength(2).IsFixedLength();

            // BNF-B-21, phase 2 France. Mapped now and written by nothing: adding a member to an
            // owned type later is a migration on every table that maps this one, and there will be
            // several by then.
            p.OwnsOne(i => i.FrenchSocialSecurity, fr =>
            {
                fr.Property(f => f.Nir)
                    .HasColumnName($"{prefix}fr_nir_enc")
                    .HasConversion(EncryptedConverters.Text(encryptor));
                fr.Property(f => f.OrganismCode)
                    .HasColumnName($"{prefix}fr_organism_code").HasMaxLength(9);
                fr.Property(f => f.AttachedToMemberId)
                    .HasColumnName($"{prefix}fr_attached_to_member_id");
            });
        });

        if (required)
            builder.Navigation(navigation).IsRequired();

        return builder;
    }

    /// <summary>
    /// Maps <see cref="PepStatus"/> as an owned type of <typeparamref name="TOwner"/>.
    ///
    /// <para>
    /// Nothing here is encrypted, and that is a decision rather than an omission: the exposure
    /// FLAG has to stay readable in SQL so dispatching can route an exposed lead to a cleared agent
    /// and so <c>HasPepExposure</c> can be recomputed in a query. The protection on the DETAIL —
    /// the function and the country, which together often name the person — is the PEP read
    /// permission, not a key.
    /// </para>
    /// </summary>
    /// <param name="required">
    /// True by default. A person's PEP status is never unknown in the domain: it is
    /// <see cref="PepStatus.NotExposed"/> until somebody declares otherwise, and a nullable
    /// navigation would let "not asked" and "not exposed" share a representation that the LBC/FT
    /// file then cannot tell apart.
    /// </param>
    public static EntityTypeBuilder<TOwner> OwnsPepStatus<TOwner>(
        this EntityTypeBuilder<TOwner> builder,
        Expression<Func<TOwner, PepStatus?>> navigation,
        string prefix = "pep_",
        bool required = true)
        where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.OwnsOne(navigation, p =>
        {
            p.Property(s => s.IsPep)
                .HasColumnName($"{prefix}is_exposed").HasDefaultValue(false).IsRequired();
            p.Property(s => s.Function)
                .HasColumnName($"{prefix}function").HasMaxLength(150);
            p.Property(s => s.CountryCode)
                .HasColumnName($"{prefix}country").HasMaxLength(2).IsFixedLength();
            p.Property(s => s.FunctionEndDate)
                .HasColumnName($"{prefix}end_date");
            p.Property(s => s.Link)
                .HasColumnName($"{prefix}link").HasConversion<string>().HasMaxLength(20);
            p.Property(s => s.OverrideReason)
                .HasColumnName($"{prefix}override_reason").HasMaxLength(300);
        });

        if (required)
            builder.Navigation(navigation).IsRequired();

        return builder;
    }
}

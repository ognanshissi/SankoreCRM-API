namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-scoped catalogue entry defining a nurturing sequence (US-M13-150).
/// Each sequence has ordered steps with a delay and email template.
/// </summary>
public sealed class NurturingSequence : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<NurturingStep> _steps = [];
    public IReadOnlyList<NurturingStep> Steps => _steps.AsReadOnly();

    private NurturingSequence() { }

    public static NurturingSequence Create(
        Guid tenantId,
        string name,
        string? description = null)
        => new()
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            Name        = name.Trim(),
            Description = description?.Trim(),
            IsActive    = true,
            CreatedAt   = DateTimeOffset.UtcNow,
        };

    public void Update(string name, string? description)
    {
        Name        = name.Trim();
        Description = description?.Trim();
    }

    public void Activate()   => IsActive = true;
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// The steps as the administrator now wants them. Enrollments keep their position: a lead that
    /// has received two e-mails receives the third of the new list next, and is done if there is none.
    /// </summary>
    public IReadOnlyList<NurturingStep> ReplaceSteps(
        IEnumerable<(TimeSpan Delay, string EmailTemplateKey, string? Subject)> steps)
    {
        _steps.Clear();
        var order = 0;
        foreach (var (delay, key, subject) in steps)
            AddStep(order++, delay, key, subject);
        return Steps;
    }

    public NurturingStep AddStep(
        int order,
        TimeSpan delayFromPrevious,
        string emailTemplateKey,
        string? subject = null)
    {
        var step = new NurturingStep(
            Guid.NewGuid(), Id, order, delayFromPrevious, emailTemplateKey, subject);
        _steps.Add(step);
        return step;
    }
}

/// <summary>
/// One step inside a nurturing sequence — defines when and what to send.
/// </summary>
public sealed class NurturingStep
{
    public Guid Id { get; private set; }
    public Guid SequenceId { get; private set; }
    public int Order { get; private set; }

    /// <summary>Delay from the previous step (or enrollment for the first step).</summary>
    public TimeSpan DelayFromPrevious { get; private set; }

    /// <summary>Email template key in M08 Notifications (e.g. "nurture.welcome").</summary>
    public string EmailTemplateKey { get; private set; } = default!;

    /// <summary>Optional subject line override.</summary>
    public string? Subject { get; private set; }

    private NurturingStep() { }

    internal NurturingStep(
        Guid id, Guid sequenceId, int order,
        TimeSpan delayFromPrevious, string emailTemplateKey, string? subject)
    {
        Id                = id;
        SequenceId        = sequenceId;
        Order             = order;
        DelayFromPrevious = delayFromPrevious;
        EmailTemplateKey  = emailTemplateKey;
        Subject           = subject;
    }
}

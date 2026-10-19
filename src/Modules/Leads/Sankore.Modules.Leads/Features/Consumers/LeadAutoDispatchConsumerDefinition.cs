namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;

/// <summary>
/// One auto-dispatch at a time per instance. An import publishes all its <c>LeadCapturedEvent</c>s
/// within a second; dispatched concurrently, every one of them read the same agent load — none of
/// the others had committed yet — and they all chose the same agent. In sequence, each dispatch
/// sees the previous assignment and the load is spread. Ten leads take a second or two longer.
/// </summary>
public sealed class LeadAutoDispatchConsumerDefinition : ConsumerDefinition<LeadAutoDispatchConsumer>
{
    public LeadAutoDispatchConsumerDefinition()
    {
        ConcurrentMessageLimit = 1;
    }
}

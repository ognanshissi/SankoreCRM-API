namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>The two services the WhatsApp handler and the bot engine share to create a prospect's lead.</summary>
internal static class WhatsAppTestServices
{
    public static WhatsAppBotFieldValues FieldValues(IAdministrationModule? admin = null, ILeadEmailIndexer? emailIndexer = null, TimeProvider? clock = null) =>
        new(emailIndexer ?? new TestLeadEmailIndexer(), admin ?? Substitute.For<IAdministrationModule>(),
            clock ?? TimeProvider.System, NullLogger<WhatsAppBotFieldValues>.Instance);

    public static WhatsAppProspects Prospects(
        LeadsDbContext db, IPhoneBlindIndexer phoneIndexer, IEventPublisher publisher, WhatsAppBotFieldValues fieldValues,
        IIntegrationModule? integration = null, TimeProvider? clock = null) =>
        new(db, phoneIndexer, integration ?? Substitute.For<IIntegrationModule>(), publisher, fieldValues,
            clock ?? TimeProvider.System, NullLogger<WhatsAppProspects>.Instance);
}

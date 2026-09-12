using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

public sealed record UpdateCompanyInfoCommand(
    string Name,
    string Description,
    string LogoUrl,
    string PrimaryColor,
    string SecondaryColor,
    Languages DefaultLanguage) : IRequest<Result>, ICommand;

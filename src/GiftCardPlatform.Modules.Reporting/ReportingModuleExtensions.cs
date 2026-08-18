using GiftCardPlatform.Modules.Reporting.Application;
using GiftCardPlatform.Modules.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Reporting;

public static class ReportingModuleExtensions
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IFinancialReportingQuery, FinancialReportingQuery>();
        services.AddScoped<IPaymentReportingQuery, PaymentReportingQuery>();
        services.AddScoped<IOrganizationCardRegisterQuery, OrganizationCardRegisterQuery>();
        return services;
    }
}

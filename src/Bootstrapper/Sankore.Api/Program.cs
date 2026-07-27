using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Sankore.Modules.Leads;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Users;
using Sankore.Modules.Users.Domain;
using Sankore.Modules.Users.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();


// builder.AddSeqEndpoint("seq");

// ---------------------------------------------------------------------
// 1. Cross-cutting infrastructure (auth, MediatR pipeline, messaging bus)
// ---------------------------------------------------------------------

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtConfig = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtConfig["Issuer"],
            ValidAudience = jwtConfig["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtConfig["SigningKey"]!))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSankoreAuthorization(); // registers Leads.* policies (Shared.Infrastructure.Auth)

// MediatR pipeline behaviors: order matters (outermost first).
// Logging -> Validation -> Transaction -> Audit -> [Handler]
builder.Services.AddScoped(typeof(MediatR.IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddScoped(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddScoped(typeof(MediatR.IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
builder.Services.AddScoped(typeof(MediatR.IPipelineBehavior<,>), typeof(AuditBehavior<,>));

builder.Services.AddScoped<IAuditWriter, Sankore.Api.Infrastructure.SqlAuditWriter>();

// Message bus (MassTransit). In-memory transport by default for local dev;
// swap to RabbitMQ/Kafka via configuration for staging/production without
// touching module code (OutboxProcessor<T> only depends on IBus).
builder.Services.AddMassTransit(x =>
{
    var useRabbitMq = builder.Configuration.GetValue<bool>("Messaging:UseRabbitMq");

    if (useRabbitMq)
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(builder.Configuration["Messaging:RabbitMqHost"] ?? "localhost");
        });
    }
    else
    {
        x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    }
});

builder.Services.AddEndpointsApiExplorer();

#region Swagger Generation
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("BearerToken", new ()
    {
        Name = "Authorization",
        Description = "Token-based authentication using the Bearer scheme",
        Type = SecuritySchemeType.Http,
        In = ParameterLocation.Header,
        Scheme = "Bearer"
    });
    
    options.AddSecurityDefinition("TenancyHeader", new ()
    {
        Name = "Tenant Authorization",
        Description = "Tenant-based authentication using the Tenant scheme",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Scheme = "TenantId"
    });
    
    options.AddSecurityRequirement(new()
    {
        {
            new ()
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "BearerToken"
                },
            },
            new List<string>()
        }
    });
});
#endregion


// ---------------------------------------------------------------------
// 2. Module registration — one line per module, each module owns its own
//    DbContext, handlers, validators, and endpoints internally.
// ---------------------------------------------------------------------

builder.Services.AddUsersModule(builder.Configuration);
builder.Services.AddLeadsModule(builder.Configuration);
// builder.Services.AddCustomersModule(builder.Configuration);   // M01 — same pattern
// builder.Services.AddKycModule(builder.Configuration);         // M02 — same pattern
// builder.Services.AddLoansModule(builder.Configuration);       // M04 — same pattern

var app = builder.Build();

app.MapDefaultEndpoints();

// ---------------------------------------------------------------------
// 3. Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
    var leadsDb = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
    var usersDb = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
    await leadsDb.Database.MigrateAsync();
    await usersDb.Database.MigrateAsync();
}

// ---------------------------------------------------------------------
// 4. HTTP pipeline
// ---------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    
    
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .WithTags("Health")
    .WithOpenApi()
    .AllowAnonymous();

// ---------------------------------------------------------------------
// 5. Module endpoint mapping — one line per module.
// ---------------------------------------------------------------------

app.MapLeadsEndpoints();
app.MapIdentityModuleEndpoints();
// app.MapCustomersEndpoints();
// app.MapKycEndpoints();
// app.MapLoansEndpoints();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;

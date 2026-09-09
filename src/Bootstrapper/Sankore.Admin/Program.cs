using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Sankore.Admin.Features.Tenants;
using Sankore.Admin.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDbContext<AdminDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("AdminDatabase")));

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Sankore Admin API",
        Version = "v1",
        Description = "Administration API for SankoreCRM"
    });

    options.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token."
    });

    options.AddSecurityRequirement(new()
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            []
        }
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// ---------------------------------------------------------------------
//  Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
// Audit schema — independent of all module schemas.
    var adminDbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
    await adminDbContext.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Sankore Admin API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();

app.MapTenantEndpoints();

app.Run();

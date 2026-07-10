using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using TradingApp.Business;
using TradingApp.Business.Middleware;
using TradingApp.Domain;

var builder = WebApplication.CreateBuilder(args);

var keyVaultUrl = new Uri("https://tradingapp-demo-kv.vault.azure.net/");
var credential = new DefaultAzureCredential();

var secretClient = new Azure.Security.KeyVault.Secrets.SecretClient(keyVaultUrl, credential);

builder.Configuration.AddAzureKeyVault(keyVaultUrl, credential);

var aiSecret = secretClient.GetSecret("APPLICATIONINSIGHTS-CONNECTION-STRING").Value.Value;

builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = aiSecret;
    options.EnableAdaptiveSampling = false;
});

builder.Services.AddDbContext<TradingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.RegisterBusiness();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
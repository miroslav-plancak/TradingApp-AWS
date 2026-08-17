using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Anthropic;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using TradingApp.API;
using TradingApp.API.BackgroundServices;
using TradingApp.Business;
using TradingApp.Business.Middleware;
using TradingApp.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var secretsManagerClient = new AmazonSecretsManagerClient(RegionEndpoint.EUNorth1);
var secretResponse = await secretsManagerClient.GetSecretValueAsync(new GetSecretValueRequest
{
    SecretId = "TradingApp/SqlConnectionString"
});
Environment.SetEnvironmentVariable("SQL_CONNECTION_STRING", secretResponse.SecretString);

builder.Services.AddTradingDbContext();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.RegisterBusiness();
builder.Services.AddResiliencePolicy("TradingApp.Business-repository");
//docker run -p 6379:6379 redis to run local redis instance
builder.Services.AddSignalR().AddStackExchangeRedis("localhost:6379"); 
builder.Services.AddHostedService<SignalRPushBackgroundService>();

builder.Services.AddSingleton(new AnthropicClient
{
    ApiKey = builder.Configuration["Anthropic:ApiKey"]
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder => builder
        .WithOrigins("http://localhost:4200", "https://localhost:4200")
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
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

app.MapAppHubs();

app.Run();
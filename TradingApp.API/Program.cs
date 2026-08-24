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

builder.Services.AddVoyageEmbeddingServices();
// This is a separate IConnectionMultiplexer connection from SignalR's AddStackExchangeRedis backplane
// connection above (that one is pub/sub for fanning Hub messages out across multiple API instances.
// This one is used for storing embedded float vectors to redis: ChunkIngestionService/ChunkRetrievalService
// use it to write chunk hashes and run FT.SEARCH ... KNN queries for RAG retrieval. They both currently point
// at the same local Redis Stack container (localhost:6379) but are logically unrelated connections serving
// unrelated purposes.
builder.Services.AddRedisConnection();
builder.Services.AddChunkingRetrievalService();

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
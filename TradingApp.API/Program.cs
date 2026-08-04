using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
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
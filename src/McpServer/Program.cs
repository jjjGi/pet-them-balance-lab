using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetThem.BalanceLab.Mcp;

// PET THEM! Balance Lab MCP server. Local stdio only: no network listener, no cloud service.
var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol. Anything else printed there corrupts the session.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "pet-them-balance-lab", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithTools<BalanceTools>();

await builder.Build().RunAsync();

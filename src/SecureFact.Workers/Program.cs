var builder = Host.CreateApplicationBuilder(args);

// Background services (outbox publisher, document pipeline, webhook delivery) are registered here as their phases land.
await builder.Build().RunAsync();

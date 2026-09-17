var builder = DistributedApplication.CreateBuilder(args);

var novadb = builder.AddProject<Projects.NovaDB_Server>("novadb")
    .WithEnvironment("NovaDB__Port", "6379")
    .WithEnvironment("NovaDB__HttpPort", "7380")
    .WithEnvironment("NovaDB__GrpcPort", "7381")
    .WithEnvironment("NovaDB__BindAddress", "127.0.0.1")
    .WithEnvironment("NovaDB__MemoryLimitBytes", "67108864")
    .WithEnvironment("NovaDB__EvictionPolicy", "allkeys-lru");

builder.AddProject<Projects.NovaDB_Admin>("admin")
    .WithEnvironment("NovaDB__AdminGrpc__Address", "http://127.0.0.1:7381")
    .WithEnvironment("NOVADB_ADMIN_PASSWORD", "changeme")
    .WithEnvironment("NOVADB_OPERATOR_PASSWORD", "changeme")
    .WithEnvironment("NOVADB_READONLY_PASSWORD", "changeme")
    .WaitFor(novadb);

builder.Build().Run();

var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.Maison_Rizlene_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Maison_Rizlene_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

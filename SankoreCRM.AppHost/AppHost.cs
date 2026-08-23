var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var db = postgres.AddDatabase("Database");

// Rabbitmq
var rmq = builder.AddRabbitMQ("rabbitmq");

// Seq
var seq = builder.AddSeq("seq");

// Redis — provider-resolution cache for Notifications module
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.Sankore_Api>("sankore-api")
    .WithReference(db)
    .WithReference(rmq)
    .WithReference(seq)
    .WithReference(redis)
    .WaitFor(rmq)
    .WaitFor(db)
    .WaitFor(redis);
builder.Build().Run();

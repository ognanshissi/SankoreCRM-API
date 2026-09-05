var builder = DistributedApplication.CreateBuilder(args);

// Docker compose for deployment
builder.AddDockerComposeEnvironment("env");

var pgPassword = builder.AddParameter("postgres-password", secret: true);

var postgres = builder.AddPostgres("postgres", password: pgPassword)
    .WithDockerfile("..", "postgres.Dockerfile")
    .WithHostPort(5432)
    .WithPgAdmin()
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

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var db = postgres.AddDatabase("Database");

// Rabbitmq
var rmq = builder.AddRabbitMQ("rabbitmq");

// Seq
var seq = builder.AddSeq("seq");
builder.AddProject<Projects.Sankore_Api>("sankore-api")
    .WithReference(db)
    .WithReference(rmq)
    .WithReference(seq)
    .WaitFor(rmq)
    .WaitFor(db);
builder.Build().Run();

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
var rmq = builder.AddRabbitMQ("rabbitmq")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithManagementPlugin();

// Seq
var seq = builder.AddSeq("seq");

// Redis — provider-resolution cache for Notifications module
var redis = builder.AddRedis("redis")
    .WithRedisInsight()
    .WithLifetime(ContainerLifetime.Persistent);

// ── Observability stack (US-M12-LOGS-001) ─────────────────────────────────
// Tempo — distributed traces backend (OTLP gRPC receiver on 4317, HTTP API on 3200).
// Grafana datasource: http://sankore-tempo:3200
var tempo = builder.AddContainer("tempo", "grafana/tempo", "2.5.0")
    .WithContainerName("sankore-tempo")
    .WithArgs("-config.file=/etc/tempo.yaml")
    .WithBindMount("../observability/tempo.yaml", "/etc/tempo.yaml", isReadOnly: true)
    .WithEndpoint(port: 3200, targetPort: 3200, scheme: "http", name: "tempo-http")
    .WithEndpoint(port: 4317, targetPort: 4317, scheme: "http", name: "tempo-otlp-grpc")
    .WithLifetime(ContainerLifetime.Persistent);

// Prometheus — scrapes /metrics from sankore-api (port 5000, pinned below).
// Grafana datasource: http://sankore-prometheus:9090
var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v2.52.0")
    .WithContainerName("sankore-prometheus")
    .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-lifecycle")
    .WithBindMount("../observability/prometheus.yml", "/etc/prometheus/prometheus.yml", isReadOnly: true)
    .WithEndpoint(port: 9090, targetPort: 9090, scheme: "http", name: "prometheus-http")
    .WithLifetime(ContainerLifetime.Persistent);

// Grafana — dashboards + provisioned alert rules.
// Login: admin / admin  (GF_AUTH_ANONYMOUS_ENABLED=true for local dev — no password needed)
builder.AddContainer("grafana", "grafana/grafana", "11.1.0")
    .WithContainerName("sankore-grafana")
    .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
    .WithEnvironment("GF_USERS_ALLOW_SIGN_UP", "false")
    .WithEnvironment("GF_ALERTING_ENABLED", "false")         // disable legacy alerting
    .WithEnvironment("GF_UNIFIED_ALERTING_ENABLED", "true")  // enable unified alerting
    .WithBindMount("../observability/grafana/provisioning", "/etc/grafana/provisioning", isReadOnly: true)
    .WithBindMount("../observability/grafana/dashboards", "/var/lib/grafana/dashboards", isReadOnly: true)
    .WithEndpoint(port: 3000, targetPort: 3000, scheme: "http", name: "grafana-http")
    .WaitFor(prometheus)
    .WaitFor(tempo)
    .WithLifetime(ContainerLifetime.Persistent);
// ─────────────────────────────────────────────────────────────────────────

builder.AddProject<Projects.Sankore_Api>("sankore-api")
    .WithReference(db)
    .WithReference(rmq)
    .WithReference(seq)
    .WithReference(redis)
    // Pin HTTP port so Prometheus static config (host.docker.internal:5000) is stable.
    .WithEnvironment("ASPNETCORE_HTTP_PORTS", "5000")
    // Send traces also to Tempo so they appear in Grafana (alongside Aspire Dashboard).
    .WithEnvironment("SANKORE_TEMPO_OTLP_ENDPOINT", tempo.GetEndpoint("tempo-otlp-grpc"))
    .WaitFor(rmq)
    .WaitFor(db)
    .WaitFor(redis);

builder.Build().Run();
